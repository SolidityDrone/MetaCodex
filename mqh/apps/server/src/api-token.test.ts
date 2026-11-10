import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { presentsApiToken, requiresApiToken, resolveApiToken } from "./api-token.js";

describe("MetaCodex API token", () => {
  it("guards the public API, the sockets and the enrollment, not health or plugin HTTP routes", () => {
    expect(requiresApiToken("/api/v1/threads")).toBe(true);
    expect(requiresApiToken("/api/v1/system/providers/state")).toBe(true);
    expect(requiresApiToken("/ws")).toBe(true);
    expect(requiresApiToken("/ws/terminals/t1")).toBe(true);
    expect(requiresApiToken("/internal/hosts/enroll-key")).toBe(true);
    expect(requiresApiToken("/health")).toBe(false);
    expect(requiresApiToken("/internal/hosts/enroll")).toBe(false); // the daemon's own credential
    expect(requiresApiToken("/api/v1/plugins/mcp/http/mcp")).toBe(false); // agents' MCP endpoint
    expect(requiresApiToken("/api/v1/plugins/mcp")).toBe(true);
  });

  it("accepts the bearer header or the query token, nothing else", () => {
    expect(presentsApiToken("s3cret", "Bearer s3cret", undefined)).toBe(true);
    expect(presentsApiToken("s3cret", undefined, "s3cret")).toBe(true);
    expect(presentsApiToken("s3cret", "Bearer wrong", undefined)).toBe(false);
    expect(presentsApiToken("s3cret", "s3cret", undefined)).toBe(false);
    expect(presentsApiToken("s3cret", undefined, undefined)).toBe(false);
  });

  it("reads MQH_API_TOKEN, else <dataDir>/api-token, else none", () => {
    const dir = mkdtempSync(path.join(tmpdir(), "mqh-token-"));
    expect(resolveApiToken(dir, {})).toBeNull();
    writeFileSync(path.join(dir, "api-token"), "from-file\n");
    expect(resolveApiToken(dir, {})).toBe("from-file");
    expect(resolveApiToken(dir, { MQH_API_TOKEN: "from-env" })).toBe("from-env");
  });
});
