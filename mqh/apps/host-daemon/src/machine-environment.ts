import type { HostDaemonContributedEnvEntry } from "@bb/host-daemon-contract";
import { operationEnvironment } from "./operation-environment.js";

export class MachineEnvironment {
  private readonly original: NodeJS.ProcessEnv;
  private readonly originalShell: NodeJS.ProcessEnv;
  private readonly managed = new Set<string>();
  private values: NodeJS.ProcessEnv = {};

  constructor(
    private readonly target: NodeJS.ProcessEnv,
    shell: NodeJS.ProcessEnv,
  ) {
    this.original = { ...target };
    this.originalShell = { ...shell };
  }

  /** Returns whether a value the daemon's processes inherit changed. */
  replace(entries: readonly HostDaemonContributedEnvEntry[]): boolean {
    const resolved = operationEnvironment(entries, this.original);
    const before = this.snapshot();
    this.values = {};
    for (const entry of entries) {
      this.managed.add(entry.name);
      this.values[entry.name] = resolved[entry.name];
    }
    for (const name of this.managed) {
      const value = this.values[name] ?? this.original[name];
      if (value === undefined) delete this.target[name];
      else this.target[name] = value;
    }
    return this.snapshot() !== before;
  }

  private snapshot(): string {
    return JSON.stringify(
      [...this.managed].sort().map((name) => [name, this.target[name] ?? null]),
    );
  }

  shellEnvironment(shell: Record<string, string>): Record<string, string> {
    const result = { ...shell };
    for (const name of this.managed) {
      const value = this.values[name] ?? this.originalShell[name];
      if (value === undefined) delete result[name];
      else result[name] = value;
    }
    return result;
  }
}
