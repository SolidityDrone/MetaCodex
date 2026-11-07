import { resolveEnvLoader, type EnvLoaderArgs } from "./env.js";
import { loadHostDaemonPortValue } from "./ports.js";
import {
  MQH_LOOPBACK_HOST,
  MQH_PROD_HOST_DAEMON_PORT,
  MQH_PROD_SERVER_PORT,
} from "./runtime.js";
import { loadServerUrlValue } from "./server-url.js";

export interface CliConfig {
  MQH_HOST_DAEMON_PORT: number;
  MQH_SERVER_URL: string;
}

interface LoadCliConfigArgs extends EnvLoaderArgs {
  repoRoot?: string;
}

const DEFAULT_CLI_SERVER_URL = `http://${MQH_LOOPBACK_HOST}:${MQH_PROD_SERVER_PORT}`;

function hasConfiguredValue(env: NodeJS.ProcessEnv, key: string): boolean {
  return env[key] !== undefined;
}

export function loadCliConfig(args: LoadCliConfigArgs = {}): CliConfig {
  const loader = resolveEnvLoader(args);
  const useDevDefaults = loader.mode === "dev" && args.repoRoot !== undefined;
  const serverUrl =
    hasConfiguredValue(loader.env, "MQH_SERVER_URL") || useDevDefaults
      ? loadServerUrlValue({
          ...args,
          env: loader.env,
          homeDir: loader.context.homeDir,
          mode: loader.mode,
        })
      : loadServerUrlValue({
          ...args,
          env: loader.env,
          homeDir: loader.context.homeDir,
          mode: loader.mode,
          serverUrl: DEFAULT_CLI_SERVER_URL,
        });

  return {
    MQH_HOST_DAEMON_PORT:
      hasConfiguredValue(loader.env, "MQH_HOST_DAEMON_PORT") || useDevDefaults
        ? loadHostDaemonPortValue({
            ...args,
            env: loader.env,
            homeDir: loader.context.homeDir,
            mode: loader.mode,
          })
        : MQH_PROD_HOST_DAEMON_PORT,
    MQH_SERVER_URL: serverUrl,
  };
}
