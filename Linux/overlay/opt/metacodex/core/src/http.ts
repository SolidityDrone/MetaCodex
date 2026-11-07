// HTTP API (bb-shaped paths under /api/v1) + the WebSocket upgrade. Loopback only, bearer token.
import { createServer } from "node:http";
import type { IncomingMessage, Server, ServerResponse } from "node:http";
import { timingSafeEqual } from "node:crypto";
import type { ThreadService } from "./threads.ts";
import { CoreError } from "./types.ts";
import type { Resolution, SendMode } from "./types.ts";
import { WsHub } from "./ws.ts";

type Handler = (p: Record<string, string>, body: any, q: URLSearchParams) => Promise<unknown> | unknown;

export const VERSION = "0.1.0";

export function createApi(svc: ThreadService, token: string, info: () => Promise<Record<string, unknown>>): { server: Server; hub: WsHub } {
  const hub = new WsHub();
  svc.onChange((c) => hub.broadcast({ type: c.kind === "thread.deleted" ? "thread.deleted" : "thread.changed", ...c }));

  const routes: [string, RegExp, string[], Handler][] = [];
  const route = (method: string, path: string, h: Handler) => {
    const names: string[] = [];
    const re = new RegExp("^" + path.replace(/:(\w+)/g, (_m, n) => { names.push(n); return "([^/]+)"; }) + "$");
    routes.push([method, re, names, h]);
  };

  route("GET", "/api/v1/status", async () => ({ ok: true, version: VERSION, clients: hub.size, ...(await info()) }));

  route("GET", "/api/v1/projects", () => svc.store.projects());
  route("POST", "/api/v1/projects", (_p, b) => svc.createProject(String(b?.name ?? ""), b?.path));
  route("GET", "/api/v1/projects/:id", (p) => svc.project(p.id));
  route("DELETE", "/api/v1/projects/:id", (p) => {
    const pr = svc.project(p.id);
    if (svc.store.threads({ projectId: pr.id }).length) throw new CoreError("not_empty", "project has threads", 409);
    svc.store.deleteProject(pr.id);
    return { ok: true };
  });

  route("GET", "/api/v1/threads", (_p, _b, q) => svc.store.threads({
    projectId: q.get("projectId") ?? undefined, parentThreadId: q.get("parentThreadId") ?? undefined,
    status: q.get("status") ?? undefined, archived: q.get("archived") === "true",
  }));
  route("POST", "/api/v1/threads", (_p, b) => {
    if (!b?.projectId) throw new CoreError("invalid", "projectId is required");
    return svc.spawn({
      projectId: String(b.projectId), prompt: b.prompt, title: b.title, provider: b.provider, model: b.model,
      parentThreadId: b.parentThreadId, parentNotifications: b.parentNotifications, permissionMode: b.permissionMode,
    });
  });
  route("GET", "/api/v1/threads/:id", (p) => svc.details(p.id));
  route("PATCH", "/api/v1/threads/:id", (p, b) => svc.update(p.id, b ?? {}));
  route("DELETE", "/api/v1/threads/:id", async (p) => { await svc.delete(p.id); return { ok: true }; });
  route("GET", "/api/v1/threads/:id/events", (p, _b, q) => {
    svc.thread(p.id);
    return svc.store.events(p.id, Number(q.get("afterSeq") ?? 0), Number(q.get("limit") ?? 200));
  });
  route("POST", "/api/v1/threads/:id/send", (p, b) => svc.send(p.id, String(b?.text ?? ""), (b?.mode ?? "steer") as SendMode));
  route("POST", "/api/v1/threads/:id/stop", (p) => svc.stop(p.id));
  route("POST", "/api/v1/threads/:id/retry", (p) => svc.retry(p.id));
  route("POST", "/api/v1/threads/:id/archive", async (p) => { await svc.archive(p.id); return { ok: true }; });
  route("POST", "/api/v1/threads/:id/unarchive", (p) => { svc.unarchive(p.id); return { ok: true }; });
  route("GET", "/api/v1/threads/:id/interactions", (p) => { svc.thread(p.id); return svc.store.interactions(p.id); });
  route("POST", "/api/v1/threads/:id/interactions/:iid/resolve", (p, b) => svc.resolve(p.id, p.iid, (b ?? {}) as Resolution));
  route("GET", "/api/v1/threads/:id/queue", (p) => { svc.thread(p.id); return svc.store.queued(p.id); });
  route("DELETE", "/api/v1/threads/:id/queue/:mid", (p) => { svc.store.deleteQueued(p.mid); return { ok: true }; });

  const authorized = (req: IncomingMessage, url: URL): boolean => {
    const h = req.headers.authorization;
    const given = h?.startsWith("Bearer ") ? h.slice(7) : url.searchParams.get("token") ?? "";
    const a = Buffer.from(given), b = Buffer.from(token);
    return a.length === b.length && timingSafeEqual(a, b);
  };

  const send = (res: ServerResponse, status: number, obj: unknown) => {
    const body = JSON.stringify(obj);
    res.writeHead(status, { "content-type": "application/json; charset=utf-8", "content-length": Buffer.byteLength(body) });
    res.end(body);
  };

  const server = createServer(async (req, res) => {
    const url = new URL(req.url ?? "/", "http://127.0.0.1");
    try {
      if (!authorized(req, url)) throw new CoreError("unauthorized", "missing or wrong token", 401, "the token is in ~/.metacodex/token");
      const match = routes.find(([m, re]) => m === req.method && re.test(url.pathname));
      if (!match) throw new CoreError("not_found", `${req.method} ${url.pathname}`, 404);
      const [, re, names, h] = match;
      const values = re.exec(url.pathname)!.slice(1);
      const params = Object.fromEntries(names.map((n, i) => [n, decodeURIComponent(values[i])]));
      const body = req.method === "GET" || req.method === "DELETE" ? null : await readJson(req);
      send(res, 200, await h(params, body, url.searchParams));
    } catch (e) {
      const ce = e instanceof CoreError ? e : new CoreError("internal", String((e as Error)?.message ?? e), 500);
      send(res, ce.status, { ok: false, error: { code: ce.code, message: ce.message, ...(ce.hint ? { hint: ce.hint } : {}) } });
    }
  });

  server.on("upgrade", (req, socket) => {
    const url = new URL(req.url ?? "/", "http://127.0.0.1");
    if (url.pathname !== "/api/v1/ws" || !authorized(req, url)) {
      socket.end("HTTP/1.1 401 Unauthorized\r\n\r\n");
      return;
    }
    hub.accept(req, socket);
  });
  return { server, hub };
}

function readJson(req: IncomingMessage): Promise<any> {
  return new Promise((resolve, reject) => {
    let size = 0;
    const chunks: Buffer[] = [];
    req.on("data", (c: Buffer) => {
      size += c.length;
      if (size > 4 << 20) { reject(new CoreError("too_large", "body over 4 MB", 413)); req.destroy(); return; }
      chunks.push(c);
    });
    req.on("end", () => {
      if (chunks.length === 0) return resolve({});
      try { resolve(JSON.parse(Buffer.concat(chunks).toString("utf8"))); }
      catch { reject(new CoreError("invalid_json", "body is not JSON")); }
    });
    req.on("error", reject);
  });
}
