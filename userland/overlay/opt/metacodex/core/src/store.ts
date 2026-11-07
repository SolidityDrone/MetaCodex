// SQLite store (node:sqlite, built into Node >= 22.13: no native addon to install on the headset).
import { DatabaseSync } from "node:sqlite";
import type { Interaction, Project, QueuedMessage, Resolution, Thread, ThreadEvent, ThreadStatus } from "./types.ts";

const SCHEMA = `
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
CREATE TABLE IF NOT EXISTS projects (
  id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE, path TEXT NOT NULL, created_at INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS threads (
  id TEXT PRIMARY KEY, project_id TEXT NOT NULL, title TEXT, provider TEXT NOT NULL, model TEXT,
  status TEXT NOT NULL, parent_thread_id TEXT, parent_notifications TEXT NOT NULL DEFAULT 'auto',
  permission_mode TEXT NOT NULL DEFAULT 'auto', session_id TEXT, archived_at INTEGER,
  created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, last_seq INTEGER NOT NULL DEFAULT 0, error TEXT);
CREATE INDEX IF NOT EXISTS threads_parent ON threads(parent_thread_id);
CREATE TABLE IF NOT EXISTS events (
  thread_id TEXT NOT NULL, seq INTEGER NOT NULL, ts INTEGER NOT NULL, kind TEXT NOT NULL, data TEXT NOT NULL,
  PRIMARY KEY (thread_id, seq));
CREATE TABLE IF NOT EXISTS interactions (
  id TEXT PRIMARY KEY, thread_id TEXT NOT NULL, kind TEXT NOT NULL, status TEXT NOT NULL, payload TEXT NOT NULL,
  resolution TEXT, created_at INTEGER NOT NULL, resolved_at INTEGER);
CREATE INDEX IF NOT EXISTS interactions_thread ON interactions(thread_id);
CREATE TABLE IF NOT EXISTS queued_messages (
  id TEXT PRIMARY KEY, thread_id TEXT NOT NULL, text TEXT NOT NULL, mode TEXT NOT NULL, waiting_on TEXT NOT NULL,
  state TEXT NOT NULL, created_at INTEGER NOT NULL);
`;

type Row = Record<string, unknown>;

const toProject = (r: Row): Project => ({
  id: r.id as string, name: r.name as string, path: r.path as string, createdAt: r.created_at as number,
});

const toThread = (r: Row): Thread => ({
  id: r.id as string,
  projectId: r.project_id as string,
  title: (r.title as string) ?? null,
  provider: r.provider as string,
  model: (r.model as string) ?? null,
  status: r.status as ThreadStatus,
  parentThreadId: (r.parent_thread_id as string) ?? null,
  parentNotifications: r.parent_notifications as Thread["parentNotifications"],
  permissionMode: r.permission_mode as Thread["permissionMode"],
  sessionId: (r.session_id as string) ?? null,
  archivedAt: (r.archived_at as number) ?? null,
  createdAt: r.created_at as number,
  updatedAt: r.updated_at as number,
  lastSeq: r.last_seq as number,
  error: (r.error as string) ?? null,
});

const toEvent = (r: Row): ThreadEvent => ({
  threadId: r.thread_id as string, seq: r.seq as number, ts: r.ts as number, kind: r.kind as string,
  data: JSON.parse(r.data as string),
});

const toInteraction = (r: Row): Interaction => ({
  id: r.id as string,
  threadId: r.thread_id as string,
  kind: r.kind as string,
  status: r.status as Interaction["status"],
  payload: JSON.parse(r.payload as string),
  resolution: r.resolution ? (JSON.parse(r.resolution as string) as Resolution) : null,
  createdAt: r.created_at as number,
  resolvedAt: (r.resolved_at as number) ?? null,
});

const toQueued = (r: Row): QueuedMessage => ({
  id: r.id as string, threadId: r.thread_id as string, text: r.text as string, mode: r.mode as QueuedMessage["mode"],
  waitingOn: r.waiting_on as QueuedMessage["waitingOn"], state: r.state as QueuedMessage["state"],
  createdAt: r.created_at as number,
});

export class Store {
  db: DatabaseSync;

  constructor(path: string) {
    this.db = new DatabaseSync(path);
    this.db.exec(SCHEMA);
  }

  close(): void {
    this.db.close();
  }

  // ---------------------------------------------------------------- projects
  addProject(p: Project): Project {
    this.db.prepare("INSERT INTO projects (id, name, path, created_at) VALUES (?, ?, ?, ?)").run(p.id, p.name, p.path, p.createdAt);
    return p;
  }
  projects(): Project[] {
    return this.db.prepare("SELECT * FROM projects ORDER BY created_at").all().map((r) => toProject(r as Row));
  }
  project(idOrName: string): Project | null {
    const r = this.db.prepare("SELECT * FROM projects WHERE id = ? OR name = ?").get(idOrName, idOrName);
    return r ? toProject(r as Row) : null;
  }
  deleteProject(id: string): void {
    this.db.prepare("DELETE FROM projects WHERE id = ?").run(id);
  }

  // ---------------------------------------------------------------- threads
  addThread(t: Thread): Thread {
    this.db.prepare(`INSERT INTO threads (id, project_id, title, provider, model, status, parent_thread_id,
      parent_notifications, permission_mode, session_id, archived_at, created_at, updated_at, last_seq, error)
      VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`).run(
      t.id, t.projectId, t.title, t.provider, t.model, t.status, t.parentThreadId, t.parentNotifications,
      t.permissionMode, t.sessionId, t.archivedAt, t.createdAt, t.updatedAt, t.lastSeq, t.error);
    return t;
  }
  thread(id: string): Thread | null {
    const r = this.db.prepare("SELECT * FROM threads WHERE id = ?").get(id);
    return r ? toThread(r as Row) : null;
  }
  threads(f: { projectId?: string; parentThreadId?: string; status?: string; archived?: boolean } = {}): Thread[] {
    const where: string[] = [];
    const args: (string | number)[] = [];
    if (f.projectId) { where.push("project_id = ?"); args.push(f.projectId); }
    if (f.parentThreadId) { where.push("parent_thread_id = ?"); args.push(f.parentThreadId); }
    if (f.status) { where.push("status = ?"); args.push(f.status); }
    where.push(f.archived ? "archived_at IS NOT NULL" : "archived_at IS NULL");
    const sql = `SELECT * FROM threads WHERE ${where.join(" AND ")} ORDER BY updated_at DESC LIMIT 500`;
    return this.db.prepare(sql).all(...args).map((r) => toThread(r as Row));
  }
  updateThread(id: string, fields: Partial<Thread>): void {
    const map: Record<string, string> = {
      title: "title", provider: "provider", model: "model", status: "status", parentThreadId: "parent_thread_id",
      parentNotifications: "parent_notifications", permissionMode: "permission_mode", sessionId: "session_id",
      archivedAt: "archived_at", error: "error",
    };
    const sets: string[] = [];
    const args: (string | number | null)[] = [];
    for (const [k, v] of Object.entries(fields)) {
      const col = map[k];
      if (!col) continue;
      sets.push(`${col} = ?`);
      args.push(v as string | number | null);
    }
    if (sets.length === 0) return;
    sets.push("updated_at = ?");
    args.push(Date.now(), id);
    this.db.prepare(`UPDATE threads SET ${sets.join(", ")} WHERE id = ?`).run(...args);
  }
  deleteThread(id: string): void {
    for (const sql of ["DELETE FROM events WHERE thread_id = ?", "DELETE FROM interactions WHERE thread_id = ?",
      "DELETE FROM queued_messages WHERE thread_id = ?", "DELETE FROM threads WHERE id = ?"]) this.db.prepare(sql).run(id);
  }

  // ---------------------------------------------------------------- events (append-only, per-thread seq)
  appendEvent(threadId: string, kind: string, data: Record<string, unknown>): ThreadEvent {
    const ts = Date.now();
    this.db.exec("BEGIN IMMEDIATE");
    try {
      const r = this.db.prepare("SELECT last_seq FROM threads WHERE id = ?").get(threadId) as Row | undefined;
      const seq = ((r?.last_seq as number) ?? 0) + 1;
      this.db.prepare("INSERT INTO events (thread_id, seq, ts, kind, data) VALUES (?, ?, ?, ?, ?)").run(threadId, seq, ts, kind, JSON.stringify(data));
      this.db.prepare("UPDATE threads SET last_seq = ?, updated_at = ? WHERE id = ?").run(seq, ts, threadId);
      this.db.exec("COMMIT");
      return { threadId, seq, ts, kind, data };
    } catch (e) {
      this.db.exec("ROLLBACK");
      throw e;
    }
  }
  events(threadId: string, afterSeq = 0, limit = 200): ThreadEvent[] {
    return this.db.prepare("SELECT * FROM events WHERE thread_id = ? AND seq > ? ORDER BY seq LIMIT ?")
      .all(threadId, afterSeq, Math.min(Math.max(limit, 1), 1000)).map((r) => toEvent(r as Row));
  }

  // ---------------------------------------------------------------- interactions
  addInteraction(i: Interaction): Interaction {
    this.db.prepare(`INSERT INTO interactions (id, thread_id, kind, status, payload, resolution, created_at, resolved_at)
      VALUES (?, ?, ?, ?, ?, ?, ?, ?)`).run(i.id, i.threadId, i.kind, i.status, JSON.stringify(i.payload), null, i.createdAt, null);
    return i;
  }
  interaction(id: string): Interaction | null {
    const r = this.db.prepare("SELECT * FROM interactions WHERE id = ?").get(id);
    return r ? toInteraction(r as Row) : null;
  }
  interactions(threadId: string, pendingOnly = false): Interaction[] {
    const sql = pendingOnly
      ? "SELECT * FROM interactions WHERE thread_id = ? AND status = 'pending' ORDER BY created_at"
      : "SELECT * FROM interactions WHERE thread_id = ? ORDER BY created_at";
    return this.db.prepare(sql).all(threadId).map((r) => toInteraction(r as Row));
  }
  settleInteraction(id: string, status: "resolved" | "interrupted", resolution: Resolution | null): void {
    this.db.prepare("UPDATE interactions SET status = ?, resolution = ?, resolved_at = ? WHERE id = ?")
      .run(status, resolution ? JSON.stringify(resolution) : null, Date.now(), id);
  }

  // ---------------------------------------------------------------- queued messages
  addQueued(q: QueuedMessage): QueuedMessage {
    this.db.prepare(`INSERT INTO queued_messages (id, thread_id, text, mode, waiting_on, state, created_at)
      VALUES (?, ?, ?, ?, ?, ?, ?)`).run(q.id, q.threadId, q.text, q.mode, q.waitingOn, q.state, q.createdAt);
    return q;
  }
  queued(threadId: string): QueuedMessage[] {
    return this.db.prepare("SELECT * FROM queued_messages WHERE thread_id = ? ORDER BY created_at").all(threadId).map((r) => toQueued(r as Row));
  }
  deleteQueued(id: string): void {
    this.db.prepare("DELETE FROM queued_messages WHERE id = ?").run(id);
  }
}
