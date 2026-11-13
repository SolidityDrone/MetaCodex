import type { BbPluginApi } from "@get-bb/plugin-sdk";
import { introduction } from "./introduction.js";

/** MetaCodex's own skills (folders in ./skills), in the order agents see them. */
const METACODEX_SKILLS = [
  "metacodex-context",
  "quest-gesture-plugins",
  "quest-scripting",
  "quest-views",
  "skill-creator",
] as const;

export default async function plugin(bb: BbPluginApi) {
  const settings = bb.settings.define({
    introduction: {
      type: "boolean",
      label: "Send the MetaCodex introduction",
      description:
        "Tell agents where they run (MetaCodex on the headset), about the mqh CLI and threads. Applies to new agent sessions.",
      default: true,
    },
    skills: {
      type: "boolean",
      label: "Give agents the MetaCodex skills",
      description:
        "metacodex-context, quest-gesture-plugins, quest-scripting, quest-views and skill-creator.",
      default: true,
    },
  });
  let current = await settings.get();
  settings.onChange((next) => {
    current = next;
  });
  bb.agents.contributeInstructions(() =>
    current.introduction ? introduction : null,
  );
  bb.agents.configure(() => ({
    tools: [],
    skills: current.skills ? [...METACODEX_SKILLS] : [],
  }));
}
