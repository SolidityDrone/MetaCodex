// mcx: command line for MetaCodex Core, mirroring the `bb` verbs (status, project, thread ...).
// Agents running inside a thread use it to spawn and steer other threads (MCX_THREAD_ID = --self).
import { readFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

const USAGE = `mcx: MetaCodex Core control plane
  mcx status
  mcx project list | create <name> [--path <dir>]
  mcx thread list [--project <id>] [--parent-thread <id>] [--status <s>] [--archived]
  mcx thread spawn --project <id> (--prompt <text> | --prompt-file <path|->) [--title t] [--provider p]
                    [--model m] [--parent-thread <id> | --parent-self] [--permission-mode accept-edits|auto|full]
                    [--parent-notifications auto|stuck|off]
  mcx thread show <id|--self>        mcx thread log <id|--self> [--after-seq n] [--limit n]
  mcx thread tell <id|--self> <message> | --message-file <path|->  [--mode steer|queue|auto]
  mcx thread stop|retry|archive|unarchive|delete <id|--self>
  mcx thread wait <id> [--timeout <seconds>]     (until idle, error, or waiting on an interaction)
  mcx thread interactions list <id> | approve <interaction> <id> | deny <interaction> <id> [--reason r]
                           | answer <interaction> <id> --text <questionId=text>
Every command takes --json. Server: $MCX_SERVER_URL (default http://127.0.0.1:38990), token: $MCX_TOKEN or ~/.metacodex/token.`;

const argv = process.argv.slice(2);
const json = argv.includes("--json");
const pos: string[] = [];
const flags = new Map<string, string | true>();
for (let i = 0; i < argv.length; i++) {
  const a = argv[i];
  if (a === "--json") continue;
  if (a.startsWith("--")) {
    const v = argv[i + 1];
    if (v === undefined || v.startsWith("--")) flags.set(a.slice(2), true);
    else { flags.set(a.slice(2), v); i++; }
  } else pos.push(a);
}
const flag = (n: string): string | undefined => { const v = flags.get(n); return typeof v === "string" ? v : undefined; };

const server = process.env.MCX_SERVER_URL ?? "http://127.0.0.1:38990";
function token(): string {
  if (process.env.MCX_TOKEN) return process.env.MCX_TOKEN;
  try { return readFileSync(join(homedir(), ".metacodex", "token"), "utf8").trim(); } catch { return ""; }
}

async function api(method: string, path: string, body?: unknown): Promise<any> {
  let res: Response;
  try {
    res = await fetch(server + path, {
      method,
      headers: { authorization: `Bearer ${token()}`, "content-type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch (e) {
    fail("server_unreachable", `cannot reach ${server}: ${(e as Error).message}`, "start it with: mcx-core &");
  }
  const data = await res!.json();
  if (!res!.ok || data?.ok === false) fail(data?.error?.code ?? "error", data?.error?.message ?? res!.statusText, data?.error?.hint);
  return data;
}

function fail(code: string, message: string, hint?: string): never {
  if (json) console.log(JSON.stringify({ ok: false, error: { code, message, ...(hint ? { hint } : {}) } }));
  else console.error(`error (${code}): ${message}${hint ? `\n  hint: ${hint}` : ""}`);
  process.exit(1);
}

function out(data: unknown, text: () => string): void {
  console.log(json ? JSON.stringify(data, null, 2) : text());
}

function threadId(i: number): string {
  if (flags.has("self")) {
    const id = process.env.MCX_THREAD_ID;
    if (!id) fail("no_self", "--self needs MCX_THREAD_ID (only set inside a thread)");
    return id;
  }
  const id = pos[i];
  if (!id) fail("usage", "missing thread id", "mcx thread list");
  return id;
}

function textArg(fileFlag: string, inlineFlag: string | null, posIndex: number): string {
  const f = flag(fileFlag);
  if (f) return f === "-" ? readFileSync(0, "utf8") : readFileSync(f, "utf8");
  if (inlineFlag) { const v = flag(inlineFlag); if (v) return v; }
  return pos.slice(posIndex).join(" ");
}

const row = (cells: (string | null | undefined)[], widths: number[]) =>
  cells.map((c, i) => String(c ?? "-").slice(0, widths[i]).padEnd(widths[i])).join("  ");

function describe(e: any): string {
  const d = e.data ?? {};
  switch (e.kind) {
    case "user.message": return `you: ${d.text}`;
    case "assistant.message": return `agent: ${d.text}`;
    case "tool.call": return `tool ${d.name} ${JSON.stringify(d.input ?? {}).slice(0, 120)}`;
    case "tool.result": return `tool result ${d.ok ? "ok" : "failed"}: ${String(d.summary ?? "").slice(0, 120)}`;
    case "status": return `status ${d.from} -> ${d.to}${d.reason ? ` (${d.reason})` : ""}`;
    case "interaction.requested": return `WAITING ${d.kind} ${d.interactionId}: ${JSON.stringify(d.payload).slice(0, 160)}`;
    case "interaction.resolved": return `resolved ${d.interactionId}: ${d.decision ?? d.status}${d.reason ? ` (${d.reason})` : ""}`;
    case "turn.finished": return `turn ${d.outcome}${d.error ? `: ${d.error}` : ""}`;
    case "child.notice": return `child ${d.childThreadId} ${d.outcome}`;
    default: return `${e.kind} ${JSON.stringify(d).slice(0, 160)}`;
  }
}

async function main(): Promise<void> {
  const [group, cmd] = pos;
  if (!group || group === "help" || flags.has("help")) { console.log(USAGE); return; }
  if (group === "status") {
    const s = await api("GET", "/api/v1/status");
    const self = process.env.MCX_THREAD_ID;
    return out({ ...s, thread: self ?? null }, () =>
      `mcx-core ${s.version} on ${server} (machine ${s.machine?.name})\n` +
      s.providers.map((p: any) => `  ${p.id.padEnd(12)} ${p.ok ? "available" : "unavailable"}  ${p.detail}`).join("\n") +
      (self ? `\nthread ${self} (project ${process.env.MCX_PROJECT_ID})` : ""));
  }
  if (group === "project") {
    if (cmd === "create") {
      const p = await api("POST", "/api/v1/projects", { name: pos[2], path: flag("path") });
      return out(p, () => `${p.id}  ${p.name}  ${p.path}`);
    }
    const ps = await api("GET", "/api/v1/projects");
    return out(ps, () => ps.map((p: any) => row([p.id, p.name, p.path], [14, 20, 40])).join("\n"));
  }
  if (group !== "thread") fail("usage", `unknown command ${group}`, "mcx help");
  switch (cmd) {
    case "list": {
      const q = new URLSearchParams();
      if (flag("project")) q.set("projectId", (await api("GET", `/api/v1/projects/${flag("project")}`)).id);
      if (flag("parent-thread")) q.set("parentThreadId", flag("parent-thread")!);
      if (flag("status")) q.set("status", flag("status")!);
      if (flags.has("archived")) q.set("archived", "true");
      const ts = await api("GET", `/api/v1/threads?${q}`);
      return out(ts, () => ts.map((t: any) => row([t.id, t.title, t.provider, t.status], [14, 40, 12, 9])).join("\n") || "(no threads)");
    }
    case "spawn": {
      const project = flag("project") ?? process.env.MCX_PROJECT_ID;
      if (!project) fail("usage", "--project is required");
      const prompt = textArg("prompt-file", "prompt", 2);
      if (!prompt.trim()) fail("usage", "--prompt or --prompt-file is required");
      const parent = flags.has("parent-self") ? process.env.MCX_THREAD_ID : flag("parent-thread");
      const p = await api("GET", `/api/v1/projects/${project}`);
      const t = await api("POST", "/api/v1/threads", {
        projectId: p.id, prompt, title: flag("title"), provider: flag("provider"), model: flag("model"),
        parentThreadId: parent, permissionMode: flag("permission-mode"), parentNotifications: flag("parent-notifications"),
      });
      return out(t, () => `${t.id}  ${t.title ?? ""}  (${t.provider}, ${t.status})`);
    }
    case "show": {
      const t = await api("GET", `/api/v1/threads/${threadId(2)}`);
      return out(t, () => [`${t.id}  ${t.title ?? "-"}`, `status ${t.status}  provider ${t.provider}${t.model ? " " + t.model : ""}  permissions ${t.permissionMode}`,
        t.parentThreadId ? `parent ${t.parentThreadId} (notifications ${t.parentNotifications})` : "no parent",
        `pending interactions: ${t.pendingInteractions.length}  queued: ${t.queuedMessages.length}  children: ${t.children.length}`,
        ...t.pendingInteractions.map((i: any) => `  ${i.id} ${i.kind}: ${JSON.stringify(i.payload).slice(0, 160)}`)].join("\n"));
    }
    case "log": {
      const es = await api("GET", `/api/v1/threads/${threadId(2)}/events?afterSeq=${flag("after-seq") ?? 0}&limit=${flag("limit") ?? 200}`);
      return out(es, () => es.map((e: any) => `${String(e.seq).padStart(4)} ${new Date(e.ts).toISOString().slice(11, 19)} ${describe(e)}`).join("\n"));
    }
    case "tell": {
      const id = threadId(2);
      const text = textArg("message-file", null, flags.has("self") ? 2 : 3);
      const r = await api("POST", `/api/v1/threads/${id}/send`, { text, mode: flag("mode") ?? "steer" });
      return out(r, () => r.delivery === "queued" ? `queued (${r.queuedMessage?.waitingOn})` : "sent");
    }
    case "stop": case "retry": case "archive": case "unarchive": {
      const r = await api("POST", `/api/v1/threads/${threadId(2)}/${cmd}`);
      return out(r, () => `${cmd}: ok`);
    }
    case "delete": {
      const r = await api("DELETE", `/api/v1/threads/${threadId(2)}`);
      return out(r, () => "deleted");
    }
    case "wait": {
      const id = threadId(2);
      const until = Date.now() + Number(flag("timeout") ?? 600) * 1000;
      for (;;) {
        const t = await api("GET", `/api/v1/threads/${id}`);
        const blocked = t.pendingInteractions.length > 0;
        if (blocked || ((t.status === "idle" || t.status === "error") && t.queuedMessages.length === 0 && !t.runtimeBusy)) {
          const state = blocked ? "waiting-interaction" : t.status;
          return out({ threadId: id, state, thread: t }, () => `${id} ${state}`);
        }
        if (Date.now() > until) fail("timeout", `${id} still ${t.status}`);
        await new Promise((r) => setTimeout(r, 1000));
      }
    }
    case "interactions": {
      const sub = pos[2];
      if (sub === "list" || !sub) {
        const is = await api("GET", `/api/v1/threads/${threadId(3)}/interactions`);
        return out(is, () => is.map((i: any) => row([i.id, i.kind, i.status, JSON.stringify(i.payload)], [14, 14, 11, 80])).join("\n") || "(none)");
      }
      const iid = pos[3];
      const id = flags.has("self") ? threadId(0) : pos[4];
      if (!iid || !id) fail("usage", `mcx thread interactions ${sub} <interaction-id> <thread-id>`);
      let body: Record<string, unknown>;
      if (sub === "approve") body = { decision: flag("scope") === "session" ? "allow_for_session" : "allow_once" };
      else if (sub === "deny") body = { decision: "deny", reason: flag("reason") };
      else if (sub === "answer") {
        const answers: Record<string, string> = {};
        const t = flag("text") ?? "";
        const eq = t.indexOf("=");
        if (eq > 0) answers[t.slice(0, eq)] = t.slice(eq + 1); else answers.answer = t;
        body = { answers };
      } else fail("usage", `unknown interactions command ${sub}`);
      const r = await api("POST", `/api/v1/threads/${id}/interactions/${iid}/resolve`, body!);
      return out(r, () => `${r.id} ${r.status}`);
    }
    default:
      fail("usage", `unknown thread command ${cmd ?? ""}`, "mcx help");
  }
}

await main();
