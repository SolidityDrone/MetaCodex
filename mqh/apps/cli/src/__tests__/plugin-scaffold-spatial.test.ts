// MQH: `mqh plugin new` scaffolds a server, a spatial.json panel bound to that server's RPCs, and a
// skill; no web app (upstream's React starter is gone).
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { pluginPackageJsonSchema } from "@bb/domain";
import { scaffoldPlugin } from "@bb/templates/plugin-scaffold";

describe("plugin scaffold", () => {
  let dir: string | null = null;

  afterEach(async () => {
    if (dir !== null) await rm(dir, { recursive: true, force: true });
    dir = null;
  });

  it("writes a server, a spatial panel bound to its RPCs and a skill, with no web app", async () => {
    dir = await mkdtemp(join(tmpdir(), "mqh-scaffold-"));
    const target = join(dir, "todo-plugin");
    await scaffoldPlugin({
      targetDir: target,
      packageName: "bb-plugin-todo-example",
      bbVersion: "0.0.0",
    });

    const pkg = JSON.parse(await readFile(join(target, "package.json"), "utf8"));
    expect(() => pluginPackageJsonSchema.parse(pkg)).not.toThrow();
    expect(pkg.bb.server).toBe("./server.ts");
    expect(pkg.bb.spatial).toBe("./spatial.json");
    expect(pkg.bb.app).toBeUndefined();
    expect(Object.keys(pkg.devDependencies ?? {})).not.toContain("@types/react");

    const spatial = JSON.parse(await readFile(join(target, "spatial.json"), "utf8"));
    const [list, form] = spatial.panels[0].body;
    const server = await readFile(join(target, "server.ts"), "utf8");
    for (const method of [list.bind.replace(/^rpc:/u, ""), form.submit.rpc, list.onSelect.rpc]) {
      expect(server).toContain(`${method}:`);
    }

    await expect(readFile(join(target, "app.tsx"), "utf8")).rejects.toThrow();
    await expect(
      readFile(join(target, "skills", "example-todos", "SKILL.md"), "utf8"),
    ).resolves.toContain("name: example-todos");
  });
});
