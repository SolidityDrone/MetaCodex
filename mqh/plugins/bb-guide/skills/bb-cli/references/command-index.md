# Core command index

This index lists every command path that the core CLI registers, including aliases: `thread get|view|status` run `thread show`, `thread message|send` run `thread tell`, `thread messages|timeline` run `thread log`, `thread create|new` run `thread spawn`, `terminal read` runs `terminal output`, `plugin uninstall` runs `plugin remove`, and `environment get` runs `environment show`. At the top level `bb host`, `bb hosts`, and `mqh machines` run `mqh machine`, `bb env` runs `mqh environment`, and the plurals `threads`, `projects`, `terminals`, `providers`, `plugins`, and `skills` run their singular command, unless a plugin registers that name. `mqh guide commands <group>` prints a group's commands with every option on one page. Check live help for flags and defaults.

## status

- `mqh status`

## settings

- `mqh settings`
- `mqh settings show`
- `mqh settings mobile-app`
- `mqh settings ai-services`
- `mqh settings ai-services show`
- `mqh settings ai-services set`
- `mqh settings ai-services test`
- `mqh settings cli-command`
- `mqh settings cli-command show`
- `mqh settings cli-command install`
- `mqh settings cli-command remove`
- `mqh settings general`
- `mqh settings replay-onboarding`
- `mqh settings completed-turns`
- `mqh settings experiment`
- `mqh settings keyboard`
- `mqh settings keyboard hints`
- `mqh settings keyboard list`
- `mqh settings keyboard set`
- `mqh settings keyboard reset`
- `mqh settings ui`
- `mqh settings ui list`
- `mqh settings ui get`
- `mqh settings ui set`
- `mqh settings ui reset`
- `mqh settings usage`
- `mqh settings version`
- `mqh settings reload`

`mqh settings usage` accepts `--refresh` to fetch fresh provider usage.

## project

- `mqh project`
- `mqh project source`
- `mqh project source add`
- `mqh project source update`
- `mqh project source delete`
- `mqh project attachment`
- `mqh project attachment upload`
- `mqh project attachment download`
- `mqh project list`
- `mqh project discover`
- `mqh project history`
- `mqh project reorder`
- `mqh project branches`
- `mqh project paths`
- `mqh project commands`
- `mqh project files`
- `mqh project content`
- `mqh project create`
- `mqh project show`
- `mqh project update`
- `mqh project delete`

## prompt-history

- `bb prompt-history`
- `bb prompt-history list`

## provider

- `mqh provider`
- `mqh provider list`
- `mqh provider enable`
- `mqh provider disable`
- `mqh provider models`

Use `--selected-model <model-id>` to discover its reasoning choices and include selected-only entries.

## machine

- `mqh machine`
- `mqh machine providers`
- `mqh machine enroll`
- `mqh machine env`
- `mqh machine env list`
- `mqh machine env set`
- `mqh machine env unset`
- `mqh machine create`
- `mqh machine list`
- `mqh machine show`
- `mqh machine reconnect`
- `mqh machine rename`
- `mqh machine remove`
- `mqh machine suspend`
- `mqh machine resume`
- `mqh machine reconcile`
- `mqh machine retry-cleanup`
- `mqh machine retry-update`
- `mqh machine provider-cli`
- `mqh machine provider-cli status`
- `mqh machine provider-cli install`

## server

- `mqh server`
- `mqh server move`
- `mqh server move status`
- `mqh server move cancel`
- `mqh server export`
- `mqh server import`
- `mqh server unlock`
- `mqh server allow-connect`
- `mqh server delete-old-copy`
- `mqh server install-machine-service`

## updates

- `bb updates`
- `bb updates status`
- `bb updates apply`
- `bb updates app`
- `bb updates app status`
- `bb updates app apply`
- `bb updates app cancel`
- `bb updates app auto`
- `bb updates app dismiss`

## terminal

- `mqh terminal`
- `mqh terminal list`
- `mqh terminal create`
- `mqh terminal start`
- `mqh terminal run`
- `mqh terminal show`
- `mqh terminal attach`
- `mqh terminal send`
- `mqh terminal resize`
- `mqh terminal output`
- `mqh terminal read`
- `mqh terminal wait`
- `mqh terminal rename`
- `mqh terminal restart`
- `mqh terminal close`
- `mqh terminal stop`

## thread

- `mqh thread`
- `mqh thread wait`
- `mqh thread spawn`
- `mqh thread create`
- `mqh thread new`
- `mqh thread fork`
- `mqh thread list`
- `mqh thread show`
- `mqh thread get`
- `mqh thread view`
- `mqh thread status`
- `mqh thread log`
- `mqh thread messages`
- `mqh thread timeline`
- `mqh thread output`
- `mqh thread open`
- `mqh thread pane`
- `mqh thread section`
- `mqh thread section list`
- `mqh thread section create`
- `mqh thread section rename`
- `mqh thread section delete`
- `mqh thread search`
- `mqh thread history`
- `mqh thread read`
- `mqh thread unread`
- `mqh thread reorder-pinned`
- `mqh thread count`
- `mqh thread queue`
- `mqh thread queue list`
- `mqh thread queue create`
- `mqh thread queue update`
- `mqh thread queue send`
- `mqh thread queue delete`
- `mqh thread queue reorder`
- `mqh thread queue group`
- `mqh thread tabs`
- `mqh thread tabs show`
- `mqh thread tabs set`
- `mqh thread update`
- `mqh thread archive`
- `mqh thread unarchive`
- `mqh thread restore-environment`
- `mqh thread pin`
- `mqh thread unpin`
- `mqh thread regenerate-title`
- `mqh thread delete`
- `mqh thread edit-message`
- `mqh thread tell`
- `mqh thread message`
- `mqh thread send`
- `mqh thread retry`
- `mqh thread stop`
- `mqh thread compact`
- `mqh thread context`
- `mqh thread commands`
- `mqh thread options`
- `mqh thread clear`
- `mqh thread cancel-plan`
- `mqh thread clear-goal`
- `mqh thread interactions`
- `mqh thread interactions list`
- `mqh thread interactions show`
- `mqh thread interactions approve`
- `mqh thread interactions grant`
- `mqh thread interactions answer`
- `mqh thread interactions respond`
- `mqh thread interactions deny`

## environment

- `mqh environment`
- `mqh environment providers`
- `mqh environment list`
- `mqh environment delete`
- `mqh environment cleanup`
- `mqh environment show`
- `mqh environment get`
- `mqh environment paths`
- `mqh environment update`
- `mqh environment commit`
- `mqh environment archive-threads`

## file

- `mqh file`
- `mqh file read`
- `mqh file write`
- `mqh file list`
- `mqh file paths`
- `mqh file mkdir`
- `mqh file move`
- `mqh file remove`

## theme

- `bb theme`
- `bb theme list`
- `bb theme set`
- `bb theme dir`
- `bb theme favicon`
- `bb theme favicon set`
- `bb theme favicon reset`
- `bb theme show`
- `bb theme reset`

## plugin

- `mqh plugin`
- `mqh plugin search`
- `mqh plugin list`
- `mqh plugin source`
- `mqh plugin install`
- `mqh plugin install-jobs`
- `mqh plugin cancel-install`
- `mqh plugin outdated`
- `mqh plugin update`
- `mqh plugin update-jobs`
- `mqh plugin new`
- `mqh plugin types`
- `mqh plugin migrate`
- `mqh plugin build`
- `mqh plugin dev`
- `mqh plugin reload`
- `mqh plugin rpc`
- `mqh plugin rpc list`
- `mqh plugin rpc inspect`
- `mqh plugin rpc call`
- `mqh plugin enable`
- `mqh plugin disable`
- `mqh plugin safe-mode`
- `mqh plugin prune`
- `mqh plugin config`
- `mqh plugin token`
- `mqh plugin run`
- `mqh plugin logs`
- `mqh plugin remove`
- `mqh plugin uninstall`

## marketplace

- `mqh marketplace`
- `mqh marketplace add`
- `mqh marketplace list`
- `mqh marketplace refresh`
- `mqh marketplace remove`

## skill

- `mqh skill`
- `mqh skill list`
- `mqh skill show`
- `mqh skill files`
- `mqh skill update`
- `mqh skill delete`
- `mqh skill search`
- `mqh skill registry`
- `mqh skill registry detail`
- `mqh skill install`
- `mqh skill cli-skills-status`
- `mqh skill install-cli-skills`

## guide

- `mqh guide`

## diagnostics

- `mqh diagnostics`
- `mqh diagnostics cli-errors`

## voice

- `mqh voice`
- `mqh voice transcribe`

## browser

- `mqh browser`
- `mqh browser instances`
- `mqh browser tabs`
- `mqh browser create`
- `mqh browser acquire`
- `mqh browser connection`
- `mqh browser release`
- `mqh browser reveal`
- `mqh browser close`
- `mqh browser capture`
- `mqh browser watch`
- `mqh browser import-sources`
- `mqh browser import-cookies`
