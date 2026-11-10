import { readFileSync } from "node:fs";
import { homedir } from "node:os";
import path from "node:path";
import { createNodeBbSdk, type BbSdk } from "@bb/sdk/node";
import type { Dispatcher } from "undici";

type CliRequestInit = RequestInit & { dispatcher?: Dispatcher };

const LOOPBACK_HOSTS = new Set(["127.0.0.1", "localhost", "[::1]", "::1"]);

function requestUrl(input: RequestInfo | URL): string {
  if (typeof input === "string") return input;
  if (input instanceof URL) return input.href;
  return input.url;
}

/** MetaCodex: the API token of the MQH on this machine (MQH_API_TOKEN, else ~/.mqh/api-token, which the headset's
 *  launcher writes), sent only to a loopback server: never to another host. */
export function apiTokenFor(url: string, env: NodeJS.ProcessEnv = process.env): string | null {
  try {
    if (!LOOPBACK_HOSTS.has(new URL(url).hostname)) return null;
  } catch {
    return null;
  }
  const fromEnv = env.MQH_API_TOKEN?.trim();
  if (fromEnv) return fromEnv;
  const dataDir = env.MQH_DATA_DIR?.trim() || path.join(homedir(), ".mqh");
  try {
    const fromFile = readFileSync(path.join(dataDir, "api-token"), "utf8").trim();
    return fromFile.length > 0 ? fromFile : null;
  } catch {
    return null;
  }
}

export function cliFetch(
  input: RequestInfo | URL,
  init?: CliRequestInit,
): Promise<Response> {
  const token = apiTokenFor(requestUrl(input));
  if (token === null) return fetch(input, init);
  const headers = new Headers(
    init?.headers ?? (input instanceof Request ? input.headers : undefined),
  );
  if (!headers.has("authorization")) headers.set("authorization", `Bearer ${token}`);
  return fetch(input, { ...init, headers });
}

function cliFetchWithTimeout(timeoutMs: number): typeof cliFetch {
  return (input, init) => {
    const timeoutSignal = AbortSignal.timeout(timeoutMs);
    return cliFetch(input, {
      ...init,
      signal: init?.signal
        ? AbortSignal.any([init.signal, timeoutSignal])
        : timeoutSignal,
    });
  };
}

export function createCliBbSdk(baseUrl: string, timeoutMs?: number): BbSdk {
  return createNodeBbSdk({
    baseUrl,
    fetch: timeoutMs === undefined ? cliFetch : cliFetchWithTimeout(timeoutMs),
  });
}
