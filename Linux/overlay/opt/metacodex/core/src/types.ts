// MetaCodex Core: shared types. Concepts follow bb (projects, threads, events, interactions, queued
// messages); the code is ours. Erasable TypeScript only: Node 24 runs it without a build step.

export type ThreadStatus = "pending" | "idle" | "starting" | "active" | "stopping" | "error";
export type PermissionMode = "accept-edits" | "auto" | "full";
export type ParentNotifications = "auto" | "stuck" | "off";
export type SendMode = "steer" | "queue" | "auto";
export type InteractionKind = "approval" | "user_question" | string; // "<plugin>/<name>" too
export type InteractionStatus = "pending" | "resolving" | "resolved" | "interrupted";

export interface Project {
  id: string;
  name: string;
  path: string;
  createdAt: number;
}

export interface Thread {
  id: string;
  projectId: string;
  title: string | null;
  provider: string;
  model: string | null;
  status: ThreadStatus;
  parentThreadId: string | null;
  parentNotifications: ParentNotifications;
  permissionMode: PermissionMode;
  sessionId: string | null;
  archivedAt: number | null;
  createdAt: number;
  updatedAt: number;
  lastSeq: number;
  error: string | null;
}

export interface ThreadEvent {
  threadId: string;
  seq: number;
  ts: number;
  kind: string;
  data: Record<string, unknown>;
}

export interface Interaction {
  id: string;
  threadId: string;
  kind: InteractionKind;
  status: InteractionStatus;
  payload: Record<string, unknown>;
  resolution: Resolution | null;
  createdAt: number;
  resolvedAt: number | null;
}

export interface Resolution {
  decision?: "allow_once" | "allow_for_session" | "deny";
  reason?: string;
  answers?: Record<string, string>;
  value?: unknown;
}

export interface QueuedMessage {
  id: string;
  threadId: string;
  text: string;
  mode: SendMode;
  waitingOn: "thread-busy" | "turn-starting" | "interaction" | "time";
  state: "queued" | "waiting" | "claimed";
  createdAt: number;
}

/// What a provider driver gets for one running thread.
export interface ThreadContext {
  threadId: string;
  cwd: string;
  env: Record<string, string>;
  model: string | null;
  permissionMode: PermissionMode;
  sessionId: string | null;
  emit(kind: string, data: Record<string, unknown>): void;
  turnStarted(): void;
  turnFinished(outcome: "completed" | "failed" | "interrupted", error?: string): void;
  setSessionId(id: string): void;
  /// Opens a pending interaction and resolves when the user decides (or the turn is interrupted).
  ask(kind: InteractionKind, payload: Record<string, unknown>): Promise<Resolution>;
}

/// One live agent runtime for one thread (a CLI process, a server session, a tape).
export interface ProviderRuntime {
  /// A user message into this runtime. "steer" = into the running turn if the provider can.
  send(text: string, mode: SendMode): "sent" | "queued";
  readonly busy: boolean;
  stop(): Promise<void>;
}

export interface ProviderDriver {
  id: string;
  label: string; // generic UI label ("cloud agent", "open agent", "replay"): no brand names in the app
  available(): Promise<{ ok: boolean; detail: string }>;
  start(ctx: ThreadContext): ProviderRuntime;
}

export class CoreError extends Error {
  code: string;
  status: number;
  hint?: string;
  constructor(code: string, message: string, status = 400, hint?: string) {
    super(message);
    this.code = code;
    this.status = status;
    this.hint = hint;
  }
}
