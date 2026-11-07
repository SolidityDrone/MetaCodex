import {
  readOptionalEnvVar,
  resolveEnvLoader,
  type EnvLoaderArgs,
} from "./env.js";
import {
  MQH_SERVER_HEADERS_ENV,
  MQH_BRIDGE_DIR_ENV,
  MQH_CLI_DIR_ENV,
  MQH_CONNECT_MACHINE_CREDENTIAL_ENV,
  MQH_HOST_ENROLL_KEY_ENV,
  MQH_HOST_DAEMON_AUTO_UPDATE_ENV,
  MQH_HOST_DAEMON_SUPERVISED_ENV,
  MQH_HOST_ID_ENV,
} from "./env-vars.js";
import { assignIfDefined } from "./objects.js";

export interface HostDaemonEntrypointConfig {
  MQH_BRIDGE_DIR?: string;
  MQH_CLI_DIR?: string;
  MQH_SERVER_HEADERS?: Record<string, string>;
  MQH_HOST_ENROLL_KEY?: string;
  MQH_HOST_DAEMON_AUTO_UPDATE?: boolean;
  MQH_HOST_DAEMON_SUPERVISED?: boolean;
  MQH_HOST_ID?: string;
}

type LoadHostDaemonEntrypointConfigArgs = EnvLoaderArgs;

export function loadHostDaemonEntrypointConfig(
  args: LoadHostDaemonEntrypointConfigArgs = {},
): HostDaemonEntrypointConfig {
  const loader = resolveEnvLoader(args);
  const config: HostDaemonEntrypointConfig = {};
  const bridgeDir = readOptionalEnvVar({
    context: loader.context,
    definition: MQH_BRIDGE_DIR_ENV,
    env: loader.env,
  });
  const cliDir = readOptionalEnvVar({
    context: loader.context,
    definition: MQH_CLI_DIR_ENV,
    env: loader.env,
  });
  const enrollKey = readOptionalEnvVar({
    context: loader.context,
    definition: MQH_HOST_ENROLL_KEY_ENV,
    env: loader.env,
  });
  const autoUpdate = readOptionalEnvVar({
    context: loader.context,
    definition: MQH_HOST_DAEMON_AUTO_UPDATE_ENV,
    env: loader.env,
  });
  const supervised = readOptionalEnvVar({
    context: loader.context,
    definition: MQH_HOST_DAEMON_SUPERVISED_ENV,
    env: loader.env,
  });
  const machineCredential = readOptionalEnvVar({
    context: loader.context,
    definition: MQH_CONNECT_MACHINE_CREDENTIAL_ENV,
    env: loader.env,
  });
  const serverHeaders =
    readOptionalEnvVar({
      context: loader.context,
      definition: MQH_SERVER_HEADERS_ENV,
      env: loader.env,
    }) ??
    (machineCredential === undefined
      ? undefined
      : { "x-bb-connect-machine": machineCredential });
  const hostId = readOptionalEnvVar({
    context: loader.context,
    definition: MQH_HOST_ID_ENV,
    env: loader.env,
  });

  assignIfDefined({
    key: "MQH_BRIDGE_DIR",
    target: config,
    value: bridgeDir,
  });
  assignIfDefined({
    key: "MQH_CLI_DIR",
    target: config,
    value: cliDir,
  });
  assignIfDefined({
    key: "MQH_SERVER_HEADERS",
    target: config,
    value: serverHeaders,
  });
  assignIfDefined({
    key: "MQH_HOST_DAEMON_AUTO_UPDATE",
    target: config,
    value: autoUpdate,
  });
  assignIfDefined({
    key: "MQH_HOST_DAEMON_SUPERVISED",
    target: config,
    value: supervised,
  });
  assignIfDefined({
    key: "MQH_HOST_ENROLL_KEY",
    target: config,
    value: enrollKey,
  });
  assignIfDefined({
    key: "MQH_HOST_ID",
    target: config,
    value: hostId,
  });
  return config;
}
