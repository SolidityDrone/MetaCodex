#!/usr/bin/env node
// Stand-in for the cloud agent CLI in tests: speaks its stream-json protocol (-p --input-format stream-json
// --output-format stream-json) without a network or an account. Each user message on stdin becomes one turn:
// system/init (first turn only), an assistant message with a tool_use, the tool_result, a final text, a result.
// A message containing "FAIL" ends its turn with an error result; "EXIT" makes the process die mid-turn.
import { createInterface } from "node:readline";

const args = process.argv.slice(2);
const resume = args.includes("--resume") ? args[args.indexOf("--resume") + 1] : null;
const session = resume ?? "sess-" + Math.random().toString(36).slice(2, 10);
let turn = 0;
const out = (o) => process.stdout.write(JSON.stringify(o) + "\n");

createInterface({ input: process.stdin }).on("line", (line) => {
  const msg = JSON.parse(line);
  const text = msg.message.content.map((c) => c.text).join(" ");
  turn++;
  if (turn === 1) out({ type: "system", subtype: "init", session_id: session, model: "fake-model", cwd: process.cwd(), tools: ["Read", "Edit"], resumed: !!resume });
  if (text.includes("EXIT")) process.exit(7);
  out({ type: "assistant", message: { role: "assistant", content: [{ type: "text", text: `working on: ${text}` }, { type: "tool_use", id: `tu${turn}`, name: "Read", input: { file_path: "README.md" } }] } });
  out({ type: "user", message: { role: "user", content: [{ type: "tool_result", tool_use_id: `tu${turn}`, content: "file contents", is_error: false }] } });
  if (text.includes("FAIL")) {
    out({ type: "result", subtype: "error_during_execution", is_error: true, result: "simulated failure", total_cost_usd: 0.001, usage: { input_tokens: 10, output_tokens: 2 }, num_turns: 1 });
    return;
  }
  out({ type: "assistant", message: { role: "assistant", content: [{ type: "text", text: `done turn ${turn} (${process.env.MCX_THREAD_ID})` }] } });
  out({ type: "result", subtype: "success", is_error: false, result: "ok", total_cost_usd: 0.002, usage: { input_tokens: 120, output_tokens: 30 }, num_turns: 2, permission_denials: [] });
});
