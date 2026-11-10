// MetaCodex: MQH's API token. On Android every app on the headset can reach 127.0.0.1, so with a token set the
// public API, the browser sockets (terminals!) and the daemon enrollment answer only to the holder: the
// MetaCodex app (it keeps the token in its vault and starts MQH with it) and the `mqh` CLI in the headset's
// Linux (it reads ~/.mqh/api-token). Plugin HTTP routes (/api/v1/plugins/:id/http/..., e.g. the MCP endpoint
// MQH hands to agents) keep their own credentials and are not gated here. No token set: unchanged (desktop).
import { timingSafeEqual } from "node:crypto";
import { readFileSync } from "node:fs";
import path from "node:path";

export const API_TOKEN_FILE = "api-token";

/** MQH_API_TOKEN, else <dataDir>/api-token; null when neither is set (no gate). */
export function resolveApiToken(
  dataDir: string,
  env: NodeJS.ProcessEnv = process.env,
): string | null {
  const fromEnv = env.MQH_API_TOKEN?.trim();
  if (fromEnv) return fromEnv;
  try {
    const fromFile = readFileSync(path.join(dataDir, API_TOKEN_FILE), "utf8").trim();
    return fromFile.length > 0 ? fromFile : null;
  } catch {
    return null;
  }
}

const PLUGIN_HTTP_ROUTE = /^\/api\/v1\/plugins\/[^/]+\/http(?:\/|$)/u;

/** The paths the token guards. */
export function requiresApiToken(requestPath: string): boolean {
  if (requestPath.startsWith("/api/v1/")) return !PLUGIN_HTTP_ROUTE.test(requestPath);
  if (requestPath === "/ws" || requestPath.startsWith("/ws/")) return true;
  return requestPath === "/internal/hosts/enroll-key";
}

function sameString(a: string, b: string): boolean {
  const bufferA = Buffer.from(a);
  const bufferB = Buffer.from(b);
  return bufferA.length === bufferB.length && timingSafeEqual(bufferA, bufferB);
}

/** `Authorization: Bearer <token>`, or `?token=` for WebSocket clients that cannot set headers. */
export function presentsApiToken(
  token: string,
  authorizationHeader: string | undefined,
  queryToken: string | undefined,
): boolean {
  if (authorizationHeader !== undefined && sameString(authorizationHeader.trim(), `Bearer ${token}`)) {
    return true;
  }
  return queryToken !== undefined && sameString(queryToken, token);
}

export function apiTokenRequiredResponse(): Response {
  return new Response(
    JSON.stringify({ code: "api_token_required", message: "MQH needs its API token (Authorization: Bearer)" }),
    { status: 401, headers: { "content-type": "application/json" } },
  );
}
