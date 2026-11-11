import type {
  BbPluginApi,
  MessageDispatchHookContext,
  PluginDispatchAttemptKind,
} from "@get-bb/plugin-sdk";
import {
  createFakePluginHost,
  makeHostResponse,
  makeMessageDispatchHookContext,
  makeThreadResponse,
} from "@get-bb/plugin-sdk/testing";
import { describe, expect, it, vi } from "vitest";
import plugin from "./server.js";

type RunningThread = Awaited<
  ReturnType<BbPluginApi["sdk"]["threads"]["listRunning"]>
>[number];
type HostResponse = ReturnType<typeof makeHostResponse>;
type SdkSubscription = Parameters<BbPluginApi["sdk"]["subscribe"]>[0];
type HostChangedSubscription = Extract<
  SdkSubscription,
  { event: "host:changed" }
>;

function isHostChangedSubscription(
  subscription: SdkSubscription,
): subscription is HostChangedSubscription {
  return subscription.event === "host:changed";
}

const PLUGIN_ID = "concurrency-limit";
function hostRecord(
  id: string,
  status: "connected" | "disconnected" = "connected",
  name = id,
): HostResponse {
  return makeHostResponse({ id, name, status });
}

function running(overrides: Partial<RunningThread> = {}): RunningThread {
  return { id: "thr_running", hostId: "host-a", ...overrides };
}

interface GateContextOverrides {
  hostId?: string | null;
  hostName?: string;
  thread?: Partial<MessageDispatchHookContext["thread"]>;
  attempt?: PluginDispatchAttemptKind;
}

function dispatchContext(overrides: GateContextOverrides = {}) {
  const hostId = overrides.hostId === undefined ? "host-a" : overrides.hostId;
  return makeMessageDispatchHookContext({
    thread: {
      id: "thr_1",
      status: "pending",
      ...overrides.thread,
    },
    attempt: overrides.attempt ?? "start-turn",
    host:
      hostId === null
        ? null
        : { id: hostId, name: overrides.hostName ?? hostId },
  });
}

interface SetupOptions {
  configuration?: {
    globalLimit: number | null;
    hostOverrides: Array<{ hostId: string; limit: number }>;
  };
  capacities?: Array<{ hostId: string; availableParallelism: number }>;
  hosts?: HostResponse[] | (() => HostResponse[]);
  running?: RunningThread[];
  detectedParallelism?: number;
  subscribe?: BbPluginApi["sdk"]["subscribe"];
  /** MQH's workspace guard reads the running threads and their environments. */
  threadsGet?: (args: { threadId: string }) => Promise<unknown>;
  environmentsGet?: (args: { environmentId: string }) => Promise<unknown>;
}

async function setup(options: SetupOptions = {}) {
  const subscribe: BbPluginApi["sdk"]["subscribe"] = () => () => {};
  const fake = createFakePluginHost({
    pluginId: PLUGIN_ID,
    sdk: {
      subscribe: options.subscribe ?? subscribe,
      hosts: {
        list: async () =>
          typeof options.hosts === "function"
            ? options.hosts()
            : (options.hosts ?? []),
      },
      threads: {
        listRunning: async () => options.running ?? [],
        ...(options.threadsGet === undefined ? {} : { get: options.threadsGet }),
      },
      ...(options.environmentsGet === undefined
        ? {}
        : { environments: { get: options.environmentsGet } }),
    },
    experimental_callHostRpc: () => ({
      availableParallelism: options.detectedParallelism ?? 8,
    }),
  });
  if (options.configuration !== undefined) {
    await fake.bb.storage.kv.set("configuration", options.configuration);
  }
  if (options.capacities !== undefined) {
    await fake.bb.storage.kv.set("host-capacities", options.capacities);
  }
  await plugin(fake.bb);
  const hook = fake.harness.registrations.hooks["message.dispatch"];
  if (hook === null) throw new Error("message.dispatch was not registered");
  return { ...fake, hook };
}

function hostChanges(): {
  emitHostChanges(
    hostId: string,
    changes: Parameters<HostChangedSubscription["callback"]>[0]["changes"],
  ): void;
  subscribe: BbPluginApi["sdk"]["subscribe"];
} {
  let callback: HostChangedSubscription["callback"] | null = null;
  return {
    emitHostChanges(hostId, changes) {
      callback?.({
        type: "changed",
        entity: "host",
        id: hostId,
        changes,
      });
    },
    subscribe(subscription) {
      if (isHostChangedSubscription(subscription)) {
        callback = subscription.callback;
      }
      return () => {
        if (isHostChangedSubscription(subscription)) callback = null;
      };
    },
  };
}

describe("configuration", () => {
  it("uses a custom settings section rather than generic setting descriptors", async () => {
    const { harness } = await setup();

    expect(harness.registrations.settingsDescriptors).toEqual({});
  });

  it("returns each host's detected automatic limit and retained offline capacity", async () => {
    const { harness } = await setup({
      hosts: [
        hostRecord("host-a", "connected", "Laptop"),
        hostRecord("host-b", "disconnected", "Studio"),
      ],
      capacities: [
        { hostId: "host-a", availableParallelism: 8 },
        { hostId: "host-b", availableParallelism: 16 },
      ],
    });

    await expect(harness.behavior.callRpc("getConfiguration")).resolves.toEqual(
      {
        globalLimit: null,
        hostOverrides: [],
        hosts: [
          {
            id: "host-a",
            name: "Laptop",
            status: "connected",
            availableParallelism: 8,
            automaticLimit: 8,
            effectiveLimit: 8,
            override: null,
          },
          {
            id: "host-b",
            name: "Studio",
            status: "disconnected",
            availableParallelism: 16,
            automaticLimit: 16,
            effectiveLimit: 16,
            override: null,
          },
        ],
      },
    );
  });

  it("rejects invalid and duplicate limits at the RPC boundary", async () => {
    const { harness } = await setup();

    await expect(
      harness.behavior.callRpc("setConfiguration", {
        globalLimit: 1.5,
        hostOverrides: [],
      }),
    ).rejects.toThrow();
    await expect(
      harness.behavior.callRpc("setConfiguration", {
        globalLimit: null,
        hostOverrides: [
          { hostId: "host-a", limit: 1 },
          { hostId: "host-a", limit: 2 },
        ],
      }),
    ).rejects.toThrow(/rpc input validation failed/u);
  });

  it("persists validated configuration and rechecks waiting dispatches", async () => {
    const { bb, harness } = await setup({ hosts: [hostRecord("host-a")] });

    await harness.behavior.callRpc("setConfiguration", {
      globalLimit: 3,
      hostOverrides: [{ hostId: "host-a", limit: 0 }],
    });

    await expect(bb.storage.kv.get("configuration")).resolves.toEqual({
      globalLimit: 3,
      hostOverrides: [{ hostId: "host-a", limit: 0 }],
    });
    expect(harness.recheckCount).toBe(1);
  });

  it("detects connected host capacity in the background", async () => {
    const { bb, harness } = await setup({
      hosts: [hostRecord("host-a")],
      detectedParallelism: 12,
    });
    const service = harness.behavior.runService("capacity-detector");

    await vi.waitFor(() => {
      expect(harness.experimental_hostRpcCalls).toHaveLength(1);
    });
    await expect(bb.storage.kv.get("host-capacities")).resolves.toEqual([
      { hostId: "host-a", availableParallelism: 12 },
    ]);
    await expect(
      harness.behavior.callRpc("getConfiguration"),
    ).resolves.toMatchObject({
      hosts: [
        {
          id: "host-a",
          availableParallelism: 12,
          automaticLimit: 12,
          effectiveLimit: 12,
        },
      ],
    });

    service.controller.abort();
    await service.done;
  });

  it("detects a host when it connects after startup", async () => {
    const changes = hostChanges();
    let status: HostResponse["status"] = "disconnected";
    const { harness } = await setup({
      hosts: () => [hostRecord("host-a", status)],
      subscribe: changes.subscribe,
    });
    const service = harness.behavior.runService("capacity-detector");
    await vi.waitFor(() => {
      expect(harness.inspection.sdk.callsTo("hosts.list")).toHaveLength(1);
    });
    expect(harness.experimental_hostRpcCalls).toHaveLength(0);

    status = "connected";
    changes.emitHostChanges("host-a", ["host-connected"]);
    await vi.waitFor(() => {
      expect(harness.experimental_hostRpcCalls).toHaveLength(1);
    });

    service.controller.abort();
    await service.done;
  });

  it("ignores host messages that only report a provider model catalog change", async () => {
    const changes = hostChanges();
    const { harness } = await setup({
      hosts: [hostRecord("host-a")],
      subscribe: changes.subscribe,
    });
    const service = harness.behavior.runService("capacity-detector");
    await vi.waitFor(() => {
      expect(harness.experimental_hostRpcCalls).toHaveLength(1);
      expect(harness.realtimeSignals).toHaveLength(1);
    });
    const hostListCalls = harness.inspection.sdk.callsTo("hosts.list").length;

    changes.emitHostChanges("host-a", ["provider-model-catalog-changed"]);
    await new Promise((resolve) => setTimeout(resolve, 10));
    expect(harness.realtimeSignals).toHaveLength(1);
    expect(harness.experimental_hostRpcCalls).toHaveLength(1);
    expect(harness.inspection.sdk.callsTo("hosts.list")).toHaveLength(
      hostListCalls,
    );

    changes.emitHostChanges("host-a", ["host-disconnected"]);
    expect(harness.realtimeSignals).toHaveLength(2);

    service.controller.abort();
    await service.done;
  });

  it("provides CLI parity for global and host overrides", async () => {
    const { harness } = await setup({
      hosts: [hostRecord("host-a", "connected", "Laptop")],
      capacities: [{ hostId: "host-a", availableParallelism: 8 }],
    });

    await expect(
      harness.behavior.runCli(["global", "3"]),
    ).resolves.toMatchObject({
      exitCode: 0,
      stdout: "3",
    });
    await expect(
      harness.behavior.runCli(["host", "host-a", "0"]),
    ).resolves.toMatchObject({
      exitCode: 0,
      stdout: "Laptop: 0 (8 processors, connected)",
    });
    await expect(
      harness.behavior.runCli(["host", "host-a", "auto"]),
    ).resolves.toMatchObject({
      exitCode: 0,
      stdout: "Laptop: auto, 8 (8 processors, connected)",
    });
    await expect(
      harness.behavior.runCli(["global", "1.5"]),
    ).resolves.toMatchObject({ exitCode: 1 });
  });

  it("documents the limit range in help and reports errors as JSON", async () => {
    const { harness } = await setup({
      hosts: [hostRecord("host-a", "connected", "Laptop")],
      capacities: [{ hostId: "host-a", availableParallelism: 8 }],
    });

    const help = (await harness.behavior.runCli(["host", "--help"])).stdout;
    expect(help).toContain("mqh concurrency-limit host");
    expect(help).toContain("0 to 10000");

    const envelope = await harness.behavior.runCli([
      "host",
      "host-b",
      "--json",
    ]);
    expect(envelope.exitCode).toBe(1);
    expect(JSON.parse(envelope.stdout)).toEqual({
      ok: false,
      error: {
        code: "unknown_host",
        message: "Unknown host: host-b",
        hint: "Run `mqh machine list` for the enrolled host ids.",
      },
    });
    expect(envelope.stderr).toContain("Unknown host: host-b");

    const badLimit = await harness.behavior.runCli(["host", "host-a", "many"]);
    expect(badLimit.exitCode).toBe(1);
    expect(badLimit.stderr).toContain(
      "Limit must be auto or a whole number from 0 to 10000",
    );
  });
});

describe("message.dispatch", () => {
  it("uses a conservative limit of one until a host is detected", async () => {
    const { hook } = await setup({ running: [running()] });

    await expect(hook(dispatchContext())).resolves.toEqual({
      action: "wait",
      reason: "1 of 1 running on host host-a",
    });
  });

  it("uses each host's detected automatic limit", async () => {
    const { hook } = await setup({
      capacities: [{ hostId: "host-a", availableParallelism: 8 }],
      running: [
        running({ id: "a" }),
        running({ id: "b" }),
        running({ id: "c" }),
        running({ id: "d" }),
        running({ id: "e" }),
        running({ id: "f" }),
        running({ id: "g" }),
        running({ id: "h" }),
      ],
    });

    await expect(hook(dispatchContext())).resolves.toEqual({
      action: "wait",
      reason: "8 of 8 running on host host-a",
    });
  });

  it("keeps host pools separate and honors explicit overrides", async () => {
    const { hook } = await setup({
      configuration: {
        globalLimit: null,
        hostOverrides: [{ hostId: "host-a", limit: 1 }],
      },
      running: [running({ hostId: "host-a" })],
    });

    await expect(hook(dispatchContext({ hostId: "host-a" }))).resolves.toEqual({
      action: "wait",
      reason: "1 of 1 running on host host-a",
    });
    await expect(hook(dispatchContext({ hostId: "host-b" }))).resolves.toEqual({
      action: "proceed",
    });
  });

  it("applies the overall limit before a host limit", async () => {
    const { hook } = await setup({
      configuration: { globalLimit: 1, hostOverrides: [] },
      running: [running()],
    });

    await expect(hook(dispatchContext())).resolves.toEqual({
      action: "wait",
      reason: "1 of 1 running on all hosts",
    });
  });

  it("treats zero as a pause rather than as unlimited", async () => {
    const global = await setup({
      configuration: { globalLimit: 0, hostOverrides: [] },
    });
    await expect(
      global.hook(dispatchContext({ hostId: null })),
    ).resolves.toEqual({
      action: "wait",
      reason: "0 of 0 running on all hosts",
    });

    const host = await setup({
      configuration: {
        globalLimit: null,
        hostOverrides: [{ hostId: "host-a", limit: 0 }],
      },
    });
    await expect(host.hook(dispatchContext())).resolves.toEqual({
      action: "wait",
      reason: "0 of 0 running on host host-a",
    });
  });

  it("skips host enforcement when no host has been selected", async () => {
    const { harness, hook } = await setup({ running: [running()] });

    await expect(hook(dispatchContext({ hostId: null }))).resolves.toEqual({
      action: "proceed",
    });
    expect(harness.inspection.sdk.callsTo("threads.listRunning")).toHaveLength(
      0,
    );
  });

  it("does not re-admit running threads or join-turn attempts", async () => {
    const { hook } = await setup({
      configuration: { globalLimit: 0, hostOverrides: [] },
    });

    await expect(
      hook(dispatchContext({ thread: { status: "active" } })),
    ).resolves.toEqual({ action: "proceed" });
    await expect(
      hook(
        dispatchContext({
          attempt: "join-turn",
          thread: { status: "idle" },
        }),
      ),
    ).resolves.toEqual({ action: "proceed" });
  });
});

describe("capacity wake events", () => {
  it("rechecks queued work when a running thread stops occupying capacity", async () => {
    const { harness } = await setup();
    const thread = makeThreadResponse({ id: "thr_freed", status: "idle" });

    await harness.behavior.emitThreadEvent("thread.idle", {
      thread,
      lastAssistantText: null,
    });
    await harness.behavior.emitThreadEvent("thread.failed", {
      thread,
      error: null,
    });
    await harness.behavior.emitThreadEvent("thread.archived", { thread });
    await harness.behavior.emitThreadEvent("thread.deleted", { thread });

    expect(harness.recheckCount).toBe(4);
  });
});

describe("workspace guard (MQH): one working thread per folder", () => {
  const checkout = { id: "env-checkout", path: "/root/app", hostId: "host-a", isWorktree: false };
  const otherThread = (environmentId: string | null) => async ({ threadId }: { threadId: string }) =>
    makeThreadResponse({ id: threadId, environmentId, title: "Fix the build" });
  const environments = (rows: Array<typeof checkout>) => async ({ environmentId }: { environmentId: string }) => {
    const row = rows.find((candidate) => candidate.id === environmentId);
    if (row === undefined) throw new Error("no such environment");
    return row;
  };
  const turn = (overrides: Parameters<typeof makeMessageDispatchHookContext>[0] = {}) =>
    makeMessageDispatchHookContext({
      thread: { id: "thr_1", status: "idle" },
      attempt: "start-turn",
      host: { id: "host-a", name: "host-a" },
      ...overrides,
    });

  it("holds a turn while another thread works in the same checkout, naming it", async () => {
    const { hook } = await setup({
      running: [running({ id: "thr_other", hostId: "host-a" })],
      capacities: [{ hostId: "host-a", availableParallelism: 8 }, { hostId: "host-b", availableParallelism: 8 }],
      threadsGet: otherThread("env-checkout"),
      environmentsGet: environments([checkout]),
    });
    await expect(hook(turn({ environment: checkout }))).resolves.toEqual({
      action: "wait",
      reason:
        "“Fix the build” is working in this checkout: this turn starts when it is done (Send now shares the checkout)",
    });
  });

  it("sees a new thread's project checkout before its environment exists", async () => {
    const { hook } = await setup({
      running: [running({ id: "thr_other", hostId: "host-a" })],
      capacities: [{ hostId: "host-a", availableParallelism: 8 }, { hostId: "host-b", availableParallelism: 8 }],
      threadsGet: otherThread("env-checkout"),
      environmentsGet: environments([checkout]),
    });
    const decision = await hook(
      turn({
        thread: { id: "thr_new", status: "pending" },
        environment: null,
        environmentIntent: {
          kind: "provider",
          environmentProviderId: "project-checkout",
          machine: { type: "existing", hostId: "host-a" },
          inputs: { path: "/root/app/" },
        },
      }),
    );
    expect(decision).toMatchObject({ action: "wait" });
  });

  it("never holds a fresh worktree: it is the thread's own folder", async () => {
    const { hook } = await setup({
      running: [running({ id: "thr_other", hostId: "host-a" })],
      capacities: [{ hostId: "host-a", availableParallelism: 8 }, { hostId: "host-b", availableParallelism: 8 }],
      threadsGet: otherThread("env-checkout"),
      environmentsGet: environments([checkout]),
    });
    await expect(
      hook(
        turn({
          thread: { id: "thr_new", status: "pending" },
          environment: null,
          environmentIntent: {
            kind: "provider",
            environmentProviderId: "git-worktree",
            machine: { type: "existing", hostId: "host-a" },
            inputs: { branch: { kind: "default" } },
          },
        }),
      ),
    ).resolves.toEqual({ action: "proceed" });
  });

  it("holds a fork that reuses a worktree another thread works in", async () => {
    const worktree = { id: "env-wt", path: "/root/app-wt/feat", hostId: "host-a", isWorktree: true };
    const { hook } = await setup({
      running: [running({ id: "thr_other", hostId: "host-a" })],
      capacities: [{ hostId: "host-a", availableParallelism: 8 }, { hostId: "host-b", availableParallelism: 8 }],
      threadsGet: otherThread("env-wt"),
      environmentsGet: environments([worktree]),
    });
    const decision = await hook(
      turn({
        thread: { id: "thr_fork", status: "pending" },
        environment: null,
        environmentIntent: { kind: "environment", environmentId: "env-wt" },
      }),
    );
    expect(decision).toMatchObject({ action: "wait" });
    expect(decision.action === "wait" && decision.reason).toContain("this worktree");
  });

  it("lets threads in other folders, on other hosts, or joining a turn go at once", async () => {
    const elsewhere = { id: "env-wt2", path: "/root/app-wt/other", hostId: "host-a", isWorktree: true };
    const { hook } = await setup({
      running: [running({ id: "thr_other", hostId: "host-a" })],
      capacities: [{ hostId: "host-a", availableParallelism: 8 }, { hostId: "host-b", availableParallelism: 8 }],
      threadsGet: otherThread("env-checkout"),
      environmentsGet: environments([checkout, elsewhere]),
    });
    await expect(hook(turn({ environment: elsewhere }))).resolves.toEqual({ action: "proceed" });
    await expect(
      hook(turn({ host: { id: "host-b", name: "host-b" }, environment: { ...checkout, id: "env-b", hostId: "host-b" } })),
    ).resolves.toEqual({ action: "proceed" });
    await expect(hook(turn({ environment: checkout, attempt: "join-turn" }))).resolves.toEqual({ action: "proceed" });
  });

  it("can be turned off from the CLI (and says so)", async () => {
    const { hook, harness } = await setup({
      running: [running({ id: "thr_other", hostId: "host-a" })],
      capacities: [{ hostId: "host-a", availableParallelism: 8 }, { hostId: "host-b", availableParallelism: 8 }],
      threadsGet: otherThread("env-checkout"),
      environmentsGet: environments([checkout]),
    });
    await expect(harness.behavior.runCli(["workspace"])).resolves.toMatchObject({
      exitCode: 0,
      stdout: "On: one working thread per workspace folder",
    });
    await expect(harness.behavior.runCli(["workspace", "off"])).resolves.toMatchObject({ exitCode: 0 });
    await expect(hook(turn({ environment: checkout }))).resolves.toEqual({ action: "proceed" });
    await expect(harness.behavior.runCli(["workspace", "sometimes"])).resolves.toMatchObject({ exitCode: 1 });
  });
});

