import { readFileSync } from "node:fs";
import { homedir } from "node:os";
import path from "node:path";
import { WebSocket as NodeWsWebSocket, type RawData } from "ws";
import {
  createRealtimeSocketAdapter,
  wrapStandardWebsocket,
} from "./realtime-client.js";
import type { BbRealtimeSocket, BbRealtimeSocketFactory } from "./transport.js";

function decodeWsMessageData(data: RawData): string {
  if (typeof data === "string") {
    return data;
  }
  if (Array.isArray(data)) {
    return Buffer.concat(data).toString("utf8");
  }
  if (Buffer.isBuffer(data)) {
    return data.toString("utf8");
  }
  return Buffer.from(new Uint8Array(data)).toString("utf8");
}

export function wrapNodeWsWebsocket(url: string): BbRealtimeSocket {
  const socket = new NodeWsWebSocket(url);
  const adapter = createRealtimeSocketAdapter(socket);
  socket.on("open", () => adapter.onopen?.());
  socket.on("message", (data) =>
    adapter.onmessage?.({ data: decodeWsMessageData(data) }),
  );
  socket.on("close", () => adapter.onclose?.());
  socket.on("error", () => adapter.onerror?.());
  return adapter;
}

/** MetaCodex: the MQH API token (apps/server/src/api-token.ts) as `?token=` on loopback sockets (a WebSocket
 *  cannot carry an Authorization header from the browser API); never added for another host. */
/** MetaCodex: the MQH API token for a loopback URL (MQH_API_TOKEN, else ~/.mqh/api-token), null for any other host. */
export function apiTokenForUrl(url: string, env: NodeJS.ProcessEnv = process.env): string | null {
  try {
    if (!["127.0.0.1", "localhost", "[::1]", "::1"].includes(new URL(url).hostname)) return null;
  } catch {
    return null;
  }
  const fromEnv = env.MQH_API_TOKEN?.trim() ?? "";
  if (fromEnv.length > 0) return fromEnv;
  const dataDir = env.MQH_DATA_DIR?.trim() || path.join(homedir(), ".mqh");
  try {
    const fromFile = readFileSync(path.join(dataDir, "api-token"), "utf8").trim();
    return fromFile.length > 0 ? fromFile : null;
  } catch {
    return null;
  }
}

export function withApiToken(url: string, env: NodeJS.ProcessEnv = process.env): string {
  const token = apiTokenForUrl(url, env);
  if (token === null) return url;
  try {
    const parsed = new URL(url);
    if (parsed.searchParams.has("token")) return url;
    parsed.searchParams.set("token", token);
    return parsed.toString();
  } catch {
    return url;
  }
}

/** MetaCodex: a fetch that adds `Authorization: Bearer <MQH API token>` to loopback requests that carry none (the
 *  server's own plugins and the CLI call MQH's public API; with the token set it answers 401 otherwise). */
export function withApiTokenFetch(inner: typeof fetch): typeof fetch {
  return (input, init) => {
    const url = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
    const token = apiTokenForUrl(url);
    if (token === null) return inner(input, init);
    const headers = new Headers(init?.headers ?? (input instanceof Request ? input.headers : undefined));
    if (!headers.has("authorization")) headers.set("authorization", `Bearer ${token}`);
    return inner(input, { ...init, headers });
  };
}

export function createNodeWebsocketFactory(): BbRealtimeSocketFactory {
  return (url) => {
    const target = withApiToken(url);
    if (typeof WebSocket !== "undefined") {
      return wrapStandardWebsocket(new WebSocket(target));
    }
    return wrapNodeWsWebsocket(target);
  };
}
