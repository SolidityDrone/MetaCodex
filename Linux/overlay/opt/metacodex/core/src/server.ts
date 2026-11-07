// mcx-core: the MetaCodex control plane daemon (server + this machine's host in one process).
// Usage: node /opt/metacodex/core/src/server.ts [--port 38990] [--data ~/.metacodex] [--work /work]
import { chmodSync, existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { randomBytes } from "node:crypto";
import { homedir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { createApi, VERSION } from "./http.ts";
import { claudeDriver } from "./providers/claude.ts";
import { replayDriver } from "./providers/replay.ts";
import { Store } from "./store.ts";
import { ThreadService } from "./threads.ts";
import type { ProviderDriver } from "./types.ts";

export interface CoreOptions {
  port: number;
  dataDir: string;
  workRoot: string;
  token?: string;
  drivers?: ProviderDriver[];
  defaultProvider?: string;
  quiet?: boolean;
}

export async function startCore(o: CoreOptions) {
  mkdirSync(o.dataDir, { recursive: true });
  mkdirSync(o.workRoot, { recursive: true });
  const tokenFile = join(o.dataDir, "token");
  let token = o.token ?? process.env.MCX_TOKEN;
  if (!token) {
    token = existsSync(tokenFile) ? readFileSync(tokenFile, "utf8").trim() : randomBytes(24).toString("hex");
    writeFileSync(tokenFile, token + "\n");
    chmodSync(tokenFile, 0o600);
  }
  const here = dirname(fileURLToPath(import.meta.url));
  const drivers = o.drivers ?? [replayDriver(join(here, "..", "tapes")), claudeDriver()];
  const store = new Store(join(o.dataDir, "core.db"));
  const url = `http://127.0.0.1:${o.port}`;
  const svc = new ThreadService(store, drivers, {
    workRoot: o.workRoot,
    defaultProvider: o.defaultProvider ?? "replay",
    cliPath: join(here, "cli.ts"),
    serverUrl: url,
  });
  if (store.projects().length === 0) svc.createProject("scratch");
  const { server, hub } = createApi(svc, token, async () => ({
    providers: await Promise.all(drivers.map(async (d) => ({ id: d.id, label: d.label, ...(await d.available()) }))),
    machine: { id: "local", name: process.env.MCX_MACHINE ?? "quest" },
    dataDir: o.dataDir,
  }));
  await new Promise<void>((r) => server.listen(o.port, "127.0.0.1", r));
  const port = (server.address() as { port: number }).port;
  if (!o.quiet) console.log(`mcx-core ${VERSION} on 127.0.0.1:${port} (data ${o.dataDir})`);
  const close = async () => {
    await svc.shutdown();
    hub.closeAll();
    const closed = new Promise<void>((r) => server.close(() => r()));
    server.closeAllConnections(); // idle keep-alive sockets would hold close() open forever
    await closed;
    store.close();
  };
  return { svc, server, port, token, close };
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const arg = (n: string, d: string) => { const i = process.argv.indexOf(n); return i > 0 ? process.argv[i + 1] : d; };
  const core = await startCore({
    port: Number(arg("--port", process.env.MCX_PORT ?? "38990")),
    dataDir: arg("--data", join(homedir(), ".metacodex")),
    workRoot: arg("--work", existsSync("/work") ? "/work" : join(homedir(), "work")),
    defaultProvider: process.env.MCX_DEFAULT_PROVIDER,
  });
  const quit = async () => { await core.close(); process.exit(0); };
  process.on("SIGTERM", quit);
  process.on("SIGINT", quit);
}
