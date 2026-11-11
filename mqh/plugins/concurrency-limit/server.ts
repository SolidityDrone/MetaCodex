import {
  defineRpcContract,
  PluginCliError,
  cliCommand,
  defineCli,
  type BbPluginApi,
  type MessageDispatchHookContext,
  type MessageDispatchHookDecision,
  type PluginThreadEventName,
} from "@get-bb/plugin-sdk";
import { z } from "zod";
import { concurrencyLimitHostContract } from "./contract.js";
import {
  automaticHostLimit,
  MAX_LIMIT_VALUE,
  parseLimitValue,
  resolveHostLimit,
  type LimitConfiguration,
} from "./limits.js";

const CONFIGURATION_KEY = "configuration";
const CAPACITIES_KEY = "host-capacities";
/** MQH: one working thread per workspace folder (on unless turned off). */
const WORKSPACE_GUARD_KEY = "workspace-guard";
const CONFIGURATION_CHANGED_CHANNEL = "configuration-changed";
const RETRY_MIN_MS = 1_000;
const RETRY_MAX_MS = 30_000;
export const MAX_REASON_LENGTH = 200;

const JSON_OPTION = {
  type: "boolean",
  description: "Emit machine-readable JSON",
} as const;

const limitSchema = z.number().int().min(0).max(MAX_LIMIT_VALUE);
const hostOverrideSchema = z
  .object({
    hostId: z.string().min(1).max(256),
    limit: limitSchema,
  })
  .strict();
const configurationShape = {
  globalLimit: limitSchema.nullable(),
  hostOverrides: z.array(hostOverrideSchema).max(256),
};
const configurationSchema = z
  .object(configurationShape)
  .strict()
  .superRefine((configuration, context) => {
    const seen = new Set<string>();
    for (const override of configuration.hostOverrides) {
      if (seen.has(override.hostId)) {
        context.addIssue({
          code: "custom",
          message: `Duplicate host override for ${override.hostId}`,
          path: ["hostOverrides"],
        });
      }
      seen.add(override.hostId);
    }
  });
const capacityRecordSchema = z
  .object({
    hostId: z.string().min(1).max(256),
    availableParallelism: z.number().int().positive(),
  })
  .strict();
const capacityRecordsSchema = z.array(capacityRecordSchema).max(256);
const hostConfigurationSchema = z
  .object({
    ...configurationShape,
    hosts: z.array(
      z
        .object({
          id: z.string().min(1),
          name: z.string(),
          status: z.enum(["connected", "disconnected"]),
          availableParallelism: z.number().int().positive().nullable(),
          automaticLimit: limitSchema,
          effectiveLimit: limitSchema,
          override: limitSchema.nullable(),
        })
        .strict(),
    ),
  })
  .strict();

export const concurrencyLimitRpcContract = defineRpcContract({
  getConfiguration: {
    input: z.null(),
    output: hostConfigurationSchema,
  },
  setConfiguration: {
    input: configurationSchema,
    output: hostConfigurationSchema,
  },
});

type Configuration = z.infer<typeof configurationSchema>;
type CapacityRecord = z.infer<typeof capacityRecordSchema>;
type RefreshOutcome = "settled" | "retry";

const DEFAULT_CONFIGURATION: Configuration = {
  globalLimit: null,
  hostOverrides: [],
};

const CAPACITY_FREED_EVENTS = [
  "thread.idle",
  "thread.failed",
  "thread.archived",
  "thread.deleted",
] as const satisfies readonly PluginThreadEventName[];

function normalizeConfiguration(
  configuration: LimitConfiguration,
): Configuration {
  return {
    globalLimit: configuration.globalLimit,
    hostOverrides: [...configuration.hostOverrides].sort((left, right) =>
      left.hostId.localeCompare(right.hostId),
    ),
  };
}

function waitDecision(
  limit: number,
  scopeLabel: string,
): MessageDispatchHookDecision {
  const reason = `${limit} of ${limit} running on ${scopeLabel}`;
  return {
    action: "wait",
    reason:
      reason.length <= MAX_REASON_LENGTH
        ? reason
        : `${reason.slice(0, MAX_REASON_LENGTH - 1)}…`,
  };
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

// ---------------------------------------------------------------------------
// MQH: the workspace guard. Two agents in one folder (the project checkout, or a
// worktree a fork reuses) edit the same files, see each other's half-done
// changes in their builds and tests, and commit each other's work. A turn that
// would start in a folder where another thread is working waits until that one
// goes idle; the reason names it. A fresh worktree is the thread's own folder and
// never waits. The user's Send now on the queued row still overrides it.
// ---------------------------------------------------------------------------

export interface WorkspaceTarget {
  readonly environmentId: string | null;
  readonly path: string | null;
  readonly hostId: string | null;
  readonly isWorktree: boolean;
}

export function sameWorkspacePath(left: string, right: string): boolean {
  const norm = (value: string): string =>
    value.replace(/\\/gu, "/").replace(/\/+$/u, "") || "/";
  return norm(left) === norm(right);
}

function stringInput(inputs: unknown, key: string): string | null {
  if (typeof inputs !== "object" || inputs === null || Array.isArray(inputs))
    return null;
  const value = (inputs as Record<string, unknown>)[key];
  return typeof value === "string" && value.length > 0 ? value : null;
}

export function workspaceWaitReason(
  holderTitle: string,
  target: WorkspaceTarget,
): string {
  const kind = target.isWorktree ? "worktree" : "checkout";
  const title =
    holderTitle.length > 60 ? `${holderTitle.slice(0, 59)}…` : holderTitle;
  const reason = `“${title}” is working in this ${kind}: this turn starts when it is done (Send now shares the ${kind})`;
  return reason.length <= MAX_REASON_LENGTH
    ? reason
    : `${reason.slice(0, MAX_REASON_LENGTH - 1)}…`;
}

function parseLimitArgument(
  raw: string,
  automaticKeyword: "auto" | "unlimited",
): number | null {
  if (raw === automaticKeyword) return null;
  const value = parseLimitValue(raw);
  if (value === null) {
    throw new PluginCliError(
      `Limit must be ${automaticKeyword} or a whole number from 0 to ${MAX_LIMIT_VALUE}`,
      { code: "invalid_limit" },
    );
  }
  return value;
}

function setHostOverride(
  configuration: Configuration,
  hostId: string,
  limit: number | null,
): Configuration {
  const hostOverrides = configuration.hostOverrides.filter(
    (override) => override.hostId !== hostId,
  );
  if (limit !== null) hostOverrides.push({ hostId, limit });
  return normalizeConfiguration({
    globalLimit: configuration.globalLimit,
    hostOverrides,
  });
}

function formatHostLine(host: {
  name: string;
  status: "connected" | "disconnected";
  availableParallelism: number | null;
  effectiveLimit: number;
  override: number | null;
}): string {
  const source =
    host.override === null
      ? `auto, ${host.effectiveLimit}`
      : String(host.override);
  const processors =
    host.availableParallelism === null
      ? "processors unknown"
      : `${host.availableParallelism} processors`;
  return `${host.name}: ${source} (${processors}, ${host.status})`;
}

export default async function concurrencyLimitPlugin(
  bb: BbPluginApi,
): Promise<void> {
  const hostClient = bb.hosts.experimental_client({
    contract: concurrencyLimitHostContract,
  });
  const storedConfiguration = configurationSchema.safeParse(
    await bb.storage.kv.get<unknown>(CONFIGURATION_KEY),
  );
  let configuration = storedConfiguration.success
    ? normalizeConfiguration(storedConfiguration.data)
    : DEFAULT_CONFIGURATION;
  let workspaceGuard =
    (await bb.storage.kv.get<unknown>(WORKSPACE_GUARD_KEY)) !== false;
  const storedCapacities = capacityRecordsSchema.safeParse(
    await bb.storage.kv.get<unknown>(CAPACITIES_KEY),
  );
  const capacities = new Map<string, number>(
    (storedCapacities.success ? storedCapacities.data : []).map((record) => [
      record.hostId,
      record.availableParallelism,
    ]),
  );

  async function readConfiguration() {
    const availableHosts = await bb.sdk.hosts.list();
    return {
      ...configuration,
      hosts: availableHosts.map(({ id, name, status }) => {
        const availableParallelism = capacities.get(id) ?? null;
        const resolved = resolveHostLimit(
          configuration,
          id,
          availableParallelism,
        );
        return {
          id,
          name,
          status,
          availableParallelism,
          automaticLimit: automaticHostLimit(availableParallelism),
          effectiveLimit: resolved.limit,
          override: resolved.mode === "override" ? resolved.limit : null,
        };
      }),
    };
  }

  async function saveConfiguration(next: Configuration): Promise<void> {
    const normalized = normalizeConfiguration(next);
    await bb.storage.kv.set(CONFIGURATION_KEY, normalized);
    configuration = normalized;
    bb.realtime.publish(CONFIGURATION_CHANGED_CHANNEL, {});
    await bb.experimental_hooks.recheck("message.dispatch");
  }

  async function persistCapacities(): Promise<void> {
    const records: CapacityRecord[] = [...capacities]
      .map(([hostId, availableParallelism]) => ({
        hostId,
        availableParallelism,
      }))
      .sort((left, right) => left.hostId.localeCompare(right.hostId));
    await bb.storage.kv.set(CAPACITIES_KEY, records);
  }

  bb.rpc.register(concurrencyLimitRpcContract, {
    getConfiguration: readConfiguration,
    async setConfiguration(next) {
      await saveConfiguration(next);
      return readConfiguration();
    },
  });

  function unknownHost(hostId: string): PluginCliError {
    return new PluginCliError(`Unknown host: ${hostId}`, {
      code: "unknown_host",
      hint: "Run `mqh machine list` for the enrolled host ids.",
    });
  }

  bb.cli.register(
    defineCli({
      name: "concurrency-limit",
      summary: "Configure global and per-host thread limits",
      description:
        "A limit holds new turns in line until a running thread goes idle. The automatic host limit is one thread per available processor.",
      commands: {
        status: cliCommand({
          summary: "Show effective concurrency limits",
          options: { json: JSON_OPTION },
          async run(input) {
            const view = await readConfiguration();
            return {
              exitCode: 0,
              stdout: input.options.json
                ? JSON.stringify(view)
                : [
                    `Overall: ${view.globalLimit === null ? "unlimited" : view.globalLimit}`,
                    "Automatic host limit: one thread per available processor",
                    ...view.hosts.map(formatHostLine),
                  ].join("\n"),
            };
          },
        }),
        global: cliCommand({
          summary: "Show or set the overall limit",
          positionals: [
            {
              name: "limit",
              description: `unlimited, or a whole number from 0 to ${MAX_LIMIT_VALUE} (0 holds every new turn); omit it to print the current limit`,
            },
          ],
          options: { json: JSON_OPTION },
          async run(input) {
            const requested = input.positionals.limit;
            if (requested !== undefined) {
              await saveConfiguration({
                ...configuration,
                globalLimit: parseLimitArgument(requested, "unlimited"),
              });
            }
            return {
              exitCode: 0,
              stdout: input.options.json
                ? JSON.stringify({ globalLimit: configuration.globalLimit })
                : configuration.globalLimit === null
                  ? "Unlimited"
                  : String(configuration.globalLimit),
            };
          },
        }),
        workspace: cliCommand({
          summary: "Show or set the one-thread-per-workspace rule",
          positionals: [
            {
              name: "state",
              description:
                "on (a turn waits while another thread works in the same folder) or off; omit it to print the rule",
            },
          ],
          options: { json: JSON_OPTION },
          async run(input) {
            const requested = input.positionals.state;
            if (requested !== undefined) {
              if (requested !== "on" && requested !== "off") {
                throw new PluginCliError("State must be on or off", {
                  code: "invalid_state",
                });
              }
              workspaceGuard = requested === "on";
              await bb.storage.kv.set(WORKSPACE_GUARD_KEY, workspaceGuard);
              await bb.experimental_hooks.recheck("message.dispatch");
            }
            return {
              exitCode: 0,
              stdout: input.options.json
                ? JSON.stringify({ workspaceGuard })
                : workspaceGuard
                  ? "On: one working thread per workspace folder"
                  : "Off: threads may work in the same folder at once",
            };
          },
        }),
        host: cliCommand({
          summary: "Show or set one host limit",
          positionals: [
            {
              name: "host-id",
              description: "Enrolled host id, as `mqh machine list` prints it",
              required: true,
            },
            {
              name: "limit",
              description: `auto, or a whole number from 0 to ${MAX_LIMIT_VALUE} (0 holds every new turn on that host); omit it to print the host's limit`,
            },
          ],
          options: { json: JSON_OPTION },
          async run(input) {
            const hostId = input.positionals["host-id"];
            const view = await readConfiguration();
            if (!view.hosts.some((candidate) => candidate.id === hostId)) {
              throw unknownHost(hostId);
            }
            const requested = input.positionals.limit;
            if (requested !== undefined) {
              await saveConfiguration(
                setHostOverride(
                  configuration,
                  hostId,
                  parseLimitArgument(requested, "auto"),
                ),
              );
            }
            const updated = (await readConfiguration()).hosts.find(
              (candidate) => candidate.id === hostId,
            );
            if (updated === undefined) throw unknownHost(hostId);
            return {
              exitCode: 0,
              stdout: input.options.json
                ? JSON.stringify(updated)
                : formatHostLine(updated),
            };
          },
        }),
      },
    }),
  );

  let refreshAllRequested = true;
  const requestedHostIds = new Set<string>();
  let wakeWaiter: (() => void) | null = null;

  function requestRefresh(hostId?: string): void {
    if (hostId === undefined) refreshAllRequested = true;
    else requestedHostIds.add(hostId);
    wakeWaiter?.();
  }

  async function refreshCapacities(
    signal: AbortSignal,
    refreshAll: boolean,
    hostIds: ReadonlySet<string>,
  ): Promise<RefreshOutcome> {
    try {
      const availableHosts = await bb.sdk.hosts.list({ signal });
      let changed = false;
      if (refreshAll) {
        const availableHostIds = new Set(availableHosts.map((host) => host.id));
        for (const hostId of capacities.keys()) {
          if (!availableHostIds.has(hostId)) {
            capacities.delete(hostId);
            changed = true;
          }
        }
      }
      let retry = false;
      const targets = availableHosts.filter(
        (availableHost) =>
          availableHost.status === "connected" &&
          (refreshAll || hostIds.has(availableHost.id)),
      );
      await Promise.all(
        targets.map(async (availableHost) => {
          try {
            const result = await hostClient.call("getCapacity", null, {
              hostId: availableHost.id,
              signal,
            });
            if (
              capacities.get(availableHost.id) !== result.availableParallelism
            ) {
              capacities.set(availableHost.id, result.availableParallelism);
              changed = true;
            }
          } catch (error) {
            if (signal.aborted) return;
            retry = true;
            bb.log.warn(
              `Could not detect capacity for host ${availableHost.id}: ${errorMessage(error)}`,
            );
          }
        }),
      );
      if (changed) {
        await persistCapacities();
        bb.realtime.publish(CONFIGURATION_CHANGED_CHANNEL, {});
        await bb.experimental_hooks.recheck("message.dispatch");
      }
      return retry ? "retry" : "settled";
    } catch (error) {
      if (!signal.aborted) {
        bb.log.warn(
          `Could not load hosts for capacity detection: ${errorMessage(error)}`,
        );
      }
      return signal.aborted ? "settled" : "retry";
    }
  }

  function waitForRefresh(
    signal: AbortSignal,
    retryDelayMs: number | null,
  ): Promise<"requested" | "retry" | "aborted"> {
    if (signal.aborted) return Promise.resolve("aborted");
    if (refreshAllRequested || requestedHostIds.size > 0) {
      return Promise.resolve("requested");
    }
    return new Promise((resolve) => {
      let timer: ReturnType<typeof setTimeout> | null = null;
      let settled = false;
      const finish = (reason: "requested" | "retry" | "aborted"): void => {
        if (settled) return;
        settled = true;
        if (timer !== null) clearTimeout(timer);
        if (wakeWaiter === wake) wakeWaiter = null;
        signal.removeEventListener("abort", abort);
        resolve(reason);
      };
      const wake = (): void => finish("requested");
      const abort = (): void => finish("aborted");
      wakeWaiter = wake;
      signal.addEventListener("abort", abort, { once: true });
      if (retryDelayMs !== null) {
        timer = setTimeout(() => finish("retry"), retryDelayMs);
      }
    });
  }

  bb.background.service("capacity-detector", {
    async start(signal) {
      const unsubscribeHost = bb.sdk.subscribe({
        event: "host:changed",
        callback: (event) => {
          if (
            event.changes.some(
              (change) =>
                change === "host-connected" || change === "host-disconnected",
            )
          )
            bb.realtime.publish(CONFIGURATION_CHANGED_CHANNEL, {});
          if (event.changes.includes("host-connected"))
            requestRefresh(event.id);
        },
      });
      const unsubscribeRealtime = bb.sdk.subscribe({
        event: "realtime:connection",
        callback: (event) => {
          if (event.state === "connected" && event.reconnected)
            requestRefresh();
        },
      });
      let retryMs = RETRY_MIN_MS;
      try {
        while (!signal.aborted) {
          const refreshAll = refreshAllRequested;
          const hostIds = new Set(requestedHostIds);
          refreshAllRequested = false;
          requestedHostIds.clear();
          const outcome = await refreshCapacities(signal, refreshAll, hostIds);
          if (signal.aborted) break;
          if (refreshAllRequested || requestedHostIds.size > 0) continue;
          const wakeReason = await waitForRefresh(
            signal,
            outcome === "retry" ? retryMs : null,
          );
          if (wakeReason === "retry") {
            refreshAllRequested = true;
            retryMs = Math.min(retryMs * 2, RETRY_MAX_MS);
          } else if (outcome === "settled") {
            retryMs = RETRY_MIN_MS;
          }
        }
      } finally {
        unsubscribeRealtime();
        unsubscribeHost();
        wakeWaiter?.();
        wakeWaiter = null;
      }
    },
  });

  for (const event of CAPACITY_FREED_EVENTS) {
    bb.events.on(event, async () => {
      await bb.experimental_hooks.recheck("message.dispatch");
    });
  }

  /** The folder this turn would work in; null when it is the thread's own (a fresh worktree) or unknown. */
  async function workspaceTarget(
    context: MessageDispatchHookContext,
  ): Promise<WorkspaceTarget | null> {
    const env = context.environment;
    if (env !== null) {
      return {
        environmentId: env.id,
        path: env.path,
        hostId: env.hostId,
        isWorktree: env.isWorktree,
      };
    }
    const intent = context.environmentIntent;
    if (intent === null) return null;
    if (intent.kind === "environment") {
      try {
        const reused = await bb.sdk.environments.get({
          environmentId: intent.environmentId,
        });
        return {
          environmentId: reused.id,
          path: reused.path,
          hostId: reused.hostId,
          isWorktree: reused.isWorktree,
        };
      } catch {
        return null;
      }
    }
    // a provider makes the environment: only the project checkout is a shared folder
    if (intent.environmentProviderId !== "project-checkout") return null;
    const path = stringInput(intent.inputs, "path");
    if (path === null) return null;
    return {
      environmentId: null,
      path,
      hostId: intent.machine.type === "existing" ? intent.machine.hostId : null,
      isWorktree: false,
    };
  }

  /** The title of another running thread in the same folder, or null. */
  async function workspaceHolder(
    target: WorkspaceTarget,
    threadId: string,
    runningThreads: readonly { id: string; hostId: string | null }[],
  ): Promise<string | null> {
    for (const entry of runningThreads) {
      if (entry.id === threadId) continue;
      if (
        target.hostId !== null &&
        entry.hostId !== null &&
        entry.hostId !== target.hostId
      )
        continue;
      try {
        const other = await bb.sdk.threads.get({ threadId: entry.id });
        if (other.environmentId === null) continue;
        let same =
          target.environmentId !== null &&
          other.environmentId === target.environmentId;
        if (!same && target.path !== null) {
          const env = await bb.sdk.environments.get({
            environmentId: other.environmentId,
          });
          same =
            env.path !== null &&
            sameWorkspacePath(env.path, target.path) &&
            (target.hostId === null || env.hostId === target.hostId);
        }
        if (same) return other.title ?? other.titleFallback ?? other.id;
      } catch {
        // a thread or environment that cannot be read does not hold the folder
      }
    }
    return null;
  }

  bb.experimental_hooks.on("message.dispatch", async (context) => {
    if (
      context.attempt === "join-turn" ||
      context.thread.status === "active" ||
      context.thread.status === "starting"
    ) {
      return { action: "proceed" };
    }

    const host = context.host;
    const target = workspaceGuard ? await workspaceTarget(context) : null;
    if (target === null && configuration.globalLimit === null && host === null) {
      return { action: "proceed" };
    }
    const running = await bb.sdk.threads.listRunning();

    if (target !== null) {
      const holder = await workspaceHolder(target, context.thread.id, running);
      if (holder !== null) {
        return { action: "wait", reason: workspaceWaitReason(holder, target) };
      }
    }

    if (
      configuration.globalLimit !== null &&
      running.length >= configuration.globalLimit
    ) {
      return waitDecision(configuration.globalLimit, "all hosts");
    }

    if (host !== null) {
      const resolved = resolveHostLimit(
        configuration,
        host.id,
        capacities.get(host.id) ?? null,
      );
      const onHost = running.filter((thread) => thread.hostId === host.id);
      if (onHost.length >= resolved.limit) {
        const label = host.name.trim() === "" ? host.id : host.name;
        return waitDecision(resolved.limit, `host ${label}`);
      }
    }

    return { action: "proceed" };
  });
}
