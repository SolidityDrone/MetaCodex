import { createPluginUpdateJobs } from "./services/plugins/plugin-update-jobs.js";
import { registerPluginUpdateJobRoutes } from "./routes/plugin-update-jobs.js";
import {
  recheckEnvironmentProvisioning,
  recheckEnvironmentProviderCreations,
} from "./services/threads/thread-environment-providers.js";
import {
  enrolledInstallerScript,
  enrolledWindowsInstallerScript,
  windowsInstallerFailureScript,
} from "./services/machines/manual-enrollment-command.js";
import { reconnectBootstrapForCredential } from "./services/machines/reconnect.js";
import {
  apiTokenRequiredResponse,
  presentsApiToken,
  requiresApiToken,
  resolveApiToken,
} from "./api-token.js";
import { getMachineEnrollmentService } from "./services/machines/machine-services.js";
import { withManualMachineProvider } from "./services/machines/manual-provider.js";
import { registerDesktopBrowserRoutes } from "./routes/desktop-browsers.js";
import {
  INSTALL_MACHINE_SCRIPT_PATH,
  INSTALL_MACHINE_WINDOWS_SCRIPT_PATH,
} from "./install-machine-asset.js";
import { createNodeWebSocket } from "@hono/node-ws";
import {
  declineWebSocketCompression,
  WEB_SOCKET_COMPRESSION,
} from "./ws/compression.js";
import { randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import { performance } from "node:perf_hooks";
import { Hono } from "hono";
import { terminalWebSocketQuerySchema } from "@bb/server-contract";
import { compress } from "hono/compress";
import { cors } from "hono/cors";
import type { ServerAppDeps } from "./types.js";
import { ApiError, createServerErrorHandler } from "./errors.js";
import { registerEnvironmentRoutes } from "./routes/environments.js";
import { registerFileRoutes } from "./routes/files.js";
import { registerHostRoutes } from "./routes/hosts.js";
import { registerProjectRoutes } from "./routes/projects.js";
import { registerThreadSectionRoutes } from "./routes/thread-sections.js";
import { registerSystemRoutes } from "./routes/system.js";
import { registerUiPreferenceRoutes } from "./routes/ui-preferences.js";
import { registerTerminalRoutes } from "./routes/terminals.js";
import { registerThreadRoutes } from "./routes/threads/index.js";
import { registerQueueRoutes } from "./routes/queue.js";
import { registerPluginRoutes } from "./routes/plugins.js";
import { registerPluginCatalogRoutes } from "./routes/plugin-catalog.js";
import { registerPluginInstallJobRoutes } from "./routes/plugin-install-jobs.js";
import { createPluginInstallJobs } from "./services/plugins/plugin-install-jobs.js";
import { registerPromptHistoryRoutes } from "./routes/prompt-history.js";
import { registerSkillsRegistryRoutes } from "./routes/skills-registry.js";
import {
  createPluginService,
  type PluginService,
} from "./services/plugins/plugin-service.js";
import { setPluginAgentContributions } from "./services/plugins/plugin-agent-contributions.js";
import { setPluginThreadEventEmitter } from "./services/plugins/plugin-thread-events.js";
import { setPathPluginPreserver } from "./services/plugins/path-plugin-preservation.js";
import { setPluginHookProvider } from "./services/plugins/plugin-hook-registry.js";
import {
  setEnvironmentProviderRecheckHandler,
  setEnvironmentProvisioningRecheckHandler,
  setPluginEnvironmentProviderBridge,
} from "./services/plugins/plugin-environment-provider-registry.js";
import {
  setServerAccessBridge,
  setServerAccessRecheckHandler,
} from "./services/plugins/plugin-server-access-registry.js";
import { setPluginMachineProviderBridge } from "./services/plugins/plugin-machine-provider-registry.js";
import { invalidateEnvironmentProviderMachineAvailability } from "./services/environments/provider-machine-availability.js";
import { requestQueuedMessageDispatch } from "./services/threads/queued-message-dispatch.js";
import { registerInternalEventRoutes } from "./internal/events.js";
import { registerInternalHostRoutes } from "./internal/hosts.js";
import { registerInternalInteractiveRequestRoutes } from "./internal/interactive-requests.js";
import { hostArtifactFileResponse } from "./internal/host-artifact-response.js";
import { registerInternalPluginHostArtifactRoutes } from "./internal/plugin-host-artifacts.js";
import { registerInternalSessionRoutes } from "./internal/session.js";
import { registerInternalSkillRoutes } from "./internal/skills.js";
import { registerInternalToolCallRoutes } from "./internal/tool-calls.js";
import {
  setAuthenticatedDaemon,
  verifyAuthenticatedDaemon,
} from "./internal/auth.js";
import {
  captureTrustedRemoteAddress,
  resolveRequestAccessRoute,
  resolveRequestAppSurface,
} from "./request-context.js";
import { runEventLoopWork } from "./services/system/event-loop-work.js";
import { setTurnTelemetryDeps } from "./services/system/turn-telemetry.js";
import { runWithTelemetryRequest } from "./services/system/telemetry.js";
import {
  onClientSocketClose,
  onClientSocketMessage,
  onClientSocketOpen,
} from "./ws/client-protocol.js";
import {
  onDaemonSocketClose,
  onDaemonSocketMessage,
  onDaemonSocketOpen,
  validateDaemonWebSocket,
} from "./ws/daemon-protocol.js";
import { roundDurationMs } from "@bb/process-utils";
import {
  onTerminalSocketClose,
  onTerminalSocketMessage,
  onTerminalSocketOpen,
} from "./ws/terminal-protocol.js";
import {
  createBbAppArtifactService,
  type BbAppArtifactService,
} from "./services/install/bb-app-artifact.js";
import { HOST_DAEMON_PROTOCOL_VERSION } from "@bb/host-daemon-contract";
import {
  createPluginCatalogService,
  type PluginCatalogService,
} from "./services/plugin-catalog/plugin-catalog-service.js";
import {
  allowedAppOrigins,
  browserRequestProblem,
  requestHostProblem,
} from "./browser-request-guard.js";
import {
  callPluginHostRpc,
  disposePluginHostWorkers,
} from "./services/plugins/plugin-host-rpc.js";

const PLUGIN_WIRE_HTTP_PATH = /^\/api\/v1\/plugins\/[^/]+\/http(?:\/|$)/u;
import { apiJsonCompression } from "./api-response-compression.js";
import { APP_SURFACE_WEB, type AppSurface } from "@bb/config/app-surface";
import type { ServerBindHost } from "@bb/config/server";
import { registerServerMoveRoutes } from "./routes/server-move.js";
import {
  INTERNAL_SERVER_MOVE_PENDING_PATH,
  registerInternalServerMoveRoutes,
} from "./internal/server-move.js";
import {
  createServerMoveCoordinator,
  type ServerMoveCoordinator,
} from "./services/server-move/coordinator.js";
import { createDefaultServerMoveEnvironment } from "./services/server-move/environment.js";
import { readServerMoveHealth } from "./services/server-move/health.js";
import type { RestoredServerMoveRun } from "./services/server-move/reconcile.js";
import {
  serverMoveFreezeMiddleware,
  serverMoveWriteFreezeMiddleware,
} from "./services/server-move/freeze.js";
import { isServerMoveSnapshotFenced } from "./services/server-move/freeze-state.js";
import {
  createManualServerImportCompletion,
  type PendingServerMove,
} from "./services/server-move/pending-boot.js";

type CloseWebSockets = () => Promise<void>;
type NodeWebSocketServer = ReturnType<typeof createNodeWebSocket>["wss"];
type WebSocketCloseError = Error | undefined;

interface ServerApp {
  app: Hono;
  closeWebSockets: CloseWebSockets;
  injectWebSocket: ReturnType<typeof createNodeWebSocket>["injectWebSocket"];
  pluginService: PluginService;
  pluginCatalogService: PluginCatalogService;
  serverMove: ServerMoveCoordinator;
}

interface CloseWebSocketServerArgs {
  forceCloseAfterMs: number;
  reason: string;
  server: NodeWebSocketServer;
}

function unauthorizedResponse(): Response {
  return new Response(
    JSON.stringify({ code: "unauthorized", message: "Unauthorized" }),
    {
      status: 401,
      headers: { "content-type": "application/json" },
    },
  );
}

function normalizeInternalAuthPath(path: string): string {
  if (path === "/") {
    return path;
  }
  return path.replace(/\/+$/u, "");
}

export interface ServerMoveAppOptions {
  appSurface: AppSurface;
  bindHost: ServerBindHost | null;
  manualImportPending: boolean;
  pending: PendingServerMove | null;
  restoredRun: RestoredServerMoveRun | null;
  retireProcess(): void;
}

interface CreateAppOptions {
  bbAppArtifactService?: BbAppArtifactService;
  serverMove?: ServerMoveAppOptions;
  slowApiRequestLogThresholdMs?: number;
  performanceDiagnosticsEnabled?: () => boolean;
}

const WEB_SOCKET_SHUTDOWN_CODE = 1001;
const WEB_SOCKET_SHUTDOWN_FORCE_CLOSE_MS = 1_000;
const WEB_SOCKET_SHUTDOWN_REASON = "server-shutdown";
const SLOW_API_REQUEST_LOG_THRESHOLD_MS = 1_000;
const THREAD_EVENT_WAIT_PATH_PATTERN =
  /^\/api\/v1\/threads\/[^/]+\/events\/wait$/u;
interface ShouldLogSlowApiRequestArgs {
  durationMs: number;
  path: string;
  thresholdMs: number;
}

function shouldLogSlowApiRequest(args: ShouldLogSlowApiRequestArgs): boolean {
  if (args.durationMs < args.thresholdMs) {
    return false;
  }
  return !THREAD_EVENT_WAIT_PATH_PATTERN.test(args.path);
}


function closeWebSocketServer(args: CloseWebSocketServerArgs): Promise<void> {
  for (const client of args.server.clients) {
    client.close(WEB_SOCKET_SHUTDOWN_CODE, args.reason);
  }

  return new Promise<void>((resolvePromise, reject) => {
    const forceCloseTimeout = setTimeout(() => {
      for (const client of args.server.clients) {
        client.terminate();
      }
    }, args.forceCloseAfterMs);
    forceCloseTimeout.unref();

    args.server.close((error: WebSocketCloseError) => {
      clearTimeout(forceCloseTimeout);
      if (error) {
        reject(error);
        return;
      }
      resolvePromise();
    });
  });
}

export function createApp(
  deps: ServerAppDeps,
  options?: CreateAppOptions,
): ServerApp {
  const app = new Hono();
  const { injectWebSocket, upgradeWebSocket, wss } = createNodeWebSocket({
    app,
  });
  wss.options.perMessageDeflate = WEB_SOCKET_COMPRESSION;
  const slowApiRequestLogThresholdMs =
    options?.slowApiRequestLogThresholdMs ?? SLOW_API_REQUEST_LOG_THRESHOLD_MS;
  const bbAppArtifactService =
    options?.bbAppArtifactService ??
    createBbAppArtifactService({
      dataDir: deps.config.dataDir,
      serverEntryUrl: import.meta.url,
    });
  const serverMoveOptions: ServerMoveAppOptions = options?.serverMove ?? {
    appSurface: APP_SURFACE_WEB,
    bindHost: null,
    manualImportPending: false,
    pending: null,
    restoredRun: null,
    retireProcess: () => {
      deps.logger.warn(
        {},
        "Server move finished, but this server has no process retire hook",
      );
    },
  };
  const pendingServerMove = serverMoveOptions.pending;

  app.use("*", async (context, next) => {
    const signal = context.req.raw.signal;
    await next();
    if (signal.aborted) await context.res.body?.cancel(signal.reason);
  });
  app.use("*", async (context, next) => {
    const problem = requestHostProblem(context, deps);
    if (problem !== null) {
      throw new ApiError(problem.status, "forbidden_host", problem.error);
    }
    return next();
  });
  // MetaCodex: with an API token set (the headset), the public API and the sockets answer only to its holder
  const apiToken = resolveApiToken(deps.config.dataDir);
  if (apiToken !== null) {
    app.use("*", async (context, next) => {
      if (!requiresApiToken(context.req.path)) return next();
      if (presentsApiToken(apiToken, context.req.header("authorization"), context.req.query("token"))) {
        return next();
      }
      return apiTokenRequiredResponse();
    });
  }
  app.use("*", async (context, next) => {
    captureTrustedRemoteAddress(context);
    return runWithTelemetryRequest(
      {
        appSurface: resolveRequestAppSurface(context),
        accessRoute: resolveRequestAccessRoute(context),
      },
      next,
    );
  });
  app.use("*", async (context, next) => {
    const path = context.req.path;
    if (!path.startsWith("/api/v1/") && !path.startsWith("/internal/")) {
      return next();
    }
    return runEventLoopWork(`${context.req.method} ${path}`, next);
  });
  app.use(
    "*",
    cors({
      origin: (origin, context) => {
        if (context.req.path === "/health") {
          return "*";
        }
        const allowedCorsOrigins = allowedAppOrigins(deps);
        const requestOrigin = new URL(context.req.url).origin;
        if (origin === requestOrigin || allowedCorsOrigins.has(origin)) {
          return origin;
        }
        return null;
      },
    }),
  );
  const compressResponse = compress();
  const compressApiJson = apiJsonCompression();
  app.use("*", (context, next) => {
    return compressResponse(context, async () => {
      await compressApiJson(context, next);
    });
  });
  app.onError(createServerErrorHandler(deps.logger));
  app.get("/health", async (context) => {
    const serverMove = await readServerMoveHealth({
      dataDir: deps.config.dataDir,
      pending: pendingServerMove,
    });
    return context.json({
      ok: true,
      ...(deps.config.launchId === undefined
        ? {}
        : { launchId: deps.config.launchId }),
      ...(serverMove === null ? {} : { serverMove }),
    });
  });
  app.get("/install.sh", async (context) => {
    const script = await readFile(INSTALL_MACHINE_SCRIPT_PATH, "utf8");
    const credential = context.req.header("X-BB-Enrollment");
    let bootstrap =
      credential === undefined
        ? null
        : await getMachineEnrollmentService(deps).pendingBootstrapForCredential(
            credential,
          );
    if (credential !== undefined && bootstrap === null) {
      try {
        bootstrap = await reconnectBootstrapForCredential(deps, credential);
      } catch (error) {
        deps.logger.warn({ error }, "Could not refresh machine access");
        return new Response(
          "echo 'Could not refresh machine access. Run the command again, or generate a new one in bb.' >&2\nexit 1\n",
          {
            status: 503,
            headers: {
              "cache-control": "no-store",
              "content-type": "text/x-shellscript; charset=utf-8",
            },
          },
        );
      }
    }
    if (credential !== undefined && bootstrap === null) {
      return new Response(
        "echo 'This enrollment command has already been used, replaced, or expired. Generate a new command in bb.' >&2\nexit 1\n",
        {
          status: 403,
          headers: {
            "cache-control": "no-store",
            "content-type": "text/x-shellscript; charset=utf-8",
          },
        },
      );
    }
    return new Response(
      bootstrap === null ? script : enrolledInstallerScript(script, bootstrap),
      {
        headers: {
          "cache-control": "no-store",
          "content-type": "text/x-shellscript; charset=utf-8",
        },
      },
    );
  });
  app.get("/install.ps1", async (context) => {
    const headers = {
      "cache-control": "no-store",
      "content-type": "text/plain; charset=utf-8",
    };
    const credential = context.req.header("X-BB-Enrollment");
    if (credential === undefined) {
      return new Response(
        windowsInstallerFailureScript(
          "This installer needs an enrollment command. Generate one in bb under Settings, Machines.",
        ),
        { headers },
      );
    }
    let bootstrap =
      await getMachineEnrollmentService(deps).pendingBootstrapForCredential(
        credential,
      );
    if (bootstrap === null) {
      try {
        bootstrap = await reconnectBootstrapForCredential(deps, credential);
      } catch (error) {
        deps.logger.warn({ error }, "Could not refresh machine access");
        return new Response(
          windowsInstallerFailureScript(
            "Could not refresh machine access. Run the command again, or generate a new one in bb.",
          ),
          { headers },
        );
      }
    }
    if (bootstrap === null) {
      return new Response(
        windowsInstallerFailureScript(
          "This enrollment command has already been used, replaced, or expired. Generate a new command in bb.",
        ),
        { headers },
      );
    }
    return new Response(
      enrolledWindowsInstallerScript(
        await readFile(INSTALL_MACHINE_WINDOWS_SCRIPT_PATH, "utf8"),
        bootstrap,
      ),
      { headers },
    );
  });
  app.get("/install/version", async (context) => {
    return context.json({
      version: await bbAppArtifactService.getVersion(),
      protocolVersion: HOST_DAEMON_PROTOCOL_VERSION,
    });
  });
  app.get("/install/bb-app.tgz", async (context) => {
    try {
      const artifact = await bbAppArtifactService.getArtifact();
      const etag = `"sha256-${artifact.digest}"`;
      const headers = {
        "cache-control": "public, max-age=300",
        "content-type": "application/gzip",
        etag,
        "x-bb-artifact-sha256": artifact.digest,
      };
      if (context.req.header("if-none-match") === etag) {
        return new Response(null, { headers, status: 304 });
      }
      const response = await hostArtifactFileResponse({
        path: artifact.path,
        byteLength: artifact.size,
        headers,
      });
      if (response === null) {
        throw new Error(`Host package ${artifact.path} is missing or changed`);
      }
      return response;
    } catch (error) {
      const diagnosticId = randomUUID();
      const code =
        error instanceof Error && "code" in error ? error.code : undefined;
      const reason =
        code === "ENOENT"
          ? "A required server package file or packaging tool is missing."
          : code === "EACCES" || code === "EPERM"
            ? "The server lacks permission to prepare or read the host package."
            : code === "ENOSPC"
              ? "The server ran out of disk space while preparing the host package."
              : "The server could not build or read its host package.";
      deps.logger.error(
        { err: error, diagnosticId },
        "Host package download failed",
      );
      return context.json(
        {
          code: "host_package_unavailable",
          message: `${reason} Check the server logs for diagnostic ID ${diagnosticId}, then retry installation.`,
          diagnosticId,
        },
        500,
        { "cache-control": "no-store" },
      );
    }
  });
  app.use("/api/v1/*", async (context, next) => {
    const startedAt = performance.now();
    await next();
    const durationMs = performance.now() - startedAt;
    const path = context.req.path;
    const diagnosticsEnabled =
      options?.performanceDiagnosticsEnabled?.() ?? false;
    if (
      shouldLogSlowApiRequest({
        durationMs,
        path,
        thresholdMs: diagnosticsEnabled ? 100 : slowApiRequestLogThresholdMs,
      })
    ) {
      const log = diagnosticsEnabled
        ? deps.logger.info.bind(deps.logger)
        : deps.logger.debug.bind(deps.logger);
      log(
        {
          durationMs: roundDurationMs(durationMs),
          method: context.req.method,
          path,
          status: context.res.status,
        },
        "Slow API request",
      );
    }
  });
  app.use("/internal/*", async (context, next) => {
    const normalizedPath = normalizeInternalAuthPath(context.req.path);
    if (normalizedPath === "/internal/hosts/enroll-key") {
      return next();
    }
    if (normalizedPath === "/internal/hosts/enroll") {
      return next();
    }
    if (normalizedPath === "/internal/ws") {
      return next();
    }
    if (normalizedPath === INTERNAL_SERVER_MOVE_PENDING_PATH) {
      return next();
    }
    try {
      const daemon = await verifyAuthenticatedDaemon(
        deps,
        context.req.header("authorization"),
      );
      setAuthenticatedDaemon(context, daemon);
    } catch {
      return unauthorizedResponse();
    }
    return next();
  });
  const pluginService = createPluginService({
    machineEnrollments: getMachineEnrollmentService(deps),
    db: deps.db,
    hub: deps.hub,
    logger: deps.logger,
    telemetry: deps.telemetry,
    pendingInteractions: deps.pendingInteractions,
    dataDir: deps.config.dataDir,
    appVersion: deps.config.appVersion,
    getAppUrl: () => deps.config.appUrl ?? null,
    sharedPorts: deps.sharedPorts,
    providerRegistry: deps.providerRegistry,
    pluginHostArtifacts: deps.pluginHostArtifacts,
    aiServices: deps.aiServices,
    callPluginHost: (args) => callPluginHostRpc(deps, args),
    disposePluginHost: (args) => disposePluginHostWorkers(deps, args),
    onSettingsChanged: (pluginId) => {
      deps.providerNativeRoots.invalidate(pluginId);
      deps.providerRegistry.forgetAllInstalled();
    },
    onPluginUnregistered: (pluginId) => {
      requestQueuedMessageDispatch(deps, {
        kind: "plugin-unregistered",
        pluginId,
      });
    },
    // `bb.experimental_hooks.recheck()`: a plugin whose wait condition
    // may have changed asks core to re-attempt the plugin-queued rows. Core
    // owns the walk, the coalescing and the pacing; the plugin owns knowing
    // when to ask.
    requestQueueDrain: () => {
      requestQueuedMessageDispatch(deps, { kind: "plugin-recheck" });
    },
    watchBuiltinPluginSources:
      process.env.MQH_MANAGED_DEV_BUILTIN_PLUGIN_HOT_RELOAD === "1",
  });
  // Messages queued while a thread awaited user interaction stop waiting once
  // that interaction settles (#1650); the idle drain then delivers them.
  deps.pendingInteractions.setThreadInteractionSettledListener((threadId) => {
    requestQueuedMessageDispatch(deps, {
      kind: "interaction-settled",
      threadId,
    });
  });
  setTurnTelemetryDeps(deps);
  setPluginThreadEventEmitter(pluginService.events);
  setPathPluginPreserver(deps.db, (directory) =>
    pluginService.preservePathPluginsInside(directory),
  );
  // Bridge the dispatch pipeline to this service's hooks. Until this runs
  // there are no hooks, which is exactly the zero-overhead path.
  setPluginHookProvider(pluginService.hooks);
  setPluginEnvironmentProviderBridge(pluginService.environmentProviders);
  setEnvironmentProvisioningRecheckHandler((threadId) =>
    recheckEnvironmentProvisioning(deps, threadId),
  );
  setPluginMachineProviderBridge(
    withManualMachineProvider(
      pluginService.machineProviders,
      getMachineEnrollmentService(deps),
    ),
  );
  setServerAccessBridge(pluginService.serverAccessProviders);
  setServerAccessRecheckHandler(() => {
    deps.hub.notifySystem(["config-changed"]);
  });
  setEnvironmentProviderRecheckHandler((pluginId) => {
    invalidateEnvironmentProviderMachineAvailability();
    deps.hub.notifySystem(["config-changed"]);
    void recheckEnvironmentProviderCreations(deps, pluginId);
  });
  // Bridge runtime-config assembly to plugin skills + context (§4.4).
  setPluginAgentContributions(pluginService);
  const serverMove = createServerMoveCoordinator(
    createDefaultServerMoveEnvironment({
      appSurface: serverMoveOptions.appSurface,
      bindHost: serverMoveOptions.bindHost,
      deps,
      env: process.env,
      pluginService,
      retireProcess: serverMoveOptions.retireProcess,
      serverEntryUrl: import.meta.url,
    }),
  );
  if (serverMoveOptions.restoredRun !== null) {
    serverMove.restore(serverMoveOptions.restoredRun);
  }
  const serverMoveFreezeState = {
    isFrozen: () => pendingServerMove !== null || serverMove.isFrozen(),
  };
  const daemonWriteFreezeState = {
    isFrozen: () =>
      pendingServerMove !== null || isServerMoveSnapshotFenced(deps.db),
  };
  app.use("/api/v1/*", serverMoveFreezeMiddleware(serverMoveFreezeState));
  for (const path of ["/internal/hosts/enroll", "/internal/hosts/enroll-key"]) {
    app.use(path, serverMoveWriteFreezeMiddleware(serverMoveFreezeState));
  }
  for (const path of [
    "/internal/session/events",
    "/internal/session/tool-call",
    "/internal/session/interactive-request",
    "/internal/session/interactive-request/interrupt",
  ]) {
    app.use(path, serverMoveWriteFreezeMiddleware(daemonWriteFreezeState));
  }
  const publicApi = new Hono();
  publicApi.use("*", async (context, next) => {
    if (PLUGIN_WIRE_HTTP_PATH.test(context.req.path)) {
      return next();
    }
    const problem = browserRequestProblem(context, deps);
    if (problem !== null) {
      throw new ApiError(problem.status, "forbidden_origin", problem.error);
    }
    return next();
  });
  const pluginCatalogService = createPluginCatalogService({
    db: deps.db,
    appVersion: deps.config.appVersion,
    marketplaceUrl: deps.config.marketplaceUrl,
    dataDir: deps.config.dataDir,
    plugins: pluginService,
    notifyCatalogChanged: () => deps.hub.notifySystem(["plugins-changed"]),
    warn: (message) => deps.logger.warn(message),
  });
  registerProjectRoutes(publicApi, deps);
  registerPromptHistoryRoutes(publicApi, deps);
  registerThreadSectionRoutes(publicApi, deps);
  registerFileRoutes(publicApi, deps);
  registerHostRoutes(publicApi, deps, pluginService);
  registerDesktopBrowserRoutes(publicApi, deps);
  registerTerminalRoutes(publicApi, deps);
  registerEnvironmentRoutes(publicApi, deps, pluginService);
  registerThreadRoutes(publicApi, deps);
  registerQueueRoutes(publicApi, deps);
  registerSystemRoutes(publicApi, deps, pluginService);
  registerUiPreferenceRoutes(publicApi, deps);
  const pluginInstallJobs = createPluginInstallJobs({
    notifyChanged: () => deps.hub.notifySystem(["plugin-install-jobs-changed"]),
  });
  const pluginUpdateJobs = createPluginUpdateJobs({
    notifyChanged: () => deps.hub.notifySystem(["plugin-update-jobs-changed"]),
  });
  registerPluginUpdateJobRoutes(publicApi, pluginUpdateJobs);
  registerPluginInstallJobRoutes(publicApi, pluginInstallJobs);
  registerPluginCatalogRoutes(
    publicApi,
    pluginCatalogService,
    pluginInstallJobs,
  );
  registerPluginRoutes(
    publicApi,
    deps,
    pluginService,
    pluginInstallJobs,
    pluginUpdateJobs,
    upgradeWebSocket,
  );
  registerSkillsRegistryRoutes(publicApi, deps);
  registerServerMoveRoutes(publicApi, deps, serverMove);
  app.route("/api/v1", publicApi);
  app.use("/api/v1/*", () => {
    throw new ApiError(404, "not_found", "Not found");
  });

  const internalApi = new Hono();
  registerInternalHostRoutes(internalApi, deps);
  registerInternalSessionRoutes(internalApi, deps, pluginService, {
    movedTo: () => serverMove.movedTo(),
    pendingMoveId: () => pendingServerMove?.moveId ?? null,
    sessionOpened: createManualServerImportCompletion({
      deps,
      pending: serverMoveOptions.manualImportPending,
    }),
  });
  registerInternalServerMoveRoutes(internalApi, deps, {
    pending: pendingServerMove,
    serverMove,
  });
  registerInternalSkillRoutes(internalApi, deps);
  registerInternalPluginHostArtifactRoutes(internalApi, deps);
  registerInternalEventRoutes(internalApi, deps);
  registerInternalToolCallRoutes(internalApi, deps);
  registerInternalInteractiveRequestRoutes(internalApi, deps);
  app.route("/internal", internalApi);

  const assertBrowserWebSocketAllowed = (
    context: Parameters<typeof browserRequestProblem>[0],
  ): void => {
    const problem = browserRequestProblem(context, deps);
    if (problem !== null) {
      throw new ApiError(
        problem.status,
        "forbidden_origin",
        problem.error,
        false,
      );
    }
  };

  app.get(
    "/ws",
    upgradeWebSocket((context) => {
      assertBrowserWebSocketAllowed(context);
      return {
        onOpen: (_event, socket) => onClientSocketOpen(deps.hub, socket),
        onMessage: (event, socket) =>
          onClientSocketMessage(deps.hub, socket, event.data),
        onClose: (_event, socket) => onClientSocketClose(deps.hub, socket),
      };
    }),
  );

  app.get(
    "/ws/terminals/:terminalId",
    upgradeWebSocket((context) => {
      assertBrowserWebSocketAllowed(context);
      declineWebSocketCompression(context);
      const terminalId = context.req.param("terminalId");
      if (terminalId === undefined) {
        throw new ApiError(404, "terminal_not_found", "missing terminal id");
      }
      const query = terminalWebSocketQuerySchema.safeParse({
        outputAcks: context.req.query("outputAcks"),
        sinceSeq: context.req.query("sinceSeq"),
      });
      if (!query.success) {
        throw new ApiError(
          400,
          "invalid_terminal_socket_query",
          "Terminal websocket sinceSeq must be a non-negative integer and outputAcks must be 0 or 1",
        );
      }
      return {
        onOpen: (_event, socket) =>
          onTerminalSocketOpen(deps, {
            outputAcks: query.data.outputAcks,
            socket,
            sinceSeq: query.data.sinceSeq,
            terminalId,
          }),
        onMessage: (event, socket) =>
          onTerminalSocketMessage(deps, {
            raw: event.data,
            socket,
            terminalId,
          }),
        onClose: (_event, socket) =>
          onTerminalSocketClose(deps, {
            socket,
            terminalId,
          }),
      };
    }),
  );

  app.get(
    "/internal/ws",
    upgradeWebSocket(async (context) => {
      declineWebSocketCompression(context);
      const websocketContext = await validateDaemonWebSocket(deps, {
        authorizationHeader: context.req.header("authorization"),
        protocolHeader: context.req.header("sec-websocket-protocol"),
        sessionId: context.req.query("sessionId") ?? null,
      });
      return {
        onOpen: (_event, socket) =>
          onDaemonSocketOpen(deps, {
            ...websocketContext,
            socket,
          }),
        onMessage: (event, socket) =>
          onDaemonSocketMessage(
            deps,
            {
              hostId: websocketContext.hostId,
              raw: event.data,
              sessionId: websocketContext.sessionId,
              socket,
            },
            pluginService,
            serverMove,
          ),
        onClose: () => onDaemonSocketClose(deps, websocketContext.sessionId),
      };
    }),
  );

  // MQH: no web UI to serve; the Unity client talks to /api/v1 only.
  app.get("/", (context) => context.text("mqh server"));

  return {
    app,
    closeWebSockets: () =>
      closeWebSocketServer({
        forceCloseAfterMs: WEB_SOCKET_SHUTDOWN_FORCE_CLOSE_MS,
        reason: WEB_SOCKET_SHUTDOWN_REASON,
        server: wss,
      }),
    injectWebSocket,
    pluginService,
    pluginCatalogService,
    serverMove,
  };
}
