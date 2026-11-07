import {
  defineWorkspaceTestConfig,
  sharedWorkerProjects,
} from "../../vitest.shared.js";

export default defineWorkspaceTestConfig({
  test: {
    silent: "passed-only",
    env: {
      MQH_DATA_DIR: "/tmp/bb-host-daemon-test",
      MQH_SERVER_URL: "http://127.0.0.1:49161",
      MQH_HOST_DAEMON_PORT: "49162",
    },
    testTimeout: 15_000,
    projects: sharedWorkerProjects({
      pkgDir: __dirname,
      name: "@bb/host-daemon",
      include: ["src/**/*.test.ts", "test/**/*.test.ts"],
    }),
  },
});
