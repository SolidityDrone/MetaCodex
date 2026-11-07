// MetaCodex Core tests: node --test opt/metacodex/core/test/   (Node >= 22.18: TypeScript runs as is)
import { after, before, test } from "node:test";
import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { startCore } from "../src/server.ts";
import { replayDriver } from "../src/providers/replay.ts";
import { claudeDriver } from "../src/providers/claude.ts";

const here = dirname(fileURLToPath(import.meta.url));
const dir = mkdtempSync(join(tmpdir(), "mcx-core-"));
const tapes = join(dir, "tapes");
let core: Awaited<ReturnType<typeof startCore>>;
let base = "";
let project = "";

const TAPE = [
  '{"turn":true}',
  '{"wait":20,"emit":"assistant.message","data":{"text":"looking"}}',
  '{"ask":"approval","payload":{"subject":"file_change","summary":"write a.txt"},"onDeny":[{"emit":"assistant.message","data":{"text":"ok, not writing"}}]}',
  '{"wait":20,"emit":"assistant.message","data":{"text":"Done: wrote a.txt"}}',
  '{"turn":true}',
  '{"wait":20,"emit":"assistant.message","data":{"text":"second turn"}}',
].join("\n");
const SLOW = ['{"turn":true}', '{"wait":800,"emit":"assistant.message","data":{"text":"slow done"}}', '{"turn":true}', '{"wait":10,"emit":"assistant.message","data":{"text":"queued turn ran"}}'].join("\n");
const QUICK = ['{"turn":true}', '{"wait":10,"emit":"assistant.message","data":{"text":"child result 42"}}'].join("\n");

async function api(method: string, path: string, body?: unknown, auth = true): Promise<any> {
  const r = await fetch(base + path, {
    method,
    headers: { "content-type": "application/json", ...(auth ? { authorization: `Bearer ${core.token}` } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  return { status: r.status, body: await r.json() };
}

async function until<T>(f: () => Promise<T | undefined | false>, ms = 5000): Promise<T> {
  const end = Date.now() + ms;
  for (;;) {
    const v = await f();
    if (v) return v;
    if (Date.now() > end) throw new Error("timed out");
    await new Promise((r) => setTimeout(r, 25));
  }
}

const events = async (id: string) => (await api("GET", `/api/v1/threads/${id}/events`)).body as any[];
const pending = async (id: string) => (await api("GET", `/api/v1/threads/${id}`)).body.pendingInteractions as any[];
const idle = async (id: string) => {
  const t = (await api("GET", `/api/v1/threads/${id}`)).body;
  return t.status === "idle" && !t.runtimeBusy && t.queuedMessages.length === 0 ? t : undefined;
};

before(async () => {
  const { mkdirSync } = await import("node:fs");
  mkdirSync(tapes, { recursive: true });
  writeFileSync(join(tapes, "demo.jsonl"), TAPE);
  writeFileSync(join(tapes, "slow.jsonl"), SLOW);
  writeFileSync(join(tapes, "quick.jsonl"), QUICK);
  // the cloud agent driver runs against a protocol-faithful stand-in (no network, no account)
  const fake = join(dir, "fake-claude");
  writeFileSync(fake, `#!/bin/sh\nexec "${process.execPath}" "${join(here, "fake-claude.mjs")}" "$@"\n`, { mode: 0o755 });
  core = await startCore({ port: 0, dataDir: join(dir, "data"), workRoot: join(dir, "work"), drivers: [replayDriver(tapes), claudeDriver(fake)], quiet: true });
  base = `http://127.0.0.1:${core.port}`;
  project = (await api("GET", "/api/v1/projects")).body[0].id;
});

after(async () => {
  await core.close();
  rmSync(dir, { recursive: true, force: true });
});

test("auth: no token is refused", async () => {
  const r = await api("GET", "/api/v1/status", undefined, false);
  assert.equal(r.status, 401);
  assert.equal(r.body.error.code, "unauthorized");
});

test("status lists providers", async () => {
  const r = await api("GET", "/api/v1/status");
  assert.equal(r.status, 200);
  assert.equal(r.body.providers[0].id, "replay");
  assert.equal(r.body.providers[0].ok, true);
});

test("websocket pushes thread changes", async () => {
  const ws = new WebSocket(`ws://127.0.0.1:${core.port}/api/v1/ws?token=${core.token}`);
  const got: any[] = [];
  ws.onmessage = (m) => got.push(JSON.parse(String(m.data)));
  await new Promise<void>((r, j) => { ws.onopen = () => r(); ws.onerror = () => j(new Error("ws error")); });
  const t = (await api("POST", "/api/v1/threads", { projectId: project, title: "ws", provider: "replay" })).body;
  await until(async () => got.some((g) => g.threadId === t.id && g.kind === "thread.created"));
  ws.close();
  assert.equal(got.find((g) => g.threadId === t.id).type, "thread.changed");
});

test("spawn -> approval -> allow -> completed turn", async () => {
  const t = (await api("POST", "/api/v1/threads", { projectId: project, prompt: "write a.txt", provider: "replay" })).body;
  assert.match(t.id, /^thr_[a-z0-9]{10}$/);
  const [i] = await until(async () => { const p = await pending(t.id); return p.length ? p : undefined; });
  assert.equal(i.kind, "approval");
  // a message while an interaction is open queues (bb semantics) instead of failing
  const q = (await api("POST", `/api/v1/threads/${t.id}/send`, { text: "also add a test", mode: "steer" })).body;
  assert.equal(q.delivery, "queued");
  assert.equal(q.queuedMessage.waitingOn, "interaction");
  const r = (await api("POST", `/api/v1/threads/${t.id}/interactions/${i.id}/resolve`, { decision: "allow_once" })).body;
  assert.equal(r.status, "resolved");
  await until(() => idle(t.id), 8000);
  const ev = await events(t.id);
  const texts = ev.filter((e) => e.kind === "assistant.message").map((e) => e.data.text);
  assert.ok(texts.includes("Done: wrote a.txt"), texts.join(" | "));
  assert.ok(texts.includes("second turn"), "the queued message ran as the next turn");
  assert.deepEqual(ev.map((e) => e.seq), ev.map((_e, k) => k + 1), "seq is 1..n without gaps");
});

test("deny with a reason takes the recorded deny branch", async () => {
  const t = (await api("POST", "/api/v1/threads", { projectId: project, prompt: "write a.txt", provider: "replay" })).body;
  const [i] = await until(async () => { const p = await pending(t.id); return p.length ? p : undefined; });
  await api("POST", `/api/v1/threads/${t.id}/interactions/${i.id}/resolve`, { decision: "deny", reason: "not in scope" });
  await until(() => idle(t.id));
  const ev = await events(t.id);
  assert.ok(ev.some((e) => e.kind === "interaction.resolved" && e.data.reason === "not in scope"));
  assert.ok(ev.some((e) => e.kind === "assistant.message" && e.data.text === "ok, not writing"));
});

test("queue mode waits for the running turn", async () => {
  const t = (await api("POST", "/api/v1/threads", { projectId: project, prompt: "go", provider: "replay", model: "slow" })).body;
  await until(async () => (await api("GET", `/api/v1/threads/${t.id}`)).body.status === "active");
  const q = (await api("POST", `/api/v1/threads/${t.id}/send`, { text: "next", mode: "queue" })).body;
  assert.equal(q.delivery, "queued");
  assert.equal(q.queuedMessage.waitingOn, "thread-busy");
  await until(() => idle(t.id), 8000);
  const texts = (await events(t.id)).filter((e) => e.kind === "assistant.message").map((e) => e.data.text);
  assert.deepEqual(texts, ["slow done", "queued turn ran"]);
});

test("a child's finished turn notifies its parent", async () => {
  const parent = (await api("POST", "/api/v1/threads", { projectId: project, title: "manager", provider: "replay", model: "quick" })).body;
  const child = (await api("POST", "/api/v1/threads", { projectId: project, prompt: "compute", provider: "replay", model: "quick", parentThreadId: parent.id })).body;
  await until(() => idle(child.id));
  const ev = await until(async () => { const e = await events(parent.id); return e.some((x) => x.kind === "child.notice") ? e : undefined; });
  const notice = ev.find((e) => e.kind === "child.notice");
  assert.equal(notice.data.childThreadId, child.id);
  assert.equal(notice.data.outcome, "finished");
  assert.match(notice.data.excerpt, /child result 42/);
  const msg = ev.find((e) => e.kind === "user.message" && String(e.data.text).includes(child.id));
  assert.ok(msg, "the notice reached the parent as a message");
  const kids = (await api("GET", `/api/v1/threads?parentThreadId=${parent.id}`)).body;
  assert.equal(kids.length, 1);
});

test("stop interrupts a running turn and keeps the history", async () => {
  const t = (await api("POST", "/api/v1/threads", { projectId: project, prompt: "go", provider: "replay", model: "slow" })).body;
  await until(async () => (await api("GET", `/api/v1/threads/${t.id}`)).body.status === "active");
  const s = (await api("POST", `/api/v1/threads/${t.id}/stop`)).body;
  assert.equal(s.status, "idle");
  const ev = await events(t.id);
  assert.ok(ev.some((e) => e.kind === "turn.finished" && e.data.outcome === "interrupted"));
  assert.ok(ev.some((e) => e.kind === "user.message"));
});

// The CLI runs as a child process against the server in THIS process: it must be awaited (execFile),
// never execFileSync, which blocks the event loop the server needs to answer (a deadlock).
test("mcx CLI talks to the server", async () => {
  const run = promisify(execFile);
  const cli = join(here, "..", "src", "cli.ts");
  const env = { ...process.env, MCX_SERVER_URL: base, MCX_TOKEN: core.token };
  const mcx = async (...args: string[]) => (await run(process.execPath, ["--no-warnings", cli, ...args], { env, encoding: "utf8" })).stdout;
  const ps = JSON.parse(await mcx("project", "list", "--json"));
  assert.equal(ps[0].name, "scratch");
  const t = JSON.parse(await mcx("thread", "spawn", "--project", "scratch", "--prompt", "hi", "--provider", "replay", "--model", "quick", "--json"));
  assert.match(t.id, /^thr_/);
  const w = JSON.parse(await mcx("thread", "wait", t.id, "--timeout", "10", "--json"));
  assert.equal(w.state, "idle");
  assert.match(await mcx("thread", "log", t.id), /agent: child result 42/);
});

test("cloud agent driver: stream-json turns, session id, failure, exit", async () => {
  const t = (await api("POST", "/api/v1/threads", { projectId: project, prompt: "read the readme", provider: "claude-code" })).body;
  await until(() => idle(t.id), 8000);
  let ev = await events(t.id);
  const kinds = ev.map((e) => e.kind);
  for (const k of ["provider.init", "assistant.message", "tool.call", "tool.result", "usage", "turn.finished"]) assert.ok(kinds.includes(k), k);
  assert.ok(ev.some((e) => e.kind === "assistant.message" && e.data.text === `done turn 1 (${t.id})`), "thread env reaches the CLI");
  const shown = (await api("GET", `/api/v1/threads/${t.id}`)).body;
  assert.match(shown.sessionId, /^sess-/);
  // second turn goes to the same process (turn 2), a failure puts the thread in error, retry recovers
  await api("POST", `/api/v1/threads/${t.id}/send`, { text: "now FAIL please", mode: "auto" });
  await until(async () => (await api("GET", `/api/v1/threads/${t.id}`)).body.status === "error", 8000);
  await api("POST", `/api/v1/threads/${t.id}/send`, { text: "again", mode: "auto" });
  await until(() => idle(t.id), 8000);
  ev = await events(t.id);
  assert.ok(ev.some((e) => e.kind === "assistant.message" && String(e.data.text).startsWith("done turn 3")), "same process kept the turn count");
  // the process dying mid-turn fails the turn with its exit code instead of hanging
  await api("POST", `/api/v1/threads/${t.id}/send`, { text: "EXIT now", mode: "auto" });
  await until(async () => (await api("GET", `/api/v1/threads/${t.id}`)).body.status === "error", 8000);
  ev = await events(t.id);
  assert.ok(ev.some((e) => e.kind === "error" && e.data.code === "provider_exit"));
  // next message restarts the CLI with --resume <session>
  await api("POST", `/api/v1/threads/${t.id}/send`, { text: "after restart", mode: "auto" });
  await until(() => idle(t.id), 8000);
  ev = await events(t.id);
  const inits = ev.filter((e) => e.kind === "provider.init");
  assert.equal(inits.length, 2, "a new process after the exit");
});
