# Secrets in MetaCodex: keystore, vault, and the Linux daemon

Status: v1, 2026-11-07. Built and tested in the Editor (`MetaCodexUiTest.Vault`, MQH
`environment-storage.test.ts`); the Android Keystore path compiles (javac against android-34) and is
untested on the headset.

## The problem

MetaCodex runs a Linux userland (Debian under proot) inside the app, and MQH, the daemon, runs there,
not in Horizon OS. Agents (Claude Code, opencode, ...) run there too, as the same Android user, with
full access to everything in that Linux. They need secrets (API keys, git tokens), and so does MQH (the
key that encrypts its machine environment). Meanwhile every other app on the headset can connect to
127.0.0.1.

## What is secret

| Secret | Owner | Where it lives (v1) |
|---|---|---|
| API keys for agents (OpenAI, OpenRouter, Anthropic Console, ...) | the user | typed in VR -> vault; pushed on request to MQH's machine environment (encrypted there) |
| Provider logins (Claude subscription, ChatGPT, opencode OAuth) | the provider CLI | the CLI's own files in the rootfs; **MetaCodex never reads, copies or stores them** (vendor terms; least privilege) |
| MQH machine-environment key (AES-256, encrypts MQH's stored env secrets) | MQH | vault (policy `once`); MQH gets it in memory via `MQH_MACHINE_ENV_KEY_COMMAND`; no key file |
| MQH API token (keeps other Android apps out of MQH's port) | the app | vault (policy `allow`); the app sends it as Bearer; the MQH launcher passes it to MQH (step 1) |
| Control-port token (the userland -> app channel) | the app | new at every launch, `/mnt/quest/control.token` (app-specific external storage) |

## Threats

| # | Who | Can | Cannot |
|---|---|---|---|
| T1 | Other Android apps | connect to any 127.0.0.1 port | read our private files (UID sandbox + SELinux), read `Android/data/<pkg>` (scoped storage) |
| T2 | Someone with the device, backups, a copied data folder | read files of a debuggable build (`run-as`), restore backups | use our Keystore key on another device or outside our app's UID |
| T3 | An agent or a prompt-injected tool in the userland (same UID) | read every file in the rootfs, env of userland processes (`/proc/*/environ`), talk to the control port (the token is in /mnt/quest) | read the app process's memory (release builds are not dumpable), take a Keystore key out of the TEE |
| T4 | The network | nothing: MQH and the control port bind to 127.0.0.1; remote access was removed from MQH | |

T3 is the hard one: an agent is a legitimate user of the secrets it needs and an untrusted reader of
the others. Same-UID isolation is impossible on Android without root, so the design narrows exposure:
no long-lived secret sits in a file inside the rootfs, each release is decided and logged by the app,
and the user sees requests in VR.

## Design

```
 Horizon OS (Android, app UID)                         Linux userland (proot, same UID)
 ┌───────────────────────────────────────────┐         ┌──────────────────────────────────────┐
 │ Android Keystore (TEE; StrongBox if any)   │         │ MQH server  (MQH_MACHINE_ENV_KEY_   │
 │   AES-256-GCM key "metacodex.vault.v1",    │         │   COMMAND="mcx-secret ensure        │
 │   non-exportable                           │         │   mqh.machine-env-key 32 mqh")      │
 │        ▲ seal / open (MetaCodexVault.java) │         │   key in memory only, encrypts its  │
 │ SecretVault (C#)                           │ control │   machine environment (AES-GCM)     │
 │   no_backup/vault.json: names + sealed     │◄─port──►│ mcx-secret get/ensure/env           │
 │   values, policies ask/once/allow/deny,    │ 47800 + │ agents: env vars from MQH's machine │
 │   access log                               │ token   │   environment, or mcx-secret env    │
 │ VAULT panel (secure keyboard, approvals)   │         │ provider CLIs: their own logins     │
 └───────────────────────────────────────────┘         └──────────────────────────────────────┘
```

1. **Key**: one AES-256-GCM key generated inside the Android Keystore, StrongBox first, the TEE
   otherwise (`StrongBoxUnavailableException` falls back). It is non-exportable: even the app cannot
   read it; it can only ask the keystore to seal or open. The VAULT panel shows where it lives
   (`getSecurityLevel()`).
2. **Vault**: `SecretVault` keeps names, policies and sealed values (12-byte random IV, 128-bit tag,
   associated data `metacodex-vault:v1:<name>` so a value cannot be swapped onto another name) in
   `getNoBackupFilesDir()/vault.json`. That folder is internal storage, excluded from backups, and
   outside the rootfs (proot never binds it). `android:allowBackup="false"` stays set.
3. **Release**: the userland asks over the control port (`mcx-secret`), never reads a file. Each
   secret has a policy:
   - `ask`: an ALLOW ONCE / ALWAYS / DENY dialog in VR (times out after ~2 min = deny). The default.
   - `once`: handed out once per app session (bootstrap keys). Re-arming is only possible inside the
     app (VAULT > RE-ARM, or the app restarting MQH), never from Linux.
   - `allow`: always, logged. `deny`: never leaves the app.
   Every request lands in the access log (VAULT panel).
4. **Envelope for MQH**: MQH already encrypts its machine environment (AES-256-GCM, per-value IV,
   AAD = project + name). On the headset its 32-byte key no longer sits in `~/.mqh/machine-environment-key`:
   with `MQH_MACHINE_ENV_KEY_COMMAND` set, MQH runs the command once, keeps the key in memory and writes
   no file. The command is `mcx-secret ensure mqh.machine-env-key 32 mqh`: the app creates the key on
   first use and hands it out once per session, so an agent asking later is refused.
5. **API keys for agents**: typed in VR (secure system keyboard), sealed in the vault, and pushed with
   `→ MQH ENV` into MQH's machine environment (write-only through MQH's API; encrypted under the key of
   point 4). MQH injects them into the agent processes that need them. For one-off tools:
   `mcx-secret env OPENAI_API_KEY -- some-tool` (value only in that process's environment).
6. **Provider logins** stay with the provider CLIs (`claude auth login`, `opencode auth login`,
   `codex login --device-auth`), run by the user in a terminal from AGENTS > SIGN IN. MetaCodex only
   opens the link they print in the Horizon browser (the OAuth redirect to `localhost` reaches the CLI
   because the browser and the userland share the device's loopback) and pastes codes. Their files are
   protected by the app sandbox (T1) and FBE at rest; they are exposed to T3 like any file in the rootfs.

## What each layer buys

| Layer | Stops |
|---|---|
| App sandbox + SELinux + FBE (Android) | T1 reading anything; T2 reading files of a release build |
| Keystore key, sealed vault in no_backup | T2 copies, backups, device transfers (ciphertext only, key bound to this device + app); T3 `cat`/`grep`/`tar` of secrets (they are not in the rootfs) |
| Policies + VR approval + access log | T3 quietly collecting secrets: requests are visible, `ask` needs the user, `once` keys are spent before agents run |
| MQH key from the command, no key file | T3 decrypting MQH's database offline with a key file next to it |
| Tokens on loopback services | T1 driving the app (control token) and, from step 1, MQH (API token) |

## Residual risks (known, documented)

- **Same UID**: a determined process in the userland can read the environment and memory of other
  userland processes (including MQH and agents that got secrets), and could in principle call the
  Keystore through binder as our UID. The vault raises the bar from "read a file" to "attack a running
  process"; it does not isolate agents from each other. Next step if needed: a credential-injecting
  proxy in the app process (agents get a local base URL, the app adds the API key header), so keys
  never enter the userland at all.
- **The control token** is in `/mnt/quest` (app-specific external storage): readable by adb and by
  processes in the userland, not by other apps. Anything in the userland can therefore ask the vault;
  the policies are what protect values.
- **Requester names** in the approval dialog are what the client says (`mcx-secret` sends its parent
  process); treat them as hints.
- **Debuggable builds** allow `adb shell run-as` into the app's files: ship release builds.
- **MQH API token enforcement** is roadmap step 1 (server middleware + CLI header); the token already
  exists in the vault and the app already sends it.

## Files

| Piece | Path |
|---|---|
| Keystore bridge (Java) | `unity/Assets/Plugins/Android/MetaCodexVault.java` |
| Vault, sealers (Android / Editor dev file) | `unity/Assets/MetaCodex/Runtime/Security/SecretVault.cs` |
| VAULT panel, broker on the control port | `unity/Assets/MetaCodex/Runtime/Shell/ShellVault.cs` |
| Async replies on the control port | `unity/Assets/MetaCodex/Runtime/App/ControlServer.cs` |
| Userland client | `userland/overlay/usr/local/bin/mcx-secret` |
| MQH key from a command | `mqh/apps/server/src/services/machines/environment-storage.ts` (+ tests) |

## Sources

- Android Keystore system: https://developer.android.com/privacy-and-security/keystore
- Hardware-backed keys and StrongBox: https://proandroiddev.com/android-keystore-what-is-the-difference-between-strongbox-and-hardware-backed-keys-4c276ea78fd0
- OWASP MASVS-STORAGE, backup leaks: https://developer.android.com/privacy-and-security/risks/backup-leaks
- OWASP MASVS-CODE, backup best practices: https://developer.android.com/privacy-and-security/risks/backup-best-practices

## Audit 2026-11-09 (owner: "solid key management, no plaintext keys")

Checked: the keystore key (AES-256-GCM, 128-bit tag, random IV required, the name as associated data, StrongBox
then TEE, non-exportable), the sealed file in the no-backup folder, release policies, the control port, `mcx-secret`,
the git credential helper (asks the vault at each use; no `~/.git-credentials`), MQH's envelope key (memory only),
logs (no values), the VAULT panel (values never shown again, no clipboard). Fixed:

- **No fallback to a key in a file on the headset.** When the Android Keystore could not be reached, the vault used
  the Editor's dev sealer, whose AES key sat in a file in the app's shared folder (which the userland and adb can
  read): in effect plain text. Now the vault is **locked** instead (`LockedSealer`: nothing is stored, the panel says
  why). Secrets an older build sealed that way are resealed under the keystore key at start, and the dev key and file
  are deleted (`SecretVault.AdoptDevFile`).
- **An unreadable vault file is kept aside** (`vault.json.unreadable-<time>`) instead of being replaced by an empty
  vault at the next save.
- **The MQH token typed in Settings > DAEMON** was stored in `settings.json` as plain text: it is sealed in the vault
  now (`mqh.api-token.override`, policy deny) and moved there from older settings files.
- **`mcx-secret env`** passed values as `env NAME=value cmd` (briefly visible in `ps` and `/proc/*/cmdline`); it exports
  them and execs the command, so they are only in that command's environment.
- The control port compares its token in constant time; the approval dialog marks the requester's name as unverified.

Still true by design (same Android user, threat T3): an agent can read what the user lets it have (environment of its
own processes, values it asked for). And a **debuggable APK** lets anyone with adb run code as the app (`run-as`) and
so use its keystore key: development builds are debuggable (the VAULT panel warns); build with `MCX_RELEASE=1`
(`tools/unity-batch.sh BatchBuild`) for a release APK before storing real keys on a shared device.
