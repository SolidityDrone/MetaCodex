import type { AvailableModel } from "@bb/domain";

/**
 * MetaCodex's local coder (docs/local-coder.md in the MetaCodex repo): a small model on the headset's AI cores. Once it
 * is installed the app puts MCX_LOCAL_CODER_URL, MCX_LOCAL_CODER_KEY and OPENCODE_CONFIG (a config with the provider
 * "metacodex", model "local-coder") into MQH's machine environment. OpenCode then lists the model as
 * "metacodex/local-coder"; this puts it first in OpenCode's model list under the same name Claude Code shows.
 */
export const OPENCODE_LOCAL_CODER_MODEL_ID = "metacodex/local-coder";
export const LOCAL_CODER_DISPLAY_NAME = "Local coder (this headset, slow)";
const LOCAL_CODER_DESCRIPTION =
  "Qwen3 1.7B on the headset's AI cores: offline and free, but slow (minutes per turn) and only for small tasks; voice waits while it works.";

export function localCoderConfigured(
  env: Readonly<Record<string, string | undefined>>,
): boolean {
  return ["MCX_LOCAL_CODER_URL", "MCX_LOCAL_CODER_KEY", "OPENCODE_CONFIG"].every(
    (name) => (env[name]?.trim() ?? "") !== "",
  );
}

/**
 * OpenCode's models with the local coder first. The entry OpenCode reported is kept (its id is what the session
 * selects); when the discovery run did not see the config, the model is added anyway, because every thread's
 * OpenCode gets the machine environment (OPENCODE_CONFIG included) and can select it.
 */
export function withLocalCoderFirst(
  models: readonly AvailableModel[],
  dialectId: string,
  env: Readonly<Record<string, string | undefined>>,
): AvailableModel[] {
  if (dialectId !== "opencode" || !localCoderConfigured(env)) {
    return [...models];
  }
  const reported = models.find(
    (model) => model.id === OPENCODE_LOCAL_CODER_MODEL_ID,
  );
  const local: AvailableModel = {
    ...(reported ?? {
      id: OPENCODE_LOCAL_CODER_MODEL_ID,
      model: OPENCODE_LOCAL_CODER_MODEL_ID,
      reasoningDiscovery: "unknown",
      supportedReasoningEfforts: [],
      defaultReasoningEffort: "medium",
      supportedServiceTiers: [],
      routeProviderId: "metacodex",
    }),
    displayName: LOCAL_CODER_DISPLAY_NAME,
    description: LOCAL_CODER_DESCRIPTION,
    isDefault: false,
  };
  const rest = models.filter(
    (model) => model.id !== OPENCODE_LOCAL_CODER_MODEL_ID,
  );
  if (reported?.isDefault === true && rest.length > 0) {
    // the local coder is never the default pick; the next model takes over
    rest[0] = { ...rest[0]!, isDefault: true };
  }
  return [local, ...rest];
}
