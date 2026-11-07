import http from "node:http";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { createServer as createNetServer } from "node:net";
import { tmpdir } from "node:os";
import path from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  HOST_DAEMON_PROTOCOL_VERSION,
  createHostDaemonLocalClient,
} from "@bb/host-daemon-contract";
import { startLocalApiServer, type LocalApiServer } from "./local-api.js";
import { resolveHostPlatform } from "./host-platform.js";
import type { HostDaemonLocalApiConfig } from "./local-api-config.js";

describe("local API server", () => {
  let server: LocalApiServer | null = null;

  function createLocalApiConfig(
    overrides: Partial<HostDaemonLocalApiConfig> = {},
  ): HostDaemonLocalApiConfig {
    return {
      bindHost: "localhost",
      healthPath: "/health",
      healthValue: "ok",
      port: 0,
      ...overrides,
    };
  }

  afterEach(async () => {
    await server?.close();
    server = null;
    vi.useRealTimers();
  });

  it("rejects a foreign browser origin instead of only withholding CORS", async () => {
    server = await startLocalApiServer({
      hostId: "host-1",
      localApiConfig: createLocalApiConfig(),
      serverUrl: "http://server.test",
      serverPort: 3334,
      devAppPort: 5173,
      getConnected: () => true,
    });
    const baseUrl = `http://localhost:${server.port}`;

    async function getStatus(headers: Record<string, string>): Promise<number> {
      const response = await fetch(`${baseUrl}/status`, { headers });
      return response.status;
    }

    expect(await getStatus({ origin: "http://127.0.0.1:3009" })).toBe(403);
    expect(await getStatus({ origin: "http://rebind.example" })).toBe(403);
    expect(await getStatus({ origin: "http://localhost:3334" })).toBe(200);
    expect(await getStatus({})).toBe(200);
  });

  it("allows the exact remote server origin the daemon is enrolled with", async () => {
    server = await startLocalApiServer({
      hostId: "host-remote",
      localApiConfig: createLocalApiConfig(),
      serverUrl: "https://remote-bb.example.test/projects/proj_1",
      serverPort: 0,
      getConnected: () => true,
    });

    const response = await fetch(`http://localhost:${server.port}/status`, {
      headers: { Origin: "https://remote-bb.example.test" },
    });

    expect(response.status).toBe(200);
    expect(response.headers.get("access-control-allow-origin")).toBe(
      "https://remote-bb.example.test",
    );
  });

  it("rejects a DNS-rebound origin that matches its own Host", async () => {
    server = await startLocalApiServer({
      hostId: "host-1",
      localApiConfig: createLocalApiConfig(),
      serverUrl: "http://server.test",
      serverPort: 3334,
      devAppPort: 5173,
      getConnected: () => true,
    });
    const { port } = server;
    const { bindHost } = server;

    function get(headers: Record<string, string>): Promise<number> {
      return new Promise((resolve, reject) => {
        const request = http.request(
          { host: bindHost, port, path: "/status", method: "GET", headers },
          (response) => {
            response.resume();
            resolve(response.statusCode ?? 0);
          },
        );
        request.once("error", reject);
        request.end();
      });
    }

    expect(
      await get({
        origin: `http://rebind.example:${port}`,
        host: `rebind.example:${port}`,
      }),
    ).toBe(403);
    expect(
      await get({
        origin: `http://${bindHost}:${port}`,
        host: `${bindHost}:${port}`,
      }),
    ).toBe(200);
  });

  it("serves host identity and status over localhost", async () => {
    server = await startLocalApiServer({
      hostId: "host-1",
      localApiConfig: createLocalApiConfig(),
      serverUrl: "http://server.test",
      serverPort: 3334,
      devAppPort: 5173,
      getConnected: () => true,
    });
    const client = createHostDaemonLocalClient(
      `http://localhost:${server.port}`,
    );

    const statusResponse = await client.status.$get();

    expect(await statusResponse.json()).toEqual({
      hostId: "host-1",
      connected: true,
      protocolVersion: HOST_DAEMON_PROTOCOL_VERSION,
      serverUrl: "http://server.test",
      supportsNativeFolderPicker: process.platform === "darwin",
      platform: resolveHostPlatform(),
    });
    const healthResponse = await client.health.$get();
    expect(await healthResponse.text()).toBe("ok");
  });

  it("explains how to resolve a local API port collision", async () => {
    const occupied = createNetServer();
    await new Promise<void>((resolve, reject) => {
      occupied.once("error", reject);
      occupied.listen(0, "127.0.0.1", resolve);
    });
    const address = occupied.address();
    if (address === null || typeof address === "string") {
      throw new Error("Expected occupied test server to have a TCP address");
    }

    try {
      await expect(
        startLocalApiServer({
          hostId: "host-1",
          localApiConfig: createLocalApiConfig({
            bindHost: "127.0.0.1",
            port: address.port,
          }),
          serverUrl: "http://server.test",
          serverPort: 3334,
          getConnected: () => true,
        }),
      ).rejects.toThrow(
        `Host daemon local API port ${address.port} is already in use on 127.0.0.1. Choose another port with --host-daemon-port <port>.`,
      );
    } finally {
      await new Promise<void>((resolve, reject) => {
        occupied.close((error) => (error ? reject(error) : resolve()));
      });
    }
  });

  it("retries client configuration after an invalid file is repaired", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(0);
    const dataDir = await mkdtemp(path.join(tmpdir(), "bb-client-config-"));
    const configPath = path.join(dataDir, "client.json");
    const origin = "https://remote-bb.example.test";
    try {
      await writeFile(configPath, "{");
      server = await startLocalApiServer({
        dataDir,
        hostId: "host-1",
        localApiConfig: createLocalApiConfig(),
        serverUrl: "http://server.test",
        serverPort: 3334,
        getConnected: () => true,
      });
      const url = `http://localhost:${server.port}/status`;
      expect((await fetch(url, { headers: { origin } })).status).toBe(403);
      await writeFile(
        configPath,
        JSON.stringify({ servers: { [origin]: {} } }),
      );
      expect((await fetch(url, { headers: { origin } })).status).toBe(200);
    } finally {
      await rm(dataDir, { recursive: true, force: true });
    }
  });

});
