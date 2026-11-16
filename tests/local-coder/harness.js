'use strict'
// Test harness for the local coder's API layer (local-ai/server: compat.js, engines.js, toolcalls.js) under plain
// Node, no install: bare-fs / bare-path are mapped to Node's fs / path, the model is a fake that "decodes" a fixed
// output in small pieces, and the three endpoints run with a fake request / response pair.
const Module = require('module')
const fs = require('fs')
const os = require('os')
const path = require('path')

const resolve = Module._resolveFilename
Module._resolveFilename = function (request, ...rest) {
  if (request === 'bare-fs') request = 'fs'
  else if (request === 'bare-path') request = 'path'
  return resolve.call(this, request, ...rest)
}

const SERVER = path.join(__dirname, '..', '..', 'local-ai', 'server')
const compat = require(path.join(SERVER, 'compat.js'))
const toolcalls = require(path.join(SERVER, 'toolcalls.js'))
const { Engines } = require(path.join(SERVER, 'engines.js'))

// ---------------------------------------------------------------- the harnesses' tools (their real names and shapes)

const prop = (type, description) => ({ type, description })
const CLAUDE_TOOLS = [
  { name: 'Bash', description: 'Executes a given bash command and returns its output.\n\nThe working directory persists between commands.', input_schema: { type: 'object', properties: { command: prop('string', 'The command to execute'), timeout: prop('number', 'Optional timeout in milliseconds'), description: prop('string', 'Clear, concise description of what this command does in 5-10 words'), run_in_background: prop('boolean', 'Run in the background') }, required: ['command'], additionalProperties: false } },
  { name: 'Read', description: 'Reads a file from the local filesystem.', input_schema: { type: 'object', properties: { file_path: prop('string', 'The absolute path to the file to read'), offset: prop('number', 'Line to start at'), limit: prop('number', 'Lines to read') }, required: ['file_path'], additionalProperties: false } },
  { name: 'Write', description: 'Writes a file to the local filesystem.', input_schema: { type: 'object', properties: { file_path: prop('string', 'The absolute path to the file to write'), content: prop('string', 'The content to write') }, required: ['file_path', 'content'], additionalProperties: false } },
  { name: 'Edit', description: 'Performs exact string replacements in files.', input_schema: { type: 'object', properties: { file_path: prop('string'), old_string: prop('string'), new_string: prop('string') }, required: ['file_path', 'old_string', 'new_string'] } },
  { name: 'Glob', description: 'Fast file pattern matching.', input_schema: { type: 'object', properties: { pattern: prop('string') }, required: ['pattern'] } },
  { name: 'TodoWrite', description: 'Create and manage a task list.', input_schema: { type: 'object', properties: { todos: { type: 'array' } }, required: ['todos'] } }
]
const fn = (name, description, properties, required) => ({ type: 'function', function: { name, description, parameters: { type: 'object', properties, required } } })
const OPENCODE_TOOLS = [
  fn('bash', 'Executes a given bash command in a persistent shell session.', { command: prop('string', 'The command to execute'), timeout: prop('number'), workdir: prop('string'), description: prop('string', 'Clear, concise description of what this command does in 5-10 words') }, ['command', 'description']),
  fn('read', 'Reads a file from the local filesystem.', { filePath: prop('string', 'The path to the file to read'), offset: prop('number'), limit: prop('number') }, ['filePath']),
  fn('write', 'Writes a file to the local filesystem.', { content: prop('string', 'The content to write to the file'), filePath: prop('string', 'The path to the file') }, ['content', 'filePath']),
  fn('edit', 'Performs exact string replacements in files.', { filePath: prop('string'), oldString: prop('string'), newString: prop('string') }, ['filePath', 'oldString', 'newString']),
  fn('glob', 'Fast file pattern matching.', { pattern: prop('string') }, ['pattern']),
  fn('todowrite', 'Manage a task list.', { todos: { type: 'array' } }, ['todos'])
]
const CODEX_TOOLS = [
  { type: 'function', name: 'exec_command', description: 'Runs a command in a PTY, returning output or a session ID for ongoing interaction.', parameters: { type: 'object', properties: { cmd: prop('string', 'Shell command to execute.'), workdir: prop('string'), yield_time_ms: prop('number'), max_output_tokens: prop('number'), login: prop('boolean') }, required: ['cmd'] } },
  { type: 'function', name: 'write_stdin', description: 'Writes characters to an existing session.', parameters: { type: 'object', properties: { session_id: prop('number'), chars: prop('string') }, required: ['session_id'] } },
  { type: 'function', name: 'update_plan', description: 'Updates the task plan.', parameters: { type: 'object', properties: { plan: { type: 'array' } }, required: ['plan'] } },
  { type: 'custom', name: 'apply_patch', description: 'Edit files with a patch.', format: { type: 'grammar' } }
]

// a long harness prompt with its environment facts and project instructions (OpenCode's format)
const LONG_SYSTEM = 'You are an interactive CLI tool that helps users with software engineering tasks.\n' + 'Follow the style rules. '.repeat(300) +
  '\n<env>\n  Working directory: /root/demo\n  Platform: linux\n  Today\'s date: 2026-11-16\n</env>\n\nInstructions from: /root/demo/AGENTS.md\nBuild with make. ' + 'Keep it short. '.repeat(200)

// ---------------------------------------------------------------- the engine with a fake model

/** A real Engines whose coder / llm "decodes" `output` in pieces of `step` characters; records what it was asked. */
function fakeEngines (output, step = 3) {
  const cacheDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mcx-lc-'))
  const e = new Engines({ models: { file: () => '' }, cfg: { coderUnloadsInterface: false }, log: () => {}, cacheDir })
  clearInterval(e.idleTimer)
  e.calls = []
  e._ensure = async () => ({
    run: async (prompt, runOpts) => {
      e.calls.push({ prompt, runOpts })
      const pieces = []
      for (let i = 0; i < output.length; i += step) pieces.push(output.slice(i, i + step))
      return {
        stats: { promptTokens: 100, generatedTokens: pieces.length },
        cancel: async () => {},
        iterate: async function * () { for (const p of pieces) yield p }
      }
    }
  })
  return e
}

// ---------------------------------------------------------------- fake HTTP

function fakeRes () {
  const r = { statusCode: 0, headers: {}, chunks: [], body: null, ended: false }
  r.setHeader = (k, v) => { r.headers[k.toLowerCase()] = v }
  r.write = (s) => { r.chunks.push(String(s)); return true }
  r.end = (s) => { if (s != null) r.chunks.push(String(s)); r.ended = true }
  return r
}
const fakeReq = () => ({ socket: { on () {} } })
function send (res, code, obj) { res.statusCode = code; res.body = obj; res.ended = true }
// as main.js's sse()
function sse (req, res) {
  const abort = { aborted: false, onabort: null }
  res.statusCode = 200
  res.setHeader('Content-Type', 'text/event-stream')
  return { abort, event (name, obj) { res.write('event: ' + name + '\ndata: ' + JSON.stringify(obj) + '\n\n') }, end () { res.end() } }
}
function sseData (res) {
  return res.chunks.join('').split('\n\n').map((b) => b.split('\n').find((l) => l.startsWith('data: '))).filter(Boolean).map((l) => l.slice(6)).filter((d) => d !== '[DONE]').map((d) => JSON.parse(d))
}

// ---------------------------------------------------------------- the three endpoints -> { text, calls, deltas, raw }

const CTX = (engines) => ({ engines, send, sse, log: () => {}, cfg: { coderSlim: true }, ids: { llm: 'qwen3-0.6b-q4_0', coder: 'qwen2.5-coder-0.5b' } })

/** OpenCode: POST /v1/chat/completions, model local-coder. */
async function chatApi (output, { stream = false, user = 'What is a closure?', tools = OPENCODE_TOOLS, system = LONG_SYSTEM, temperature } = {}) {
  const engines = fakeEngines(output)
  const res = fakeRes()
  const j = { model: 'local-coder', stream, temperature, messages: [{ role: 'system', content: system }, { role: 'user', content: user }], tools }
  await compat.chatCompletions(fakeReq(), res, j, CTX(engines))
  if (!stream) {
    const m = res.body.choices[0].message
    return { engines, raw: res.body, text: m.content || '', finish: res.body.choices[0].finish_reason, calls: (m.tool_calls || []).map((c) => ({ name: c.function.name, args: JSON.parse(c.function.arguments) })) }
  }
  const data = sseData(res)
  const deltas = data.map((d) => d.choices[0].delta.content).filter((c) => c != null)
  const last = data[data.length - 1]
  return { engines, raw: data, text: deltas.join(''), deltas, finish: last.choices[0].finish_reason, calls: (last.choices[0].delta.tool_calls || []).map((c) => ({ name: c.function.name, args: JSON.parse(c.function.arguments) })) }
}

/** Claude Code: POST /v1/messages. */
async function messagesApi (output, { stream = false, user = 'What is a closure?', tools = CLAUDE_TOOLS, system = LONG_SYSTEM, temperature = 1 } = {}) {
  const engines = fakeEngines(output)
  const res = fakeRes()
  const j = { model: 'local-coder', stream, max_tokens: 1024, temperature, system: [{ type: 'text', text: system }], messages: [{ role: 'user', content: [{ type: 'text', text: user }] }], tools }
  await compat.messagesApi(fakeReq(), res, j, CTX(engines))
  if (!stream) {
    const c = res.body.content
    return { engines, raw: res.body, text: c.filter((b) => b.type === 'text').map((b) => b.text).join(''), finish: res.body.stop_reason, calls: c.filter((b) => b.type === 'tool_use').map((b) => ({ name: b.name, args: b.input })) }
  }
  const data = sseData(res)
  const blocks = {}
  const deltas = []
  let finish = null
  for (const d of data) {
    if (d.type === 'content_block_start') blocks[d.index] = Object.assign({}, d.content_block, { json: '' })
    else if (d.type === 'content_block_delta' && d.delta.type === 'text_delta') { blocks[d.index].text += d.delta.text; deltas.push(d.delta.text) } else if (d.type === 'content_block_delta' && d.delta.type === 'input_json_delta') blocks[d.index].json += d.delta.partial_json
    else if (d.type === 'message_delta') finish = d.delta.stop_reason
  }
  const list = Object.keys(blocks).sort((a, b) => a - b).map((k) => blocks[k])
  return { engines, raw: data, deltas, finish, text: list.filter((b) => b.type === 'text').map((b) => b.text).join(''), calls: list.filter((b) => b.type === 'tool_use').map((b) => ({ name: b.name, args: JSON.parse(b.json) })) }
}

/** Codex: POST /v1/responses. */
async function responsesApi (output, { stream = false, user = 'What is a closure?', tools = CODEX_TOOLS, system = LONG_SYSTEM } = {}) {
  const engines = fakeEngines(output)
  const res = fakeRes()
  const j = { model: 'local-coder', stream, instructions: system, input: [{ type: 'message', role: 'user', content: [{ type: 'input_text', text: user }] }], tools }
  await compat.responses(fakeReq(), res, j, CTX(engines))
  const fromOutput = (out) => ({
    text: out.filter((o) => o.type === 'message').map((o) => o.content.map((c) => c.text).join('')).join(''),
    calls: out.filter((o) => o.type === 'function_call').map((o) => ({ name: o.name, args: JSON.parse(o.arguments) }))
  })
  if (!stream) return Object.assign({ engines, raw: res.body }, fromOutput(res.body.output))
  const data = sseData(res)
  const deltas = data.filter((d) => d.type === 'response.output_text.delta').map((d) => d.delta)
  const done = data.find((d) => d.type === 'response.completed')
  const items = data.filter((d) => d.type === 'response.output_item.done').map((d) => d.item)
  const r = fromOutput(items)
  const final = fromOutput(done.response.output)
  return { engines, raw: data, deltas, text: r.text, calls: r.calls, final }
}

module.exports = { compat, toolcalls, fakeEngines, chatApi, messagesApi, responsesApi, CLAUDE_TOOLS, OPENCODE_TOOLS, CODEX_TOOLS, LONG_SYSTEM }
