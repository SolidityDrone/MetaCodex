import { readServerConnectHoldFile } from "@bb/server-archive";
import type { ServerLogger } from "../../types.js";
import type { PluginLoadHold } from "../plugins/plugin-runtime.js";
import { MQH_ACCOUNT_PLUGIN_SOURCE, CONNECT_PLUGIN_SOURCE } from "./mode.js";

export const CONNECT_HOLD_DETAIL =
  "Off after mqh server import so this server can't take the original server's tunnel or use its mqh account. Stop the original server, run mqh server allow-connect, then restart bb.";

export const CONNECT_HOLD_SOURCES = [
  CONNECT_PLUGIN_SOURCE,
  MQH_ACCOUNT_PLUGIN_SOURCE,
] as const;

export interface CreateConnectHoldArgs {
  dataDir: string;
  logger: Pick<ServerLogger, "warn">;
}

export function createConnectHold(args: CreateConnectHoldArgs): PluginLoadHold {
  return {
    sources: CONNECT_HOLD_SOURCES,
    detail: CONNECT_HOLD_DETAIL,
    isActive: async () => {
      try {
        return (await readServerConnectHoldFile(args.dataDir)) !== null;
      } catch (error) {
        args.logger.warn(
          { err: error },
          "Could not read server-connect-hold.json, so bb connect and mqh account stay off",
        );
        return true;
      }
    },
  };
}
