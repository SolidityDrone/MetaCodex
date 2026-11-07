import { enrollMachine } from "../../../../apps/cli/src/commands/machine-enrollment.ts";

const env = {
  MQH_DATA_DIR: process.env.MQH_DATA_DIR,
  MQH_ENROLLMENT: process.env.MQH_ENROLLMENT,
  PATH: "/nonexistent",
};

await enrollMachine(
  { bootstrapEnv: "MQH_ENROLLMENT" },
  {
    env,
    fetchFn: async () =>
      Response.json(
        { hostId: "host_synthetic", hostKey: "synthetic-host-key" },
        { status: 201 },
      ),
    homeDir: process.env.MQH_DATA_DIR,
  },
);
