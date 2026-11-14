# MetaCodex guide

Tell every agent MQH runs where it is (MetaCodex on the headset) and give it MetaCodex's
skills. The plugin and its two settings are on by default.

- `introduction`: send the MetaCodex introduction (the mqh CLI, threads, messages).
- `skills`: give agents `metacodex-context`, `quest-gesture-plugins`, `quest-scripting`,
  `quest-views`, `skill-creator` and `worktree-per-thread`.

Use `mqh plugin config metacodex-guide set <key> true|false` from the CLI. Changes apply
when the agent configuration is next assembled; they do not erase instructions from an
existing conversation.

Disabling the plugin removes its introduction and its skills from MQH threads. The app
also lays the same skills (except `skill-creator`) in `/root/.claude/skills`, so the agent
CLIs started in a terminal find them too.
