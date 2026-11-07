import type { FeatureFlags } from "@bb/domain";
import type { AppInstallKind, AppSourceOrigin } from "./app-install.js";
import type { AppSurface } from "./app-surface.js";
import type { AppUpdateMode } from "./app-update.js";
import {
  loadCommonConfig,
  type CommonConfig,
  type LoadCommonConfigArgs,
} from "./common.js";
import { loadDatabaseConfig, type DatabaseConfig } from "./database.js";
import { loadDevAppConfig } from "./dev-app.js";
import {
  readEnvVarWithDefault,
  readOptionalEnvVar,
  resolveEnvLoader,
} from "./env.js";
import {
  MQH_APP_INSTALL_KIND_ENV,
  MQH_APP_SOURCE_COMMIT_ENV,
  MQH_APP_SOURCE_ORIGIN_ENV,
  MQH_APP_URL_ENV,
  MQH_APP_SURFACE_ENV,
  MQH_APP_VERSION_ENV,
  MQH_CONNECT_GATE_KEYS_URL_ENV,
  MQH_EXTERNAL_URL_ENV,
  MQH_INHERITED_SKILLS_ROOTS_ENV,
  MQH_MARKETPLACE_URL_ENV,
  MQH_POSTHOG_API_KEY_ENV,
  MQH_SERVER_BIND_HOST_ENV,
  MQH_SERVER_LAUNCH_ID_ENV,
  MQH_APP_UPDATE_MODE_ENV,
  MQH_CLI_LAUNCHER_ENV,
  MQH_TELEMETRY_ENV,
  MQH_PERF_DIAGNOSTICS_ENV,
  MQH_SKIP_ONBOARDING_ENV,
  MQH_SETUP_GUIDE_VARIANT_ENV,
  DEFAULT_BB_APP_URL,
  DEFAULT_BB_APP_SURFACE,
  DEFAULT_BB_APP_VERSION,
  DEFAULT_BB_CONNECT_GATE_KEYS_URL,
  DEFAULT_BB_EXTERNAL_URL,
  DEFAULT_BB_MARKETPLACE_URL,
  DEFAULT_BB_POSTHOG_API_KEY,
  DEFAULT_BB_SERVER_BIND_HOST,
  DEFAULT_BB_TELEMETRY,
  parseServerBindHost,
  type ServerBindHost,
  type SetupGuideVariant,
} from "./env-vars.js";
import { loadFeatureFlags } from "./feature-flags.js";
import { assignIfDefined } from "./objects.js";
import { loadHostDaemonPortValue } from "./ports.js";
import { loadServerPortConfig, type ServerPortConfig } from "./server-port.js";

export interface ServerConfig
  extends CommonConfig, DatabaseConfig, ServerPortConfig {
  MQH_APP_INSTALL_KIND?: AppInstallKind;
  MQH_APP_SOURCE_COMMIT?: string;
  MQH_APP_SOURCE_ORIGIN?: AppSourceOrigin;
  MQH_APP_URL: string;
  MQH_APP_SURFACE: AppSurface;
  MQH_APP_VERSION: string;
  MQH_CONNECT_GATE_KEYS_URL: string;
  MQH_DEV_APP_PORT?: number;
  MQH_EXTERNAL_URL: string;
  MQH_HOST_DAEMON_PORT: number;
  MQH_INHERITED_SKILLS_ROOTS: string[];
  MQH_POSTHOG_API_KEY: string;
  MQH_MARKETPLACE_URL: string;
  MQH_SERVER_BIND_HOST: ServerBindHost;
  MQH_SERVER_LAUNCH_ID?: string;
  MQH_APP_UPDATE_MODE?: AppUpdateMode;
  MQH_CLI_LAUNCHER?: string;
  MQH_TELEMETRY: boolean;
  MQH_PERF_DIAGNOSTICS: boolean;
  MQH_SKIP_ONBOARDING: boolean;
  MQH_SETUP_GUIDE_VARIANT?: SetupGuideVariant;
  featureFlags: FeatureFlags;
}

type LoadServerConfigArgs = LoadCommonConfigArgs;

export { parseServerBindHost };
export type { ServerBindHost };

export function loadServerConfig(
  args: LoadServerConfigArgs = {},
): ServerConfig {
  const loader = resolveEnvLoader(args);
  const commonConfig = loadCommonConfig({
    env: loader.env,
    homeDir: loader.context.homeDir,
    mode: loader.mode,
    repoRoot: args.repoRoot,
  });
  const databaseConfig = loadDatabaseConfig({
    commonConfig,
    env: loader.env,
    homeDir: loader.context.homeDir,
    mode: loader.mode,
    repoRoot: args.repoRoot,
  });
  const serverPortConfig = loadServerPortConfig({
    env: loader.env,
    homeDir: loader.context.homeDir,
    mode: loader.mode,
    repoRoot: args.repoRoot,
  });
  const devAppConfig = loadDevAppConfig({
    env: loader.env,
    homeDir: loader.context.homeDir,
    mode: loader.mode,
  });
  const config: ServerConfig = {
    ...commonConfig,
    ...databaseConfig,
    ...serverPortConfig,
    MQH_APP_URL: readEnvVarWithDefault({
      context: loader.context,
      defaultValue: DEFAULT_BB_APP_URL,
      definition: MQH_APP_URL_ENV,
      env: loader.env,
    }),
    MQH_APP_SURFACE: readEnvVarWithDefault({
      context: loader.context,
      defaultValue: DEFAULT_BB_APP_SURFACE,
      definition: MQH_APP_SURFACE_ENV,
      env: loader.env,
    }),
    MQH_APP_VERSION: readEnvVarWithDefault({
      context: loader.context,
      defaultValue: DEFAULT_BB_APP_VERSION,
      definition: MQH_APP_VERSION_ENV,
      env: loader.env,
    }),
    MQH_CONNECT_GATE_KEYS_URL: readEnvVarWithDefault({
      context: loader.context,
      defaultValue: DEFAULT_BB_CONNECT_GATE_KEYS_URL,
      definition: MQH_CONNECT_GATE_KEYS_URL_ENV,
      env: loader.env,
    }),
    MQH_EXTERNAL_URL: readEnvVarWithDefault({
      context: loader.context,
      defaultValue: DEFAULT_BB_EXTERNAL_URL,
      definition: MQH_EXTERNAL_URL_ENV,
      env: loader.env,
    }),
    MQH_HOST_DAEMON_PORT: loadHostDaemonPortValue({
      env: loader.env,
      homeDir: loader.context.homeDir,
      mode: loader.mode,
      repoRoot: args.repoRoot,
    }),
    MQH_INHERITED_SKILLS_ROOTS: readEnvVarWithDefault({
      context: loader.context,
      defaultValue: [],
      definition: MQH_INHERITED_SKILLS_ROOTS_ENV,
      env: loader.env,
    }),
    MQH_MARKETPLACE_URL: readEnvVarWithDefault({
      context: loader.context,
      defaultValue: DEFAULT_BB_MARKETPLACE_URL,
      definition: MQH_MARKETPLACE_URL_ENV,
      env: loader.env,
    }),
    MQH_POSTHOG_API_KEY: readEnvVarWithDefault({
      context: loader.context,
      defaultValue: DEFAULT_BB_POSTHOG_API_KEY,
      definition: MQH_POSTHOG_API_KEY_ENV,
      env: loader.env,
    }),
    MQH_SERVER_BIND_HOST: readEnvVarWithDefault({
      context: loader.context,
      defaultValue: DEFAULT_BB_SERVER_BIND_HOST,
      definition: MQH_SERVER_BIND_HOST_ENV,
      env: loader.env,
    }),
    MQH_PERF_DIAGNOSTICS: readEnvVarWithDefault({
      context: loader.context,
      definition: MQH_PERF_DIAGNOSTICS_ENV,
      env: loader.env,
      defaultValue: false,
    }),
    MQH_SKIP_ONBOARDING: readEnvVarWithDefault({
      context: loader.context,
      definition: MQH_SKIP_ONBOARDING_ENV,
      env: loader.env,
      defaultValue: false,
    }),
    MQH_TELEMETRY: readEnvVarWithDefault({
      context: loader.context,
      defaultValue: DEFAULT_BB_TELEMETRY,
      definition: MQH_TELEMETRY_ENV,
      env: loader.env,
    }),
    featureFlags: loadFeatureFlags({
      env: loader.env,
      homeDir: loader.context.homeDir,
      mode: loader.mode,
    }),
  };

  assignIfDefined({
    key: "MQH_DEV_APP_PORT",
    target: config,
    value: devAppConfig.MQH_DEV_APP_PORT,
  });
  assignIfDefined({
    key: "MQH_APP_UPDATE_MODE",
    target: config,
    value: readOptionalEnvVar({
      context: loader.context,
      definition: MQH_APP_UPDATE_MODE_ENV,
      env: loader.env,
    }),
  });
  assignIfDefined({
    key: "MQH_CLI_LAUNCHER",
    target: config,
    value: readOptionalEnvVar({
      context: loader.context,
      definition: MQH_CLI_LAUNCHER_ENV,
      env: loader.env,
    }),
  });
  assignIfDefined({
    key: "MQH_APP_INSTALL_KIND",
    target: config,
    value: readOptionalEnvVar({
      context: loader.context,
      definition: MQH_APP_INSTALL_KIND_ENV,
      env: loader.env,
    }),
  });
  assignIfDefined({
    key: "MQH_APP_SOURCE_ORIGIN",
    target: config,
    value: readOptionalEnvVar({
      context: loader.context,
      definition: MQH_APP_SOURCE_ORIGIN_ENV,
      env: loader.env,
    }),
  });
  assignIfDefined({
    key: "MQH_APP_SOURCE_COMMIT",
    target: config,
    value: readOptionalEnvVar({
      context: loader.context,
      definition: MQH_APP_SOURCE_COMMIT_ENV,
      env: loader.env,
    }),
  });
  assignIfDefined({
    key: "MQH_SETUP_GUIDE_VARIANT",
    target: config,
    value: readOptionalEnvVar({
      context: loader.context,
      definition: MQH_SETUP_GUIDE_VARIANT_ENV,
      env: loader.env,
    }),
  });
  assignIfDefined({
    key: "MQH_SERVER_LAUNCH_ID",
    target: config,
    value: readOptionalEnvVar({
      context: loader.context,
      definition: MQH_SERVER_LAUNCH_ID_ENV,
      env: loader.env,
    }),
  });

  return config;
}
