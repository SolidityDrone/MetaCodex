import fs from "node:fs/promises";
import { createAsyncTtlMemo } from "@bb/async-cache";
import { serve } from "@hono/node-server";
import {
  buildLocalAppOrigins,
  type BuildLocalAppOriginsArgs,
} from "@bb/config/local-app-origins";
import {
  formatClientConfigPath,
  normalizeClientServerOrigin,
  parseClientConfig,
  type ClientConfig,
} from "@bb/config/client-config";
import { assignIfDefined } from "@bb/config/objects";
import {
  healthResponseSchema,
  HOST_DAEMON_PROTOCOL_VERSION,
  typedRoutes,
  type HostDaemonLocalSchema,
} from "@bb/host-daemon-contract";
import { Hono } from "hono";
import { cors } from "hono/cors";
import { isFsErrorWithCode } from "./fs-errors.js";
import type { HostDaemonLocalApiConfig } from "./local-api-config.js";
import { resolveHostPlatform } from "./host-platform.js";

interface StartLocalApiServerOptions {
  dataDir?: string;
  hostId: string;
  localApiConfig: HostDaemonLocalApiConfig;
  serverUrl: string;
  serverPort: number;
  devAppPort?: number;
  appUrl?: string;
  getConnected: () => boolean;
}

export interface LocalApiServer {
  bindHost: string;
  port: number;
  close(): Promise<void>;
}

interface ClientConfigLoader {
  load(): Promise<ClientConfig>;
}

const CLIENT_CONFIG_CACHE_TTL_MS = 1_000;
const EMPTY_CLIENT_CONFIG: ClientConfig = { servers: {} };

function isSelfEvidentLocalHostname(hostname: string): boolean {
  if (hostname === "localhost" || hostname.endsWith(".localhost")) {
    return true;
  }
  if (hostname.startsWith("[") && hostname.endsWith("]")) {
    return true;
  }
  return /^\d{1,3}(?:\.\d{1,3}){3}$/u.test(hostname);
}

function createClientConfigLoader(
  dataDir: string | undefined,
  nowMs: () => number = Date.now,
): ClientConfigLoader {
  const cache = createAsyncTtlMemo<string, ClientConfig>({
    ttlMs: CLIENT_CONFIG_CACHE_TTL_MS,
    now: nowMs,
    maxEntries: 1,
  });

  return {
    async load(): Promise<ClientConfig> {
      if (dataDir === undefined) {
        return EMPTY_CLIENT_CONFIG;
      }
      return cache.run(dataDir, () => readClientConfig(dataDir));
    },
  };
}

async function readClientConfig(dataDir: string): Promise<ClientConfig> {
  try {
    return parseClientConfig(
      JSON.parse(await fs.readFile(formatClientConfigPath(dataDir), "utf8")),
    );
  } catch (error) {
    if (!isFsErrorWithCode(error, "ENOENT")) {
      throw error;
    }
    return EMPTY_CLIENT_CONFIG;
  }
}

async function isConfiguredClientOrigin(
  origin: string,
  configLoader: ClientConfigLoader,
): Promise<boolean> {
  try {
    const serverOrigin = normalizeClientServerOrigin(origin);
    const config = await configLoader.load();
    return config.servers[serverOrigin] !== undefined;
  } catch {
    return false;
  }
}

export async function startLocalApiServer(
  options: StartLocalApiServerOptions,
): Promise<LocalApiServer> {
  const app = new Hono();
  const clientConfigLoader = createClientConfigLoader(options.dataDir);
  const originArgs: BuildLocalAppOriginsArgs = {
    serverPort: options.serverPort,
  };
  assignIfDefined({
    key: "appUrl",
    target: originArgs,
    value: options.appUrl,
  });
  assignIfDefined({
    key: "devAppPort",
    target: originArgs,
    value: options.devAppPort,
  });
  const allowedCorsOrigins = new Set<string>(buildLocalAppOrigins(originArgs));
  try {
    allowedCorsOrigins.add(new URL(options.serverUrl).origin);
  } catch {}
  const isAllowedAppOrigin = async (
    origin: string,
    requestUrl: string,
  ): Promise<boolean> => {
    if (
      allowedCorsOrigins.has(origin) ||
      (await isConfiguredClientOrigin(origin, clientConfigLoader))
    ) {
      return true;
    }
    let originUrl: URL;
    try {
      originUrl = new URL(origin);
    } catch {
      return false;
    }
    return (
      isSelfEvidentLocalHostname(originUrl.hostname) &&
      origin === new URL(requestUrl).origin
    );
  };

  app.use(
    "*",
    cors({
      origin: async (origin, context) =>
        (await isAllowedAppOrigin(origin, context.req.url)) ? origin : null,
    }),
  );

  app.get(options.localApiConfig.healthPath, (c) =>
    c.text(healthResponseSchema.parse(options.localApiConfig.healthValue)),
  );
  app.use("*", async (c, next) => {
    const origin = c.req.header("origin");
    if (
      origin !== undefined &&
      !(await isAllowedAppOrigin(origin, c.req.url))
    ) {
      return c.json(
        { error: `origin "${origin}" is not a local BB app origin` },
        403,
      );
    }
    await next();
  });

  const { get } = typedRoutes<HostDaemonLocalSchema>(app);
  const platform = resolveHostPlatform();

  get("/status", (c) =>
    c.json({
      hostId: options.hostId,
      connected: options.getConnected(),
      protocolVersion: HOST_DAEMON_PROTOCOL_VERSION,
      serverUrl: options.serverUrl,
      supportsNativeFolderPicker: platform === "darwin",
      platform,
    }),
  );

  // MQH: no desktop "open in editor / file manager" targets on a headset.

  let boundServer: {
    server: ReturnType<typeof serve>;
    port: number;
  };
  try {
    boundServer = await new Promise<{
      server: ReturnType<typeof serve>;
      port: number;
    }>((resolve, reject) => {
      const s = serve(
        {
          fetch: app.fetch,
          port: options.localApiConfig.port,
          hostname: options.localApiConfig.bindHost,
        },
        (info) => resolve({ server: s, port: info.port }),
      );
      s.on("error", reject);
    });
  } catch (error) {
    if (isFsErrorWithCode(error, "EADDRINUSE")) {
      throw new Error(
        `Host daemon local API port ${options.localApiConfig.port} is already in use on ${options.localApiConfig.bindHost}. Choose another port with --host-daemon-port <port>.`,
        { cause: error },
      );
    }
    throw error;
  }

  const { server, port: boundPort } = boundServer;

  return {
    bindHost: options.localApiConfig.bindHost,
    port: boundPort,
    async close(): Promise<void> {
      await new Promise<void>((resolve, reject) => {
        server.close((error) => {
          if (error) {
            reject(error);
            return;
          }
          resolve();
        });
      });
    },
  };
}
