import { readdirSync, readFileSync } from "node:fs";
import { expect, it } from "vitest";
import {
  createFakePluginHost,
  makePluginAgentConfigurationContext,
} from "@get-bb/plugin-sdk/testing";
import plugin from "./server.js";

const bundledSkills = readdirSync(new URL("./skills", import.meta.url), {
  withFileTypes: true,
})
  .filter((entry) => entry.isDirectory())
  .map((entry) => entry.name);

it("introduces MetaCodex and MQH, never bb", async () => {
  const { bb, harness } = createFakePluginHost({
    pluginId: "metacodex-guide",
    agentSkillIds: bundledSkills,
  });
  try {
    await plugin(bb);
    const introduction = harness.registrations.instructionProvider?.({
      threadId: "thr_test",
      projectId: "proj_test",
    });
    expect(introduction).toContain("You are working inside MetaCodex");
    expect(introduction).toContain("`metacodex-context` skill");
    expect(introduction).toContain(
      "Reference an MQH thread as `@thread:thr_abc123`",
    );
    expect(introduction).toContain("Do not construct thread URLs manually");
    expect(introduction).not.toMatch(/\bbb\b|\bBB\b/u);
    expect(introduction).toBe(introduction?.trim());
  } finally {
    await harness.lifecycle.dispose();
  }
});

it("gives agents every MetaCodex skill, and the switches stay independent across reloads", async () => {
  const { bb, harness } = createFakePluginHost({
    pluginId: "metacodex-guide",
    agentSkillIds: bundledSkills,
  });
  try {
    await plugin(bb);
    const instructions = () =>
      harness.registrations.instructionProvider?.({
        threadId: "thr_test",
        projectId: "proj_test",
      });
    const skills = async () =>
      (
        await harness.behavior.resolveAgentConfiguration(
          makePluginAgentConfigurationContext(),
        )
      ).skills;
    expect((await skills()).sort()).toEqual([...bundledSkills].sort());
    await harness.behavior.setSettings({ introduction: false });
    expect(instructions()).toBeNull();
    expect((await skills()).sort()).toEqual([...bundledSkills].sort());
    await harness.behavior.setSettings({ skills: false });
    expect(await skills()).toEqual([]);
    await harness.lifecycle.reload(plugin);
    expect(instructions()).toBeNull();
    expect(await skills()).toEqual([]);
    await harness.behavior.setSettings({ introduction: true, skills: true });
    expect(instructions()).toContain("mqh status");
    expect(await skills()).toContain("metacodex-context");
  } finally {
    await harness.lifecycle.dispose();
  }
});

it("ships only MetaCodex skills, without bb product texts", () => {
  expect([...bundledSkills].sort()).toEqual([
    "metacodex-context",
    "quest-gesture-plugins",
    "quest-scripting",
    "quest-views",
    "skill-creator",
  ]);
  for (const name of bundledSkills) {
    const text = readFileSync(
      new URL(`./skills/${name}/SKILL.md`, import.meta.url),
      "utf8",
    );
    expect(text, name).not.toMatch(/\bbb\b|\bBB\b|bb-cli|bb-plugin-authoring|submit-a-plugin/u);
  }
});
