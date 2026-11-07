import { describe, expect, expectTypeOf, it } from "vitest";
import { z } from "zod";
import {
  defineRpcContract,
  type ExperimentalPluginRpcHandlersWithContext,
  type PluginRpcHandlers,
} from "../rpc-contract.js";

const contract = defineRpcContract({
  lookup: {
    input: z.object({
      id: z.string(),
      includeClosed: z.boolean().default(false),
    }),
    output: z.object({ title: z.string(), closed: z.boolean() }),
  },
  ping: {
    input: z.null(),
    output: z.object({ ok: z.literal(true) }),
  },
});

const handlers: PluginRpcHandlers<typeof contract> = {
  lookup(input) {
    expectTypeOf(input).toEqualTypeOf<{
      id: string;
      includeClosed: boolean;
    }>();
    return { title: input.id, closed: input.includeClosed };
  },
  ping() {
    return { ok: true };
  },
};

const contextHandlers: ExperimentalPluginRpcHandlersWithContext<
  typeof contract
> = {
  ...handlers,
  ping(_input, context) {
    expectTypeOf(context.experimental_caller).toEqualTypeOf<
      | { readonly kind: "plugin"; readonly pluginId: string }
      | { readonly kind: "client" }
    >();
    return { ok: true };
  },
};


describe("schema-driven rpc contract", () => {
  it("is an identity helper that preserves handler inference", () => {
    expect(Object.keys(contract)).toEqual(["lookup", "ping"]);
    expect(handlers.lookup({ id: "x", includeClosed: true })).toEqual({
      title: "x",
      closed: true,
    });
    expect(
      contextHandlers.ping(null, {
        experimental_caller: { kind: "plugin", pluginId: "other" },
        experimental_signal: new AbortController().signal,
      }),
    ).toEqual({ ok: true });
  });
});
