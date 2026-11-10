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
export function withApiToken(url: string, env: NodeJS.ProcessEnv = process.env): string {
  try {
    const parsed = new URL(url);
    if (!["127.0.0.1", "localhost", "[::1]", "::1"].includes(parsed.hostname)) return url;
    if (parsed.searchParams.has("token")) return url;
    let token = env.MQH_API_TOKEN?.trim() ?? "";
    if (token.length === 0) {
      const dataDir = env.MQH_DATA_DIR?.trim() || path.join(homedir(), ".mqh");
      try {
        token = readFileSync(path.join(dataDir, "api-token"), "utf8").trim();
      } catch {
        token = "";
      }
    }
    if (token.length === 0) return url;
    parsed.searchParams.set("token", token);
    return parsed.toString();
  } catch {
    return url;
  }
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
