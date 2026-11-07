import {
  readEnvVarWithDefault,
  readOptionalEnvVar,
  resolveEnvLoader,
  type EnvLoaderArgs,
} from "./env.js";
import {
  MQH_DEV_APP_HOST_ENV,
  MQH_DEV_APP_PORT_ENV,
  DEFAULT_BB_DEV_APP_HOST,
} from "./env-vars.js";
import { assignIfDefined } from "./objects.js";

interface DevAppConfig {
  MQH_DEV_APP_HOST: string;
  MQH_DEV_APP_PORT?: number;
}

type LoadDevAppConfigArgs = EnvLoaderArgs;

export function loadDevAppConfig(
  args: LoadDevAppConfigArgs = {},
): DevAppConfig {
  const loader = resolveEnvLoader(args);
  const config: DevAppConfig = {
    MQH_DEV_APP_HOST: readEnvVarWithDefault({
      context: loader.context,
      defaultValue: DEFAULT_BB_DEV_APP_HOST,
      definition: MQH_DEV_APP_HOST_ENV,
      env: loader.env,
    }),
  };
  const appPort = readOptionalEnvVar({
    context: loader.context,
    definition: MQH_DEV_APP_PORT_ENV,
    env: loader.env,
  });

  assignIfDefined({
    key: "MQH_DEV_APP_PORT",
    target: config,
    value: appPort,
  });

  return config;
}
