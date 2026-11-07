// Replay provider: plays a recorded tape with its timing. Judge builds run on it (no keys, no network);
// tests use it as a deterministic agent. Recorded approvals wait for a real decision from the user.
//
// Tape = JSON lines: {"turn":true} starts the next turn's block; {"wait":ms}; {"emit":kind,"data":{...}};
// {"ask":"approval"|"user_question","payload":{...},"onDeny":[...lines...]}; {"fail":"message"}.
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import type { ProviderDriver, ProviderRuntime, SendMode, ThreadContext } from "../types.ts";

type Step = { turn?: boolean; wait?: number; emit?: string; data?: Record<string, unknown>; ask?: string; payload?: Record<string, unknown>; onDeny?: Step[]; fail?: string };

export function parseTape(text: string): Step[][] {
  const turns: Step[][] = [];
  let cur: Step[] | null = null;
  for (const line of text.split("\n")) {
    const l = line.trim();
    if (!l || l.startsWith("//")) continue;
    const step = JSON.parse(l) as Step;
    if (step.turn) { cur = []; turns.push(cur); continue; }
    if (!cur) { cur = []; turns.push(cur); }
    cur.push(step);
  }
  return turns;
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

class ReplayRuntime implements ProviderRuntime {
  busy = false;
  private turns: Step[][];
  private next = 0;
  private stopped = false;
  private ctx: ThreadContext;
  private speed: number;

  constructor(ctx: ThreadContext, turns: Step[][], speed: number) {
    this.ctx = ctx;
    this.turns = turns;
    this.speed = speed;
  }

  send(text: string, mode: SendMode): "sent" | "queued" {
    if (this.busy) {
      this.ctx.emit("assistant.message", { text: `(replay) noted: ${text.slice(0, 120)}` });
      return "sent";
    }
    void mode;
    this.busy = true;
    void this.play();
    return "sent";
  }

  private async play(): Promise<void> {
    this.ctx.turnStarted();
    const steps = this.turns[this.next++] ?? [{ emit: "assistant.message", data: { text: "(replay) this recording has no more turns." } }];
    let outcome: "completed" | "failed" = "completed";
    let error: string | undefined;
    try {
      if ((await this.run(steps)) === "failed") { outcome = "failed"; error = "recorded failure"; }
    } catch (e) {
      outcome = "failed";
      error = String(e);
    }
    // not busy before reporting: the service dispatches the next queued message from turnFinished
    this.busy = false;
    if (!this.stopped) this.ctx.turnFinished(outcome, error);
  }

  private async run(steps: Step[]): Promise<"ok" | "failed"> {
    for (const s of steps) {
      if (this.stopped) return "ok";
      if (s.wait) await sleep(s.wait / this.speed);
      if (s.emit) this.ctx.emit(s.emit, s.data ?? {});
      if (s.fail) { this.ctx.emit("error", { code: "replay", message: s.fail, retryable: true }); return "failed"; }
      if (s.ask) {
        const r = await this.ctx.ask(s.ask, s.payload ?? {});
        if (r.decision === "deny") {
          this.ctx.emit("tool.result", { ok: false, summary: `denied${r.reason ? ": " + r.reason : ""}` });
          if (s.onDeny) return this.run(s.onDeny);
          return "ok";
        }
      }
    }
    return "ok";
  }

  async stop(): Promise<void> {
    this.stopped = true;
  }
}

export function replayDriver(tapeDir: string, defaultTape = "demo", speed = 1): ProviderDriver {
  return {
    id: "replay",
    label: "replay",
    async available() {
      return { ok: existsSync(join(tapeDir, `${defaultTape}.jsonl`)), detail: tapeDir };
    },
    start(ctx) {
      const name = ctx.model && /^[\w.-]+$/.test(ctx.model) ? ctx.model : defaultTape; // model = tape name
      const file = join(tapeDir, `${name}.jsonl`);
      const turns = existsSync(file) ? parseTape(readFileSync(file, "utf8")) : [];
      return new ReplayRuntime(ctx, turns, speed);
    },
  };
}
