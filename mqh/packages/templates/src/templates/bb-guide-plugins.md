---
kind: instruction
title: bb Guide — Plugins
summary: Command reference for installing, configuring, and running mqh plugins, a summary of the bundled plugins, and the plugin authoring loop.
intent: Provide complete plugin management documentation, route each bundled plugin to its own skill, and route plugin authors to the bb-plugin-authoring skill.
editingNotes: Keep flags accurate against the CLI implementation (apps/cli/src/commands/plugin.ts, apps/cli/src/commands/marketplace.ts) and the server plugin service; a CLI test asserts every `mqh plugin` and `mqh marketplace` subcommand and flag appears in this chapter. Each bundled plugin gets a two-to-three-line summary here; its manual lives in the plugin's own skill. The full authoring reference is the bb-plugin-authoring builtin skill.
---
Plugin commands

A mqh plugin is a TypeScript package that extends bb with server code, an
optional entry that runs on enrolled hosts, UI, agent skills and tools, and
`mqh` CLI subcommands. Plugins are full-trust code.

Builtin plugins (`builtin:<name>`) ship with bb and install automatically.
Official store plugins ship with the app and install on request; third-party
plugins come from marketplaces, Git, npm, or a local path. Plugin state lives
under `<bb-data-dir>/plugins/<id>/` (SQLite database, secrets, logs).

  mqh plugin search <query>       Search the store: the bundled official
                                 plugins plus every registered marketplace.
                                 Matches id, name, description, category, or
                                 tag; status shows installed, compatible,
                                 requires newer bb, or `id in use by <source>`
  mqh plugin install <entry>      Install an official plugin by name,
                                 <entry-id>@<marketplace>, a Git repository
                                 URL, a local path, builtin:<name>,
                                 git:<url>[@<ref|semver-range>], or
                                 npm:<package>[@<version|tag|range>].
                                 Installs ask for confirmation; --yes skips it.
                                 --subdirectory <path> or --plugin <name>
                                 selects one plugin of a multi-plugin
                                 repository; --tag-prefix <prefix> scopes a
                                 git: semver range. Installing a local path
                                 for an id already installed from another
                                 path moves it and keeps its settings.
                                 Installs run one at a time as server jobs
                                 that continue if the CLI or app disconnects;
                                 a repeat request joins the active job.
                                 --no-wait starts the job and prints its id
  mqh plugin install-jobs         List queued, running, and recently finished
                                 installs (--json for the jobs)
  mqh plugin cancel-install <job> Cancel an install: a queued job is dropped;
                                 a running job stops its download or build
                                 and installs nothing, unless it already
                                 started registering, which then finishes
  mqh plugin outdated             Check installed plugins for compatible
                                 updates (--json for raw results), including
                                 newer releases blocked by engine ranges
  mqh plugin update <id> | --all  Apply compatible updates for one plugin or
                                 every tracking plugin. Same confirmation as
                                 install (--yes skips; non-TTY refuses
                                 without --yes)
  mqh plugin list                 Status, services, schedules, handler timings,
                                 and contributed CLI commands. `mqh status`
                                 also names enabled plugins that are
                                 incompatible, failed, or missing
  mqh plugin source <id> [--json] Requested and resolved source, subdirectory,
                                 semver range and resolved tag, engine ranges,
                                 install time, and recent activation history
  mqh plugin enable|disable <id>  Load or unload an installed plugin. A
                                 builtin you never toggled follows bb's
                                 default; an explicit choice persists across
                                 releases
  mqh plugin safe-mode [on|off]   Show or change safe mode. `on` stops every
                                 plugin you installed (official store plugins
                                 included) without changing its enabled
                                 setting; builtins keep running. `off`
                                 restarts them and exits 1 if any fail.
                                 Installs and updates of stopped plugins are
                                 refused until it is off
  mqh plugin reload [id]          Restart plugins on their current sources.
                                 Exits 1 when one does not come up (the
                                 previous instance is kept or degraded)
  mqh plugin config <id> [set <key> <value> | unset <key>]
                                 Show or change a plugin's declared settings.
                                 Running plugins receive changes live; a
                                 plugin waiting for configuration retries
  mqh plugin logs <id> [-n N] [-f]  Print (or follow) a plugin's log
  mqh plugin run <id> [args...]   Run a plugin command explicitly (also works
                                 when a core command owns its name)
  mqh plugin token <id> [--rotate]  Print the token for a plugin's
                                 token-authenticated HTTP routes; --rotate
                                 replaces it
  mqh plugin remove <id>          Uninstall and delete the plugin's settings,
                                 secrets, and schedules (local path sources
                                 stay on disk; builtin removals are
                                 remembered)
  mqh plugin prune [--dry-run]    Delete cached git:/npm: plugin versions no
                                 installed plugin uses (left by earlier bb
                                 releases, rolled-back updates, or
                                 interrupted operations) and leftover cache
                                 directories, and print what was freed.
                                 Updates and removals already delete what
                                 they replace. Never touches a running
                                 version or a local path source. Also in the
                                 command palette

  mqh marketplace add <source>    Add a marketplace from an https manifest URL,
                                 git:<url>[@<ref>], or path:<directory>.
                                 Adding a marketplace installs nothing. For a
                                 git: source, --subdirectory <path> reads
                                 marketplace.json from that directory of the
                                 repository instead of its root
  mqh marketplace list            Name, source, entry count, and last refresh
                                 (--json for raw rows)
  mqh marketplace refresh [name]  Re-read one catalog or all of them. Never
                                 installs, updates, or runs plugin code; a
                                 failed refresh keeps the last valid catalog
                                 and exits non-zero
  mqh marketplace remove <name>   Forget a marketplace. Plugins installed from
                                 it keep running as direct installs and keep
                                 updating from their recorded source.
                                 bb-official and bb-community cannot be
                                 removed

Installed plugins and their settings also appear under Settings → Installed
plugins.

Plugin CLI commands

A plugin can register one top-level `mqh` subcommand (for example
`bb github …`); run it like any core command. Core command names win on a
collision, and `mqh plugin list` then shows the `mqh plugin run <id>` form.
Inside agent threads the generated `plugin-commands` skill lists every
available plugin command. Most plugin commands accept `--json` and `--help`.

Bundled plugins

Each bundled plugin documents its commands and behavior in its own skill.

- Prompt Library (builtin, disabled by default): enable with
  `mqh plugin enable bb--prompt-library`. Open **+ → Prompts…** or press
  **Ctrl+R** to search, preview, star, and insert prompts. Its commands and
  behavior are documented in the `prompt-library` skill.
- Custom instructions (builtin): up to 4,096 characters saved under
  Settings → Custom instructions are added to every agent task on this host.
  `mqh instructions get`, `mqh instructions set <text...>`, and
  `mqh instructions clear`.
- Account Pooler (builtin, off by default): pools Claude and Codex accounts
  behind the mqh server with quota-aware failover. Run
  `mqh plugin enable account-pool`, then `bb pool account add`,
  `bb pool status`, and `bb pool routing`; see the `account-pool` skill.
- Keep Awake (builtin): prevents idle sleep on selected macOS and Windows
  hosts while bb runs; closing the lid still sleeps.
  `bb keep-awake status|enable|disable [--json]`, `bb keep-awake hosts all`,
  or `bb keep-awake hosts <host-id>...`.
- GitHub Star (builtin): asks once for a GitHub star after more than five
  prompts. `mqh plugin disable bb--github-star` stops it from asking.
- Concurrency limit (builtin): caps running threads overall and per host.
  `mqh concurrency-limit status|global|host`; see the `concurrency-limit`
  skill.
- Provider retry (builtin): queues an automatic retry after provider
  overloads, server or connection errors, and subscription-window limits.
  `mqh provider-retry status|explain|retry|cancel`; see the `provider-retry`
  skill.
- Thread list (builtin): owns the sidebar's layout preferences, synced to
  every window. `mqh thread-list prefs list|get|set|reset`; see the
  `thread-list` skill.
- Push notifications (builtin): notifies mobile, web, and desktop clients
  when a thread needs attention.
  `bb push-notifications status|list|add|remove|test|thread`; see the
  `push-notifications` skill.
- Inline previews (builtin): shows an HTML page inline in a reply, from a
  fenced `inline-vis` code block or a `::inline-vis{file="demo.html"}`
  workspace or thread-storage file; see the `inline-vis` skill.
- Automations (builtin): recurring or one-shot agent prompts and stored
  server scripts. `mqh automation create|list|show|update|pause|resume|run|runs|delete`;
  see the `automations` skill.
- Workflows (builtin, off by default): durable JavaScript orchestration of
  worker threads. Run `mqh plugin enable workflows`, then
  `mqh workflows validate|run|status|history|list|stop`; see the `workflows`
  skill.
- Modal sandboxes (official, `mqh plugin install environment-modal-sandbox`):
  reusable Modal cloud machines. `bb modal account inspect`,
  `mqh machine create --provider modal-sandbox`, and `bb modal image|sandbox`;
  see the `modal-sandboxes` skill.

Multi-plugin repositories

One repository can hold several plugins, each an ordinary plugin directory
with its own package.json. An optional `.bb/plugins.json` collection manifest
indexes them:

  {
    "$schema": "https://getbb.app/schemas/plugins.schema.json",
    "schemaVersion": 1,
    "name": "acme-plugins",
    "plugins": [
      { "name": "sidebar", "source": "./plugins/sidebar" },
      { "name": "status", "source": "./apps/status" }
    ]
  }

Every source is a repository-relative directory starting with "./"; absolute
paths, "..", the repository root, duplicate names, and unknown fields are
rejected. The file is an index only: identity, branding, entry points, and
engine ranges stay in each plugin's manifest.

  mqh plugin install git:github.com/acme/repo@main --plugin sidebar
  mqh plugin install git:github.com/acme/repo@main --subdirectory plugins/sidebar
  mqh plugin install path:/work/repo --plugin sidebar

--subdirectory works without a collection manifest; --plugin resolves a name
from .bb/plugins.json. When a repository with a collection manifest is not a
plugin itself and neither flag is given, the install fails and lists the
entry names. bb records the subdirectory, so outdated, update, and remove
work per plugin.

Marketplaces

`bb-official` lists the plugins bundled with the app. It reads a local catalog
and never uses the network; install its entries by bare name or qualified name
(`mqh plugin install docs` or `mqh plugin install docs@bb-official`). An app
update also updates the bundled copy.

`bb-community` lists reviewed plugins that live outside the app bundle, read
from https://getbb.app/marketplace/v2/marketplace.json at startup and every two
hours. Set MQH_MARKETPLACE_URL (read at startup) to override the URL. An invalid
manifest keeps the last valid catalog. `mqh plugin search` shows install counts
that bb measures through anonymous telemetry, so they undercount; third-party
marketplaces have none. Builtins show "Built in", and a plugin under 25
installs that was published in the last 30 days shows "New".
`mqh plugin search --json` returns the raw `installs`, `installedByDefault`,
and long-form `overview` fields.

Anyone can host a third-party marketplace:

  mqh marketplace add https://plugins.acme.dev/marketplace.json
  mqh marketplace add git:github.com/acme/bb-marketplace@main
  mqh marketplace add git:github.com/acme/agent-plugins@main --subdirectory bb
  mqh marketplace add path:/work/acme-marketplace

--subdirectory follows the same path rules as `mqh plugin install
--subdirectory`; bb reads marketplace.json and relative icons from that
directory on every refresh. `source.git.subdir` in a catalog entry stays
relative to the repository root.

The manifest `name` is the marketplace identity; duplicates and the reserved
`bb-official` and `bb-community` names are refused. Install an entry of a
specific marketplace with <entry-id>@<marketplace>:

  mqh plugin install thread-hover-cards@acme-plugins

A bare id resolves across every marketplace: one match installs, several
matches fail and list the id@marketplace choices. Other source forms bypass
catalog resolution. Before installing from a third-party marketplace, bb shows
the marketplace, the entry's author, and the true source, including the exact
release tag and commit a range lands on. `--yes` skips the prompt, not the
resolution, and the install is refused if the listing or resolved commit
changes after confirmation. Settings → Plugin marketplaces manages the same
list. To publish a marketplace, see the bb-plugin-authoring skill.

Updates and pinning

bb checks tracking sources every six hours. With the `pluginUpdates`
experiment on (`mqh settings experiment pluginUpdates true`, on for early
adopters), Settings → Updates checks again when it opens, lists each compatible
update with an Update button and Update all, shows candidates blocked by
engines.bb or engines.bbPluginSdk with the reason, and the sidebar updates
badge counts plugin updates. Updates are never applied automatically: `mqh plugin outdated` checks on
demand and `mqh plugin update` applies compatible candidates. Reinstalling an
installed managed plugin is refused; use `mqh plugin update`. A failed
activation restores the previous version and leaves the failure visible.

Exact npm versions, Git tags and commits, path sources, and bundled official
plugins are pinned. npm ranges, dist-tags, and omitted specs, omitted Git refs
(the default branch), Git branches, and Git semver ranges track compatible
updates. Change a pinned git:/npm: source with `mqh plugin remove` (which
deletes settings, secrets, and schedules) and a fresh install. Edit a local
path plugin in place and `mqh plugin reload <id>`, or move it with
`mqh plugin install path:<new dir>`; both keep its configuration. Before bb
deletes a thread's storage folder or removes a bb-managed workspace (such as
a worktree), it copies any path plugin installed from inside it to
<dataDir>/plugins/<id>/preserved-source and moves the plugin there, keeping
its configuration. Such a plugin carries a `sourceNotice` that says so;
`mqh plugin install` and `mqh plugin list` print it as `note:`, and the app shows
it in the install toast and on Plugin details.

Managed git:/npm: installs refuse engines.bb and engines.bbPluginSdk
mismatches, manifest and artifact identity mismatches, and ids reserved by
bundled plugins. Git and path installs build the plugin on install (git
sources need `git`; bb ships its own npm), and a build failure fails the
install. npm packages must ship their prebuilt app and host bundles.

Git semver ranges

A git source can track releases over the repository's tags:

  mqh plugin install git:github.com/acme/repo@^1.2.0
  mqh plugin install git:github.com/acme/repo@semver:^1.2.0
  mqh plugin install git:github.com/acme/repo@^1.2.0 --tag-prefix notes/

bb keeps tags named [<tag-prefix>]vX.Y.Z and installs the highest one the
range allows. Prereleases are excluded unless the range names one. Without
--tag-prefix tags are repository-wide (v1.2.3); with it they version one
plugin (notes/v1.2.3).

bb records the selected tag and its commit. If that tag later points at
another commit, bb refuses it and names both commits; remove and reinstall
the plugin to accept the new commit.

A bare spec that reads as a range (`^1.2.0`, `1.x`, `>=1 <2`) resolves over
tags only when no branch or tag has that literal name; when one does, the
install fails and asks you to choose `@semver:<range>` or `@ref:<name>`. Bare
version tags such as `v1` and `v1.2.3` are always the literal tag.

Authoring a plugin

  mqh plugin new <name>           Scaffold ./bb-plugin-<name> (backend, sidebar
                                 page, `bb <name>` command, skill) and install
                                 its npm dependencies; no server required
  mqh plugin install .            Register the directory in place
  mqh plugin dev [path]           Watch sources, rebuild, and reload the
                                 installed plugin on every save
  mqh plugin build [path]         Compile the plugin into dist/; no server
                                 required
  mqh plugin types [path] [--check]  Sync the plugin's SDK and shimmed-package
                                 type pins to this bb; keep the SDK in
                                 dependencies when an entry needs its own
                                 SDK install; --check reports stale pins
                                 and the wrong dependency field
  mqh plugin migrate [path] [--yes]  Move a plugin that vendors types/ to the
                                 @get-bb/plugin-sdk npm package

The full authoring reference (manifest, every API surface, build and release,
testing, and marketplace publishing) is the built-in `bb-plugin-authoring`
skill; in a checkout it lives under plugins/bb-guide/skills/bb-plugin-authoring/.
`plugins/` holds every bundled plugin and `examples/plugins/` holds reference
plugins.

## Inspect plugin RPC

`mqh plugin rpc list [plugin-id] [--method <exact-name>] [--json]` lists discoverable methods from running plugins, optionally restricted to one plugin. `mqh plugin rpc inspect <plugin-id> [method] [--json]` dumps registration and method descriptions plus input/output JSON Schemas. Copy the relevant schema into your consumer and call the existing plugin RPC endpoint. Discovery is opt-in advertising, not access control; method names may carry versions such as `provider-usage.v1.listResources`.

`mqh plugin rpc call <plugin-id> <method> [--input-file <json-path>] [--json]` invokes a method using server-side schema validation. Omitting the input file sends JSON null. Input files avoid putting sensitive values in command arguments. Handler refusals print their code and message and exit non-zero: `forbidden` (403), `not_found` (404), `conflict` (409), or `invalid` (422).

### Background updates

`mqh plugin update <id> --yes` starts a server job and waits by polling, so the activation stability check does not hold one HTTP request open. Add `--no-wait` to return immediately. Use `mqh plugin update-jobs [job-id] --json` for progress and results, including automatic rollback. Queued/running updates continue after the CLI or app disconnects. Finished jobs remain for ten minutes; jobs do not survive server restarts. Running updates cannot be cancelled midway through activation.

SDK: `plugins.applyUpdate({ pluginId })` waits; `plugins.experimental_startUpdate({ pluginId })` returns the job. Inspect with `plugins.experimental_updateJobs.list()` or `.get({ jobId })`. Raw HTTP callers opt in with `Prefer: respond-async`; legacy callers still receive the completed result.
