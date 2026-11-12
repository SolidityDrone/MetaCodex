import type { AvailableModel } from "@get-bb/plugin-sdk/provider-bridge";

/**
 * MetaCodex's local coder (docs/local-coder.md in the MetaCodex repo): a small model on the headset's AI cores behind
 * the local AI service's OpenAI Responses / Anthropic Messages endpoints. The app puts MCX_LOCAL_CODER_URL and
 * MCX_LOCAL_CODER_KEY into MQH's machine environment once the model is installed; then this model is listed, and only a
 * thread that picks it talks to the local service (every other thread is untouched).
 */
export const LOCAL_CODER_MODEL = "local-coder";

export interface LocalCoderEndpoint {
  url: string;
  key: string;
}

export function localCoderEndpoint(
  env: NodeJS.ProcessEnv = process.env,
): LocalCoderEndpoint | null {
  const url = env["MCX_LOCAL_CODER_URL"]?.trim();
  const key = env["MCX_LOCAL_CODER_KEY"]?.trim();
  return url && key ? { url: url.replace(/\/+$/, ""), key } : null;
}

export function isLocalCoderModel(model: string | null | undefined): boolean {
  return model === LOCAL_CODER_MODEL;
}

export function localCoderModel(): AvailableModel {
  return {
    id: LOCAL_CODER_MODEL,
    model: LOCAL_CODER_MODEL,
    displayName: "Local coder (this headset, slow)",
    description:
      "Qwen3 1.7B on the headset's AI cores: offline and free, but slow (minutes per turn) and only for small tasks; voice waits while it works.",
    reasoningDiscovery: "unknown",
    supportedReasoningEfforts: [],
    defaultReasoningEffort: "medium",
    supportedServiceTiers: [],
    isDefault: false,
  };
}
