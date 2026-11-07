// MQH: the headset harness has no remote access. Upstream bb tunnels a machine to getbb.app ("bb connect")
// and runs frpc for connect-next shares; both clients are removed. These stand-ins keep the daemon's
// session protocol answering "idle, no shares" with no network code at all.
import type {
  HostDaemonConnectNext,
  HostDaemonConnectNextStatus,
  HostDaemonConnectShares,
  HostDaemonConnectTunnelIdentity,
} from "@bb/host-daemon-contract";

const REMOTE_ACCESS_DISABLED = "remote access is not part of MQH";

export class DisabledConnectTunnel {
  status(): { ports: number[] } {
    return { ports: [] };
  }

  replaceShareSet(_shares: HostDaemonConnectShares): boolean {
    return false;
  }

  replaceAuthoritativeShareSet(_shares: HostDaemonConnectShares): void {}

  ensureTunnelIdentity(): Promise<HostDaemonConnectTunnelIdentity> {
    return Promise.reject(new Error(REMOTE_ACCESS_DISABLED));
  }

  shutdown(): void {}
}

export class DisabledMachineNextTunnel {
  private readonly since = Date.now();

  replace(_next: HostDaemonConnectNext): boolean {
    return false;
  }

  replaceAuthoritative(_next: HostDaemonConnectNext): void {}

  sessionLost(): void {}

  sessionRestored(): void {}

  setPorts(_ports: readonly number[]): Promise<void> {
    return Promise.resolve();
  }

  status(): HostDaemonConnectNextStatus {
    return {
      state: "idle",
      label: null,
      lastError: REMOTE_ACCESS_DISABLED,
      nextRetryAt: null,
      since: this.since,
      shares: [],
    };
  }

  shutdown(): Promise<void> {
    return Promise.resolve();
  }
}
