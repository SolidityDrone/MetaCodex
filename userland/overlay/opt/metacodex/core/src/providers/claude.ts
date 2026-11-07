// Cloud agent provider: drives the unmodified Claude Code CLI through its stream-json protocol, one child
// process per running thread (multi-turn over stdin, resumable with --resume). Core never sees or stores
// credentials: the CLI uses its own login or the user's API key from the environment.
import { spawn, spawnSync } from "node:child_process";
import type { ChildProcessWithoutNullStreams } from "node:child_process";
import { createInterface } from "node:readline";
import type { ProviderDriver, ProviderRuntime, SendMode, ThreadContext } from "../types.ts";

type Json = Record<string, any>;

function permissionArgs(mode: string): string[] {
  if (mode === "full") return ["--dangerously-skip-permissions"];
  if (mode === "accept-edits") return ["--permission-mode", "acceptEdits"];
  return ["--permission-mode", "default"]; // v0: tool requests the CLI cannot ask about are reported as denials
}

class ClaudeRuntime implements ProviderRuntime {
  busy = false;
  private proc: ChildProcessWithoutNullStreams | null = null;
  private ctx: ThreadContext;
  private bin: string;
  private stopping = false;
  private stderr = "";

  constructor(ctx: ThreadContext, bin: string) {
    this.ctx = ctx;
    this.bin = bin;
  }

  private ensureProcess(): ChildProcessWithoutNullStreams {
    if (this.proc && this.proc.exitCode === null) return this.proc;
    const args = ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
      ...permissionArgs(this.ctx.permissionMode)];
    if (this.ctx.model) args.push("--model", this.ctx.model);
    if (this.ctx.sessionId) args.push("--resume", this.ctx.sessionId);
    const p = spawn(this.bin, args, { cwd: this.ctx.cwd, env: { ...process.env, ...this.ctx.env }, stdio: ["pipe", "pipe", "pipe"] });
    this.proc = p;
    this.stderr = "";
    p.stderr.on("data", (d: Buffer) => { this.stderr = (this.stderr + d.toString()).slice(-4000); });
    createInterface({ input: p.stdout }).on("line", (line) => this.onLine(line));
    p.on("exit", (code, signal) => {
      this.proc = null;
      if (this.busy && !this.stopping) {
        this.busy = false;
        this.ctx.emit("error", { code: "provider_exit", message: `agent exited (${code ?? signal}) ${this.stderr.trim().slice(-500)}`, retryable: true });
        this.ctx.turnFinished("failed", `agent exited (${code ?? signal})`);
      }
    });
    p.on("error", (e) => {
      this.ctx.emit("error", { code: "provider_spawn", message: String(e), retryable: false });
      if (this.busy) { this.busy = false; this.ctx.turnFinished("failed", String(e)); }
    });
    return p;
  }

  send(text: string, mode: SendMode): "sent" | "queued" {
    const p = this.ensureProcess();
    const msg = { type: "user", message: { role: "user", content: [{ type: "text", text }] } };
    p.stdin.write(JSON.stringify(msg) + "\n");
    if (!this.busy) {
      this.busy = true;
      this.ctx.turnStarted();
    }
    void mode; // the CLI queues a message that arrives mid-turn and answers it next
    return "sent";
  }

  private onLine(line: string): void {
    let m: Json;
    try { m = JSON.parse(line); } catch { return; }
    switch (m.type) {
      case "system":
        if (m.subtype === "init") {
          if (m.session_id) this.ctx.setSessionId(m.session_id);
          this.ctx.emit("provider.init", { model: m.model, tools: (m.tools ?? []).length, cwd: m.cwd });
        }
        break;
      case "assistant":
        for (const c of m.message?.content ?? []) {
          if (c.type === "text" && c.text) this.ctx.emit("assistant.message", { text: c.text });
          else if (c.type === "tool_use") this.ctx.emit("tool.call", { callId: c.id, name: c.name, input: c.input });
        }
        break;
      case "user":
        for (const c of m.message?.content ?? []) {
          if (c.type === "tool_result") {
            const body = typeof c.content === "string" ? c.content : JSON.stringify(c.content ?? "");
            this.ctx.emit("tool.result", { callId: c.tool_use_id, ok: !c.is_error, summary: body.slice(0, 400) });
          }
        }
        break;
      case "result": {
        this.ctx.emit("usage", { costUsd: m.total_cost_usd ?? null, tokensIn: m.usage?.input_tokens ?? null, tokensOut: m.usage?.output_tokens ?? null, turns: m.num_turns ?? null });
        for (const d of m.permission_denials ?? []) this.ctx.emit("permission.denied", { tool: d.tool_name, input: d.tool_input });
        const failed = m.is_error || (typeof m.subtype === "string" && m.subtype.startsWith("error"));
        this.busy = false;
        this.ctx.turnFinished(failed ? "failed" : "completed", failed ? String(m.result ?? m.subtype) : undefined);
        break;
      }
    }
  }

  async stop(): Promise<void> {
    const p = this.proc;
    if (!p) return;
    this.stopping = true;
    p.stdin.end();
    p.kill("SIGINT");
    await new Promise<void>((r) => {
      const t = setTimeout(() => { p.kill("SIGKILL"); r(); }, 3000);
      p.once("exit", () => { clearTimeout(t); r(); });
    });
    this.busy = false;
  }
}

export function claudeDriver(bin = "claude"): ProviderDriver {
  return {
    id: "claude-code",
    label: "cloud agent",
    async available() {
      const r = spawnSync(bin, ["--version"], { encoding: "utf8", timeout: 60000 });
      return r.status === 0 ? { ok: true, detail: (r.stdout || "").trim() } : { ok: false, detail: (r.stderr || String(r.error ?? "not installed")).trim().slice(0, 200) };
    },
    start: (ctx) => new ClaudeRuntime(ctx, bin),
  };
}
