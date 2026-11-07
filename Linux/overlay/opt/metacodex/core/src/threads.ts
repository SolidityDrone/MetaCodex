// Thread service: the behaviour of bb's threads (spawn, tell steer/queue/auto, interactions, parent
// notifications, stop/retry/archive/delete) on top of the store and the provider drivers.
import { randomBytes } from "node:crypto";
import { mkdirSync } from "node:fs";
import { join } from "node:path";
import type { Store } from "./store.ts";
import { CoreError } from "./types.ts";
import type {
  Interaction, InteractionKind, ParentNotifications, PermissionMode, Project, ProviderDriver, ProviderRuntime,
  QueuedMessage, Resolution, SendMode, Thread, ThreadContext, ThreadEvent, ThreadStatus,
} from "./types.ts";

export function newId(prefix: string): string {
  const alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
  const bytes = randomBytes(10);
  let s = "";
  for (const b of bytes) s += alphabet[b % alphabet.length];
  return `${prefix}_${s}`;
}

export interface SpawnOptions {
  projectId: string;
  prompt?: string;
  title?: string;
  provider?: string;
  model?: string;
  parentThreadId?: string;
  parentNotifications?: ParentNotifications;
  permissionMode?: PermissionMode;
}

export type ChangeListener = (change: { threadId: string; seq: number; status: ThreadStatus; kind: string }) => void;

export class ThreadService {
  private runtimes = new Map<string, ProviderRuntime>();
  private waiters = new Map<string, (r: Resolution | null) => void>(); // interaction id -> resolver
  private listeners = new Set<ChangeListener>();
  private lastFailedPrompt = new Map<string, string>();
  store: Store;
  drivers: Map<string, ProviderDriver>;
  workRoot: string;
  defaultProvider: string;
  cliPath: string;
  serverUrl: string;

  constructor(store: Store, drivers: ProviderDriver[], opts: { workRoot: string; defaultProvider: string; cliPath: string; serverUrl: string }) {
    this.store = store;
    this.drivers = new Map(drivers.map((d) => [d.id, d]));
    this.workRoot = opts.workRoot;
    this.defaultProvider = opts.defaultProvider;
    this.cliPath = opts.cliPath;
    this.serverUrl = opts.serverUrl;
    // a restart loses every live runtime: threads caught mid-turn become idle with a note
    for (const t of store.threads({})) {
      if (t.status === "active" || t.status === "starting" || t.status === "stopping") {
        store.updateThread(t.id, { status: "idle" });
        store.appendEvent(t.id, "status", { from: t.status, to: "idle", reason: "core restarted" });
      }
      for (const i of store.interactions(t.id, true)) store.settleInteraction(i.id, "interrupted", null);
    }
  }

  onChange(l: ChangeListener): () => void {
    this.listeners.add(l);
    return () => this.listeners.delete(l);
  }

  // ------------------------------------------------------------------ projects

  createProject(name: string, path?: string): Project {
    if (!/^[A-Za-z0-9._-]{1,64}$/.test(name)) throw new CoreError("invalid_name", "project names use letters, digits, . _ - (max 64)");
    if (this.store.project(name)) throw new CoreError("exists", `project ${name} already exists`, 409);
    const dir = path ?? join(this.workRoot, name);
    mkdirSync(dir, { recursive: true });
    return this.store.addProject({ id: newId("prj"), name, path: dir, createdAt: Date.now() });
  }

  project(idOrName: string): Project {
    const p = this.store.project(idOrName);
    if (!p) throw new CoreError("not_found", `no project ${idOrName}`, 404, "list them with: mcx project list");
    return p;
  }

  // ------------------------------------------------------------------ threads

  thread(id: string): Thread {
    const t = this.store.thread(id);
    if (!t) throw new CoreError("not_found", `no thread ${id}`, 404, "list them with: mcx thread list");
    return t;
  }

  details(id: string) {
    const t = this.thread(id);
    const pending = this.store.interactions(id, true);
    const queued = this.store.queued(id);
    return {
      ...t,
      pendingInteractions: pending,
      queuedWork: queued.length === 0 ? "none" : "waiting",
      queuedMessages: queued,
      children: this.store.threads({ parentThreadId: id }).map((c) => ({ id: c.id, title: c.title, status: c.status })),
      runtimeLoaded: this.runtimes.has(id),
      runtimeBusy: this.runtimes.get(id)?.busy ?? false,
    };
  }

  spawn(o: SpawnOptions): Thread {
    const project = this.project(o.projectId);
    const provider = o.provider ?? (o.parentThreadId ? this.thread(o.parentThreadId).provider : this.defaultProvider);
    if (!this.drivers.has(provider)) throw new CoreError("unknown_provider", `no provider ${provider}`, 400, `providers: ${[...this.drivers.keys()].join(", ")}`);
    if (o.parentThreadId) this.thread(o.parentThreadId);
    const now = Date.now();
    const t: Thread = {
      id: newId("thr"),
      projectId: project.id,
      title: o.title ?? (o.prompt ? o.prompt.split("\n")[0].slice(0, 60) : null),
      provider,
      model: o.model ?? null,
      status: "idle",
      parentThreadId: o.parentThreadId ?? null,
      parentNotifications: o.parentNotifications ?? "auto",
      permissionMode: o.permissionMode ?? (o.parentThreadId ? this.thread(o.parentThreadId).permissionMode : "auto"),
      sessionId: null,
      archivedAt: null,
      createdAt: now,
      updatedAt: now,
      lastSeq: 0,
      error: null,
    };
    this.store.addThread(t);
    this.emit(t.id, "thread.created", { projectId: project.id, provider, model: t.model, parentThreadId: t.parentThreadId, title: t.title });
    if (o.prompt) this.send(t.id, o.prompt, "auto");
    return this.thread(t.id);
  }

  /// bb's `thread tell`: steer into a live turn, queue behind it, or (auto) steer-or-start.
  send(id: string, text: string, mode: SendMode = "steer"): { delivery: "sent" | "queued"; queuedMessage?: QueuedMessage } {
    const t = this.thread(id);
    if (t.archivedAt) throw new CoreError("archived", "thread is archived", 409, `mcx thread unarchive ${id}`);
    if (!text.trim()) throw new CoreError("empty", "empty message");
    if (this.store.interactions(id, true).length > 0) return { delivery: "queued", queuedMessage: this.enqueue(id, text, mode, "interaction") };
    const rt = this.runtimes.get(id);
    if (rt?.busy) {
      if (mode === "queue") return { delivery: "queued", queuedMessage: this.enqueue(id, text, mode, "thread-busy") };
      const r = rt.send(text, "steer");
      this.emit(id, "user.message", { text, mode: "steer", delivered: r });
      return { delivery: r };
    }
    this.startTurn(t, text, mode);
    return { delivery: "sent" };
  }

  private enqueue(id: string, text: string, mode: SendMode, waitingOn: QueuedMessage["waitingOn"]): QueuedMessage {
    const q = this.store.addQueued({ id: newId("qm"), threadId: id, text, mode, waitingOn, state: "waiting", createdAt: Date.now() });
    this.emit(id, "queue.added", { messageId: q.id, waitingOn });
    return q;
  }

  private startTurn(t: Thread, text: string, mode: SendMode): void {
    let rt = this.runtimes.get(t.id);
    if (!rt) {
      const driver = this.drivers.get(t.provider)!;
      this.setStatus(t.id, "starting");
      rt = driver.start(this.contextFor(t));
      this.runtimes.set(t.id, rt);
    }
    this.lastFailedPrompt.set(t.id, text);
    this.emit(t.id, "user.message", { text, mode });
    rt.send(text, "queue");
  }

  private contextFor(t: Thread): ThreadContext {
    const project = this.store.project(t.projectId)!;
    const self = this;
    return {
      threadId: t.id,
      cwd: project.path,
      env: {
        MCX_PROJECT_ID: t.projectId,
        MCX_THREAD_ID: t.id,
        MCX_ENVIRONMENT_ID: `env_${t.projectId}`,
        MCX_CLI: this.cliPath,
        MCX_SERVER_URL: this.serverUrl,
      },
      model: t.model,
      permissionMode: t.permissionMode,
      sessionId: t.sessionId,
      emit: (kind, data) => self.emit(t.id, kind, data),
      turnStarted: () => self.setStatus(t.id, "active"),
      turnFinished: (outcome, error) => self.finishTurn(t.id, outcome, error),
      setSessionId: (sid) => self.store.updateThread(t.id, { sessionId: sid }),
      ask: (kind, payload) => self.ask(t.id, kind, payload),
    };
  }

  private finishTurn(id: string, outcome: "completed" | "failed" | "interrupted", error?: string): void {
    const t = this.store.thread(id);
    if (!t) return;
    this.emit(id, "turn.finished", { outcome, ...(error ? { error } : {}) });
    if (outcome === "failed") {
      this.store.updateThread(id, { error: error ?? "turn failed" });
      this.setStatus(id, "error", error);
    } else {
      this.store.updateThread(id, { error: null });
      this.setStatus(id, "idle");
      if (outcome === "completed") this.lastFailedPrompt.delete(id);
    }
    for (const i of this.store.interactions(id, true)) this.settle(i.id, "interrupted", null);
    this.notifyParent(t, outcome === "completed" ? "finished" : outcome);
    this.dispatchQueue(id);
  }

  private dispatchQueue(id: string): void {
    const t = this.store.thread(id);
    if (!t || t.archivedAt || this.store.interactions(id, true).length > 0) return;
    if (this.runtimes.get(id)?.busy) return;
    const next = this.store.queued(id)[0];
    if (!next) return;
    this.store.deleteQueued(next.id);
    this.emit(id, "queue.dispatched", { messageId: next.id });
    this.startTurn(t, next.text, next.mode);
  }

  /// A child's outcome or blocker becomes a message to its parent (bb: parent notifications).
  private notifyParent(child: Thread, outcome: "finished" | "failed" | "interrupted" | "blocked", detail?: string): void {
    if (!child.parentThreadId || child.parentNotifications === "off") return;
    if (child.parentNotifications === "stuck" && outcome === "finished") return;
    const parent = this.store.thread(child.parentThreadId);
    if (!parent || parent.archivedAt) return;
    const last = [...this.store.events(child.id, Math.max(0, child.lastSeq - 50), 100)].reverse()
      .find((e) => e.kind === "assistant.message");
    const excerpt = detail ?? ((last?.data.text as string) ?? "").slice(0, 600);
    this.emit(parent.id, "child.notice", { childThreadId: child.id, outcome, excerpt });
    const text = `[metacodex] child thread ${child.id} (${child.title ?? "untitled"}) ${outcome}.` + (excerpt ? `\n${excerpt}` : "") +
      `\nInspect it with: mcx thread log ${child.id}`;
    try { this.send(parent.id, text, "auto"); } catch { /* parent cannot take messages now */ }
  }

  // ------------------------------------------------------------------ interactions

  private ask(id: string, kind: InteractionKind, payload: Record<string, unknown>): Promise<Resolution> {
    const i: Interaction = { id: newId("int"), threadId: id, kind, status: "pending", payload, resolution: null, createdAt: Date.now(), resolvedAt: null };
    this.store.addInteraction(i);
    this.emit(id, "interaction.requested", { interactionId: i.id, kind, payload });
    const t = this.store.thread(id);
    if (t) this.notifyParent(t, "blocked", `waiting for ${kind}: ${String(payload.summary ?? payload.title ?? "")}`);
    return new Promise((resolve) => {
      this.waiters.set(i.id, (r) => resolve(r ?? { decision: "deny", reason: "interrupted" }));
    });
  }

  resolve(threadId: string, interactionId: string, resolution: Resolution): Interaction {
    const i = this.store.interaction(interactionId);
    if (!i || i.threadId !== threadId) throw new CoreError("not_found", `no interaction ${interactionId} on ${threadId}`, 404);
    if (i.status !== "pending") throw new CoreError("not_pending", `interaction is ${i.status}`, 409);
    if (i.kind === "approval" && !resolution.decision) throw new CoreError("invalid", "approval needs decision: allow_once, allow_for_session or deny");
    this.settle(interactionId, "resolved", resolution);
    this.dispatchQueue(threadId);
    return this.store.interaction(interactionId)!;
  }

  private settle(interactionId: string, status: "resolved" | "interrupted", r: Resolution | null): void {
    const i = this.store.interaction(interactionId);
    if (!i) return;
    this.store.settleInteraction(interactionId, status, r);
    this.emit(i.threadId, "interaction.resolved", { interactionId, status, ...(r ?? {}) });
    const w = this.waiters.get(interactionId);
    this.waiters.delete(interactionId);
    w?.(r);
  }

  // ------------------------------------------------------------------ lifecycle

  async stop(id: string): Promise<Thread> {
    const t = this.thread(id);
    const rt = this.runtimes.get(id);
    if (rt) {
      const wasBusy = rt.busy;
      this.setStatus(id, "stopping");
      for (const i of this.store.interactions(id, true)) this.settle(i.id, "interrupted", null);
      this.runtimes.delete(id);
      await rt.stop();
      if (wasBusy) this.emit(id, "turn.finished", { outcome: "interrupted" });
      this.setStatus(id, "idle", "stopped");
      if (wasBusy) this.notifyParent(t, "interrupted");
    }
    return this.thread(id);
  }

  retry(id: string): { delivery: "sent" | "queued" } {
    const t = this.thread(id);
    const prompt = this.lastFailedPrompt.get(id);
    if (t.status !== "error" || !prompt) throw new CoreError("no_failed_turn", "nothing to retry", 409);
    this.emit(id, "turn.retry", {});
    return this.send(id, prompt, "auto");
  }

  async archive(id: string): Promise<void> {
    for (const c of this.store.threads({ parentThreadId: id })) await this.archive(c.id);
    await this.stop(id);
    this.store.updateThread(id, { archivedAt: Date.now() });
    this.emit(id, "thread.archived", {});
  }

  unarchive(id: string): void {
    this.thread(id);
    this.store.updateThread(id, { archivedAt: null });
    this.emit(id, "thread.unarchived", {});
  }

  async delete(id: string): Promise<void> {
    for (const c of [...this.store.threads({ parentThreadId: id }), ...this.store.threads({ parentThreadId: id, archived: true })]) await this.delete(c.id);
    await this.stop(id);
    this.store.deleteThread(id);
    for (const l of this.listeners) l({ threadId: id, seq: 0, status: "idle", kind: "thread.deleted" });
  }

  update(id: string, f: { title?: string; parentThreadId?: string | null; model?: string; parentNotifications?: ParentNotifications }): Thread {
    this.thread(id);
    this.store.updateThread(id, f);
    this.emit(id, "thread.updated", f as Record<string, unknown>);
    return this.thread(id);
  }

  async shutdown(): Promise<void> {
    for (const id of [...this.runtimes.keys()]) await this.stop(id).catch(() => undefined);
  }

  // ------------------------------------------------------------------ events

  private setStatus(id: string, to: ThreadStatus, reason?: string): void {
    const t = this.store.thread(id);
    if (!t || t.status === to) return;
    this.store.updateThread(id, { status: to });
    this.emit(id, "status", { from: t.status, to, ...(reason ? { reason } : {}) });
  }

  emit(id: string, kind: string, data: Record<string, unknown>): ThreadEvent {
    const e = this.store.appendEvent(id, kind, data);
    const status = this.store.thread(id)?.status ?? "idle";
    for (const l of this.listeners) {
      try { l({ threadId: id, seq: e.seq, status, kind }); } catch { /* a broken client must not stop the thread */ }
    }
    return e;
  }
}
