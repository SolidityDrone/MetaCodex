/**
 * `@get-bb/plugin-sdk` — the typed facade plugin authors compile against.
 *
 * The root export carries the side-effect-free host-contract types plus the
 * backend contract (`BbPluginApi`, the `server.ts` factory argument — types
 * only, implemented by the server). MQH has no web app: plugins expose data
 * through their server/host parts and the Unity client renders it (the
 * upstream `./app` React surface is removed).
 */
export * from "./backend-contract.js";
export * from "./cli-spec.js";
export * from "./host-contract.js";
export type * from "./json-value.js";
export * from "./rpc-contract.js";
export type {
  ExperimentalDesktopBrowsersArea,
  ExperimentalDesktopBrowserScope,
  ExperimentalDesktopBrowserLease,
  ExperimentalDesktopBrowserCreateInput,
  ExperimentalDesktopBrowserAcquireInput,
} from "@bb/sdk";

export type * from "./machine-bootstrap.js";

export {
  normalizeHostPath as experimental_normalizeHostPath,
  resolveHostPath as experimental_resolveHostPath,
  isHostPathWithin as experimental_isHostPathWithin,
  joinHostPath as experimental_joinHostPath,
  getHostPathDirname as experimental_getHostPathDirname,
  getHostPathBasename as experimental_getHostPathBasename,
  getRelativeHostPath as experimental_getRelativeHostPath,
  getHostPathComparisonKey as experimental_getHostPathComparisonKey,
} from "@bb/domain";
