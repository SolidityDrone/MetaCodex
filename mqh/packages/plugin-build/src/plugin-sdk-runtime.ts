import { PLUGIN_SDK_PACKAGE_NAME } from "./plugin-sdk-install.js";

export type PluginEntryKind = "server" | "host";

export const PLUGIN_SDK_ROOT_FILTER = /^@get-bb\/plugin-sdk$|^@bb\/plugin-sdk$/;
export const PLUGIN_SDK_HOST_FALLBACK_SPECIFIER = "@get-bb/plugin-sdk/host";

const PLUGIN_SDK_HOST_FALLBACK_EXPORTS: ReadonlySet<string> = new Set([
  "experimental_defineHostEntry",
]);

export function isPluginSdkImportProvided(
  entry: PluginEntryKind,
  specifier: string,
  names: readonly string[],
): boolean {
  switch (entry) {
    case "server":
      return PLUGIN_SDK_ROOT_FILTER.test(specifier);
    case "host":
      return (
        specifier === PLUGIN_SDK_PACKAGE_NAME ||
        (specifier === PLUGIN_SDK_HOST_FALLBACK_SPECIFIER &&
          names.every((name) => PLUGIN_SDK_HOST_FALLBACK_EXPORTS.has(name)))
      );
  }
}
