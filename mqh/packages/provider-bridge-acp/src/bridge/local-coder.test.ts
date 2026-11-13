import type { AvailableModel } from "@bb/domain";
import { describe, expect, it } from "vitest";
import {
  LOCAL_CODER_DISPLAY_NAME,
  OPENCODE_LOCAL_CODER_MODEL_ID,
  withLocalCoderFirst,
} from "./local-coder.js";

const ENV = {
  MCX_LOCAL_CODER_URL: "http://127.0.0.1:47810",
  MCX_LOCAL_CODER_KEY: "k",
  OPENCODE_CONFIG: "/mnt/quest/local-coder.opencode.json",
};

function model(id: string, isDefault = false): AvailableModel {
  return {
    id,
    model: id,
    displayName: id.split("/")[1] ?? id,
    description: "",
    reasoningDiscovery: "unknown",
    supportedReasoningEfforts: [],
    defaultReasoningEffort: "medium",
    supportedServiceTiers: [],
    isDefault,
    routeProviderId: id.split("/")[0],
  };
}

describe("the local coder in OpenCode's model list", () => {
  it("moves OpenCode's own entry first, under the shared name, never as the default", () => {
    const listed = withLocalCoderFirst(
      [
        model("opencode/big-pickle", true),
        model("opencode/grok-code"),
        { ...model(OPENCODE_LOCAL_CODER_MODEL_ID), sessionOptions: [] },
      ],
      "opencode",
      ENV,
    );
    expect(listed.map((m) => m.id)).toEqual([
      OPENCODE_LOCAL_CODER_MODEL_ID,
      "opencode/big-pickle",
      "opencode/grok-code",
    ]);
    expect(listed[0]).toMatchObject({
      displayName: LOCAL_CODER_DISPLAY_NAME,
      isDefault: false,
      routeProviderId: "metacodex",
      sessionOptions: [],
    });
    expect(listed[1]?.isDefault).toBe(true);
  });

  it("adds it when the discovery run did not list it (OpenCode started before the config existed)", () => {
    const listed = withLocalCoderFirst(
      [model("opencode/big-pickle", true)],
      "opencode",
      ENV,
    );
    expect(listed.map((m) => [m.id, m.isDefault])).toEqual([
      [OPENCODE_LOCAL_CODER_MODEL_ID, false],
      ["opencode/big-pickle", true],
    ]);
    expect(listed[0]?.model).toBe(OPENCODE_LOCAL_CODER_MODEL_ID);
  });

  it("hands the default on when OpenCode had picked the local coder", () => {
    const listed = withLocalCoderFirst(
      [model(OPENCODE_LOCAL_CODER_MODEL_ID, true), model("opencode/big-pickle")],
      "opencode",
      ENV,
    );
    expect(listed.map((m) => [m.id, m.isDefault])).toEqual([
      [OPENCODE_LOCAL_CODER_MODEL_ID, false],
      ["opencode/big-pickle", true],
    ]);
  });

  it("changes nothing without the local coder, or for other ACP agents", () => {
    const models = [model("opencode/big-pickle", true)];
    expect(withLocalCoderFirst(models, "opencode", {})).toEqual(models);
    expect(
      withLocalCoderFirst(models, "opencode", { ...ENV, OPENCODE_CONFIG: "" }),
    ).toEqual(models);
    expect(withLocalCoderFirst(models, "cursor", ENV)).toEqual(models);
  });
});
