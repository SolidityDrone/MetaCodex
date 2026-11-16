'use strict'
// Agent harnesses on the local coding model (docs/local-coder.md): the API shapes they speak, translated to the
// service's chat engine (engines.chat with engine 'coder'):
//   OpenCode      OpenAI chat completions      POST /v1/chat/completions  (main.js; model "local-coder")
//   Codex         OpenAI Responses API          POST /v1/responses        (wire_api = "responses"; chat is gone)
//   Claude Code   Anthropic Messages API        POST /v1/messages         (ANTHROPIC_BASE_URL)
// Each takes "stream": true (server-sent events in that API's own event format). Tool calls go both ways: the
// model's calls are parsed in toolcalls.js (tagged, fenced, bare JSON) and go out as tool_calls / function_call items /
// tool_use blocks, never as text (docs/local-coder.md, "Tool calls in the model's text").

const { familyOf } = require('./toolcalls')

const CODER_NAMES = /coder|qwen3-1\.7b|local/i

/** Does a request's `model` ask for the local coding model? */
function wantsCoder (model) { return typeof model === 'string' && CODER_NAMES.test(model) }

function text (v) {
  if (v == null) return ''
  if (typeof v === 'string') return v
  if (Array.isArray(v)) return v.map(text).join('')
  if (typeof v === 'object') return v.text != null ? String(v.text) : v.content != null ? text(v.content) : v.output != null ? text(v.output) : ''
  return String(v)
}

const newId = (p) => p + Date.now().toString(36) + Math.random().toString(36).slice(2, 8)

// Claude Code asks for temperature 1: a 0.5B model then writes more broken JSON and wanders. The coder runs at most at
// this temperature (service config coderMaxTemperature).
function coderTemperature (t, cfg) {
  const max = cfg && cfg.coderMaxTemperature != null ? Number(cfg.coderMaxTemperature) : 0.2
  return t == null ? undefined : Math.min(Number(t), max)
}

/** What a parse left out or changed, for the log line: " (echo as text)", " (broken call)". */
function parseNote (r) {
  return (r && r.echoed ? ' (echo as text)' : '') + (r && r.broken ? ' (broken call)' : '') + (r && r.repeatStopped ? ' (repeat stopped)' : '')
}

// ---------------------------------------------------------------- OpenAI chat completions (OpenCode; the app's LLM)

/**
 * POST /v1/chat/completions for both engines: the app's interface LLM, and the local coder when `model` names it
 * (OpenCode's provider, model "local-coder"). ctx: { engines, send, log, cfg, ids: { llm, coder } } (model ids).
 */
async function chatCompletions (req, res, j, ctx) {
  const { engines, send, log } = ctx
  const cfg = ctx.cfg || {}
  if (!Array.isArray(j.messages) || !j.messages.length) throw Object.assign(new Error('messages is required'), { status: 400 })
  const coder = wantsCoder(j.model)
  // the local coder gets the slim profile (slim): a short system prompt, the core tools (OpenCode threads)
  if (coder && cfg.coderSlim !== false) Object.assign(j, slim({ messages: j.messages.map((m) => Object.assign({}, m, { content: Array.isArray(m.content) ? m.content.map((p) => typeof p === 'string' ? p : p.text || '').join('') : m.content })), tools: j.tools || [] }))
  const opts = { max_tokens: j.max_tokens, temperature: coder ? coderTemperature(j.temperature, cfg) : j.temperature, grammar: j.grammar, engine: coder ? 'coder' : 'llm' }
  const model = (ctx.ids || {})[coder ? 'coder' : 'llm'] || j.model
  const tag = coder ? 'coder chat' : 'llm'
  if (j.stream) return chatCompletionsStream(req, res, j, ctx, opts, model, tag)
  const r = await engines.chat(j.messages, Array.isArray(j.tools) ? j.tools : [], opts)
  const calls = r.tool_calls.map((c, i) => ({ id: 'call_' + Date.now().toString(36) + i, type: 'function', function: { name: c.name, arguments: JSON.stringify(c.arguments || {}) } }))
  const st = r.stats || {}
  log(`${tag} ${r.ms} ms (first ${r.firstMs} ms, prompt ${st.promptTokens || '?'} tok)${parseNote(r)}: ${calls.length ? calls.map((c) => c.function.name + c.function.arguments).join(' ') : JSON.stringify(r.content).slice(0, 120)}`)
  send(res, 200, {
    id: 'chatcmpl-' + Date.now().toString(36),
    object: 'chat.completion',
    created: Math.floor(Date.now() / 1000),
    model,
    choices: [{ index: 0, message: Object.assign({ role: 'assistant', content: r.content || null }, calls.length ? { tool_calls: calls } : {}), finish_reason: calls.length ? 'tool_calls' : 'stop' }],
    usage: { prompt_tokens: st.promptTokens || 0, completion_tokens: st.generatedTokens || 0, total_tokens: (st.promptTokens || 0) + (st.generatedTokens || 0) },
    timings: { ms: r.ms, firstMs: r.firstMs, tps: st.TPS || null }
  })
}

/** stream: true -> text/event-stream, OpenAI chunk format: a role chunk, one delta.content per decoded token (no
 * <think> block, no <tool_call> text; the coder's text from a possible call on is held, engines.chat), a final chunk
 * with finish_reason (and parsed tool_calls), then [DONE]. The client closing the connection cancels the generation
 * (a queued one is skipped). */
async function chatCompletionsStream (req, res, j, { engines, log }, opts, model, tag) {
  const id = 'chatcmpl-' + Date.now().toString(36)
  const created = Math.floor(Date.now() / 1000)
  const abort = { aborted: false, onabort: null }
  const gone = () => { if (abort.aborted) return; abort.aborted = true; if (abort.onabort) abort.onabort() }
  // the socket, not the request: bare-http1 closes the request object once its body has been read
  try { if (req.socket) req.socket.on('close', gone) } catch {}
  res.statusCode = 200
  res.setHeader('Content-Type', 'text/event-stream')
  res.setHeader('Cache-Control', 'no-cache')
  const chunk = (delta, finish, extra) => {
    if (abort.aborted) return
    const o = { id, object: 'chat.completion.chunk', created, model, choices: [{ index: 0, delta, finish_reason: finish || null }] }
    try { res.write('data: ' + JSON.stringify(Object.assign(o, extra || {})) + '\n\n') } catch { gone() }
  }
  chunk({ role: 'assistant' })
  let r
  try {
    r = await engines.chat(j.messages, Array.isArray(j.tools) ? j.tools : [], Object.assign({}, opts, { abort, onToken: (t) => chunk({ content: t }) }))
  } catch (e) {
    chunk({}, 'error', { error: String(e.message || e) })
    try { res.end('data: [DONE]\n\n') } catch {}
    throw Object.assign(e, { streamed: true })
  }
  const calls = r.tool_calls.map((c, i) => ({ index: i, id: 'call_' + Date.now().toString(36) + i, type: 'function', function: { name: c.name, arguments: JSON.stringify(c.arguments || {}) } }))
  const st = r.stats || {}
  log(`${tag} stream ${r.ms} ms (first ${r.firstMs} ms, prompt ${st.promptTokens || '?'} tok)${r.cancelled ? ' cancelled' : ''}${parseNote(r)}: ${calls.length ? calls.map((c) => c.function.name).join(' ') + ' ' : ''}${JSON.stringify(r.content).slice(0, 120)}`)
  chunk(calls.length ? { tool_calls: calls } : {}, calls.length ? 'tool_calls' : 'stop', {
    usage: { prompt_tokens: st.promptTokens || 0, completion_tokens: st.generatedTokens || 0 }, timings: { ms: r.ms, firstMs: r.firstMs, tps: st.TPS || null }
  })
  try { res.end('data: [DONE]\n\n') } catch {}
}

// ---------------------------------------------------------------- OpenAI Responses API (Codex)

/** Responses `instructions` + `input` items -> chat messages; `tools` -> chat tools. */
function responsesToChat (j) {
  const messages = []
  if (j.instructions) messages.push({ role: 'system', content: text(j.instructions) })
  const items = typeof j.input === 'string' ? [{ type: 'message', role: 'user', content: j.input }] : (j.input || [])
  for (const it of items) {
    const type = it.type || (it.role ? 'message' : '')
    if (type === 'message') {
      const role = it.role === 'developer' ? 'system' : it.role
      messages.push({ role, content: text(it.content) })
    } else if (type === 'function_call') {
      const last = messages[messages.length - 1]
      const call = { id: it.call_id || it.id, function: { name: it.name, arguments: it.arguments || '{}' } }
      if (last && last.role === 'assistant' && Array.isArray(last.tool_calls)) last.tool_calls.push(call)
      else messages.push({ role: 'assistant', content: '', tool_calls: [call] })
    } else if (type === 'function_call_output') {
      messages.push({ role: 'tool', tool_call_id: it.call_id, content: text(it.output) })
    } else if (type === 'local_shell_call' || type === 'custom_tool_call') {
      messages.push({ role: 'assistant', content: '', tool_calls: [{ id: it.call_id, function: { name: it.name || 'shell', arguments: JSON.stringify(it.action || it.input || {}) } }] })
    } else if (type === 'local_shell_call_output' || type === 'custom_tool_call_output') {
      messages.push({ role: 'tool', tool_call_id: it.call_id, content: text(it.output) })
    } // reasoning items and others: nothing the model needs
  }
  const tools = (j.tools || []).filter((t) => t.type === 'function' || t.parameters).map((t) => ({ type: 'function', function: { name: t.name, description: t.description || '', parameters: t.parameters || { type: 'object', properties: {} } } }))
  return { messages: mergeSystem(messages), tools }
}

/** A Responses object from the engine's result. */
function responseObject (id, model, r, status) {
  const output = []
  if (r && r.content) output.push({ type: 'message', id: newId('msg_'), status: 'completed', role: 'assistant', content: [{ type: 'output_text', text: r.content, annotations: [] }] })
  for (const c of (r && r.tool_calls) || []) {
    if (!c.name) continue
    output.push({ type: 'function_call', id: newId('fc_'), call_id: newId('call_'), name: c.name, arguments: JSON.stringify(c.arguments || {}), status: 'completed' })
  }
  const st = (r && r.stats) || {}
  return {
    id, object: 'response', created_at: Math.floor(Date.now() / 1000), status: status || 'completed', model, output,
    usage: { input_tokens: st.promptTokens || 0, output_tokens: st.generatedTokens || 0, total_tokens: (st.promptTokens || 0) + (st.generatedTokens || 0) }
  }
}

async function responses (req, res, j, { engines, send, sse, log, cfg }) {
  const { messages, tools } = slim(responsesToChat(j), !cfg || cfg.coderSlim !== false)
  const id = newId('resp_')
  const model = j.model || 'local-coder'
  const opts = { engine: 'coder', max_tokens: j.max_output_tokens, temperature: coderTemperature(j.temperature, cfg) }
  if (!j.stream) {
    const r = await engines.chat(messages, tools, opts)
    log(`coder responses ${r.ms} ms, prompt ${(r.stats || {}).promptTokens || '?'} tok${parseNote(r)}: ${r.tool_calls.length ? r.tool_calls.map((c) => c.name).join(' ') : JSON.stringify(r.content).slice(0, 80)}`)
    return send(res, 200, responseObject(id, model, r))
  }
  const ev = sse(req, res)
  let seq = 0
  const emit = (type, o) => ev.event(type, Object.assign({ type, sequence_number: seq++ }, o))
  emit('response.created', { response: responseObject(id, model, null, 'in_progress') })
  const msgId = newId('msg_')
  let opened = false
  let streamed = ''
  const r = await engines.chat(messages, tools, Object.assign(opts, {
    abort: ev.abort,
    onToken: (t) => {
      if (!opened) {
        opened = true
        emit('response.output_item.added', { output_index: 0, item: { type: 'message', id: msgId, status: 'in_progress', role: 'assistant', content: [] } })
        emit('response.content_part.added', { item_id: msgId, output_index: 0, content_index: 0, part: { type: 'output_text', text: '', annotations: [] } })
      }
      streamed += t
      emit('response.output_text.delta', { item_id: msgId, output_index: 0, content_index: 0, delta: t })
    }
  }))
  let index = 0
  if (opened) {
    emit('response.output_text.done', { item_id: msgId, output_index: 0, content_index: 0, text: streamed })
    emit('response.content_part.done', { item_id: msgId, output_index: 0, content_index: 0, part: { type: 'output_text', text: streamed, annotations: [] } })
    emit('response.output_item.done', { output_index: 0, item: { type: 'message', id: msgId, status: 'completed', role: 'assistant', content: [{ type: 'output_text', text: streamed, annotations: [] }] } })
    index = 1
  }
  const final = responseObject(id, model, Object.assign({}, r, { content: streamed || r.content }))
  for (const item of final.output.filter((o) => o.type === 'function_call')) {
    emit('response.output_item.added', { output_index: index, item: Object.assign({}, item, { status: 'in_progress', arguments: '' }) })
    emit('response.function_call_arguments.delta', { item_id: item.id, output_index: index, delta: item.arguments })
    emit('response.function_call_arguments.done', { item_id: item.id, output_index: index, arguments: item.arguments })
    emit('response.output_item.done', { output_index: index, item })
    index++
  }
  emit('response.completed', { response: final })
  log(`coder responses stream ${r.ms} ms, prompt ${(r.stats || {}).promptTokens || '?'} tok${r.cancelled ? ' cancelled' : ''}${parseNote(r)}: ${final.output.map((o) => o.type === 'function_call' ? o.name : 'text').join(' ')}`)
  ev.end()
}

// ---------------------------------------------------------------- Anthropic Messages API (Claude Code)

function anthropicToChat (j) {
  const messages = []
  const sys = Array.isArray(j.system) ? j.system.map(text).join('\n\n') : text(j.system)
  if (sys) messages.push({ role: 'system', content: sys })
  for (const m of j.messages || []) {
    const blocks = typeof m.content === 'string' ? [{ type: 'text', text: m.content }] : (m.content || [])
    if (m.role === 'assistant') {
      const out = { role: 'assistant', content: '', tool_calls: [] }
      for (const b of blocks) {
        if (b.type === 'text') out.content += b.text || ''
        else if (b.type === 'tool_use') out.tool_calls.push({ id: b.id, function: { name: b.name, arguments: JSON.stringify(b.input || {}) } })
      }
      if (!out.tool_calls.length) delete out.tool_calls
      messages.push(out)
    } else {
      let userText = ''
      for (const b of blocks) {
        if (b.type === 'tool_result') messages.push({ role: 'tool', tool_call_id: b.tool_use_id, content: (b.is_error ? 'ERROR: ' : '') + text(b.content) })
        else if (b.type === 'text') userText += (userText ? '\n' : '') + (b.text || '')
      }
      if (userText) messages.push({ role: 'user', content: userText })
    }
  }
  const tools = (j.tools || []).filter((t) => t.name && t.input_schema).map((t) => ({ type: 'function', function: { name: t.name, description: t.description || '', parameters: t.input_schema } }))
  return { messages: mergeSystem(messages), tools }
}

function anthropicContent (r) {
  const content = []
  if (r.content) content.push({ type: 'text', text: r.content })
  for (const c of r.tool_calls || []) if (c.name) content.push({ type: 'tool_use', id: newId('toolu_'), name: c.name, input: c.arguments || {} })
  return content
}

async function messagesApi (req, res, j, { engines, send, sse, log, cfg }) {
  const { messages, tools } = slim(anthropicToChat(j), !cfg || cfg.coderSlim !== false)
  const id = newId('msg_')
  const model = j.model || 'local-coder'
  const opts = { engine: 'coder', max_tokens: j.max_tokens, temperature: coderTemperature(j.temperature, cfg) }
  if (!j.stream) {
    const r = await engines.chat(messages, tools, opts)
    const st = r.stats || {}
    const content = anthropicContent(r)
    log(`coder messages ${r.ms} ms, prompt ${(r.stats || {}).promptTokens || '?'} tok${parseNote(r)}: ${content.map((b) => b.type === 'tool_use' ? b.name : 'text').join(' ')}`)
    return send(res, 200, { id, type: 'message', role: 'assistant', model, content, stop_reason: content.some((b) => b.type === 'tool_use') ? 'tool_use' : 'end_turn', stop_sequence: null, usage: { input_tokens: st.promptTokens || 0, output_tokens: st.generatedTokens || 0 } })
  }
  const ev = sse(req, res)
  ev.event('message_start', { type: 'message_start', message: { id, type: 'message', role: 'assistant', model, content: [], stop_reason: null, stop_sequence: null, usage: { input_tokens: 0, output_tokens: 0 } } })
  let opened = false
  const r = await engines.chat(messages, tools, Object.assign(opts, {
    abort: ev.abort,
    onToken: (t) => {
      if (!opened) { opened = true; ev.event('content_block_start', { type: 'content_block_start', index: 0, content_block: { type: 'text', text: '' } }) }
      ev.event('content_block_delta', { type: 'content_block_delta', index: 0, delta: { type: 'text_delta', text: t } })
    }
  }))
  let index = 0
  if (opened) { ev.event('content_block_stop', { type: 'content_block_stop', index: 0 }); index = 1 }
  const uses = anthropicContent(Object.assign({}, r, { content: '' }))
  for (const b of uses) {
    ev.event('content_block_start', { type: 'content_block_start', index, content_block: { type: 'tool_use', id: b.id, name: b.name, input: {} } })
    ev.event('content_block_delta', { type: 'content_block_delta', index, delta: { type: 'input_json_delta', partial_json: JSON.stringify(b.input) } })
    ev.event('content_block_stop', { type: 'content_block_stop', index })
    index++
  }
  const st = r.stats || {}
  ev.event('message_delta', { type: 'message_delta', delta: { stop_reason: uses.length ? 'tool_use' : 'end_turn', stop_sequence: null }, usage: { output_tokens: st.generatedTokens || 0 } })
  ev.event('message_stop', { type: 'message_stop' })
  log(`coder messages stream ${r.ms} ms, prompt ${(r.stats || {}).promptTokens || '?'} tok${r.cancelled ? ' cancelled' : ''}${parseNote(r)}: ${uses.length ? uses.map((b) => b.name).join(' ') : 'text'}`)
  ev.end()
}

/** Claude Code asks how many tokens a request has: a rough count (4 characters a token) is enough for it. */
function countTokens (j) {
  const { messages, tools } = anthropicToChat(j)
  const chars = JSON.stringify(messages).length + JSON.stringify(tools).length
  return { input_tokens: Math.ceil(chars / 4) }
}

// ---------------------------------------------------------------- the slim profile

// Owner 2026-11-12: "if using local shall use slim agent profiles". A harness sends a long system prompt and many
// tools (Claude Code's first turn: 11,071 prompt tokens, 300 s to read on the PC, 10-20 min on the headset); a small
// model gets lost in it anyway. For the local coder the system prompt becomes a short one (slimHeader) plus the
// harness's own environment facts (working folder, platform, date) and the first part of the project's instructions,
// and only the core file / shell tools go, their descriptions cut and only their required arguments kept.
// The harness's prompt is replaced, not cut to its first N characters: those are its identity and style rules, which
// a 0.5B model cannot follow. The tool list itself is written by the model's own chat template (Qwen2.5-Coder's
// GGUF: <tools> with one JSON schema a line, then "return ... within <tool_call></tool_call> tags").
// The 0.5B coder (2026-11-13) picked "edit" for a file that did not exist and looped on the error: only three tools
// now, shell, read and write (a whole file), so every task has one obvious way.
// 2026-11-16 (owner: "I asked a question and it answered with a JSON to echo a hello world"): the prompt says first
// that a question gets a plain answer with no tool, and names the tools as the request does (Claude Code: Bash).
const SLIM_TOOLS = /^(read|write|bash|read_file|write_file|shell|exec_command)$/i
const SLIM_KEEP_SYSTEM_CHARS = 1200 // a request with no tools (a title, a summary): its system prompt, up to this
const PROJECT_NOTES_CHARS = 1500 // the project's instructions (CLAUDE.md, AGENTS.md) the coder keeps: the first part
const TOOL_LINES = { shell: 'runs a shell command', read: 'reads a file', write: 'writes a whole file (new or changed); give its full content' }

/** The coder's own short system prompt, naming the request's tools. */
function slimHeader (tools) {
  const lines = [
    'You are a coding assistant on a small local model on a VR headset.',
    'If the user asks a question or just talks, answer in plain text and call no tool.'
  ]
  if (tools.length) {
    lines.push('When the user asks you to create or change files or to run something, use a tool, one call at a time:')
    for (const t of tools) lines.push(`- ${t.function.name}: ${TOOL_LINES[familyOf(t.function.name)] || t.function.description}`)
    lines.push('Use paths inside the working directory. When the task is done, say in one or two sentences what you did.')
  }
  return lines.join('\n')
}

function slimSystem (text, tools) {
  text = String(text || '')
  if (!tools.length) return text.length <= SLIM_KEEP_SYSTEM_CHARS ? text : cut(text, SLIM_KEEP_SYSTEM_CHARS)
  const facts = []
  for (const m of text.matchAll(/<env>[\s\S]*?<\/env>/g)) facts.push(m[0])
  if (!facts.length) {
    for (const line of text.split('\n')) {
      if (/working directory|current directory|\bcwd\b|platform|operating system|today'?s date|is a git repo/i.test(line) && line.length < 300) facts.push(line.trim())
      if (facts.length >= 8) break
    }
  }
  // OpenCode puts the project's instructions in its system prompt ("Instructions from: <path>" + the file)
  const notes = []
  for (const m of text.matchAll(/(^|\n)Instructions from: [^\n]*\n[\s\S]*?(?=\n\nInstructions from: |$)/g)) notes.push(m[0].trim())
  return slimHeader(tools) + (facts.length ? '\n\n' + facts.join('\n') : '') + (notes.length ? '\n\n' + cut(notes.join('\n\n'), PROJECT_NOTES_CHARS) : '')
}

function cut (s, n) { s = String(s || ''); return s.length > n ? s.slice(0, n).replace(/\s+\S*$/, '') + '…' : s }

// the order a small model writes a tool's arguments in (the tool grammar of the model's template holds it to the
// schema's order): the command or the path first, then the content
const ARG_ORDER = [/^(command|cmd)$/, /path|file/i, /^(content|contents|text)$/]
const argRank = (k) => { const i = ARG_ORDER.findIndex((re) => re.test(k)); return i < 0 ? ARG_ORDER.length : i }

/** A tool's schema with its required arguments only (all its arguments when none is required), short descriptions. */
function slimParams (p) {
  if (!p || typeof p !== 'object' || !p.properties) return p || { type: 'object', properties: {} }
  const names = Object.keys(p.properties)
  const req = Array.isArray(p.required) ? p.required.filter((k) => k in p.properties) : []
  const keep = (req.length ? req : names).slice().sort((a, b) => argRank(a) - argRank(b))
  const properties = {}
  for (const k of keep) {
    const s = p.properties[k] || {}
    properties[k] = typeof s.description === 'string' ? Object.assign({}, s, { description: cut(s.description.split('\n')[0], 100) }) : s
  }
  return Object.assign({ type: 'object', properties, required: req.length ? keep : [] }, p.additionalProperties === false ? { additionalProperties: false } : {})
}

/** The project's instructions in a user turn, cut to their first part. */
function capNotes (text) {
  if (text.length <= PROJECT_NOTES_CHARS) return text
  const close = /<\/(system-reminder|INSTRUCTIONS)>\s*$/.exec(text)
  return cut(text, PROJECT_NOTES_CHARS) + '\n(the rest of the project instructions is left out)' + (close ? '\n' + close[0].trim() + '\n' : '')
}

/** The slim profile for one request: a short system prompt with the environment facts, the core tools only. */
function slim ({ messages, tools }, on = true) {
  if (!on) return { messages, tools }
  let kept = (tools || []).filter((t) => SLIM_TOOLS.test(((t.function || t).name) || ''))
  if (!kept.length) kept = (tools || []).slice(0, 6)
  kept = kept.map((t) => {
    const f = t.function || t
    return { type: 'function', function: { name: f.name, description: cut((f.description || '').split('\n')[0], 200), parameters: slimParams(f.parameters) } }
  })
  // harness reminders inside user turns (Claude Code: <system-reminder> blocks with skill lists, todos) go too, except
  // the project's own instructions (CLAUDE.md / AGENTS.md: e.g. how a native app is built and installed here), cut
  const msgs = mergeSystem(messages).map((m) => m.role === 'system' ? { role: 'system', content: slimSystem(m.content, kept) }
    : m.role === 'user' && typeof m.content === 'string' ? Object.assign({}, m, { content: m.content.replace(/<system-reminder>[\s\S]*?<\/system-reminder>\s*/g, (r) => /claudeMd|Contents of [^\n]*(CLAUDE|AGENTS)\.md/.test(r) ? capNotes(r) + '\n' : '').replace(/<INSTRUCTIONS>[\s\S]*?<\/INSTRUCTIONS>/g, capNotes).trim() || m.content }) : m)
  // no system prompt at all: the short one, so a question still gets a plain answer
  if (kept.length && !msgs.some((m) => m.role === 'system')) msgs.unshift({ role: 'system', content: slimHeader(kept) })
  return { messages: fitHistory(msgs), tools: kept }
}

// The coder's context is 16k tokens (ShellLocalCoder's OpenCode config: 16384, 1024 out): the history is kept under
// ~9k tokens (4 characters a token) so the prompt, the tools and the answer fit. A long tool result is cut; then the
// oldest turns after the task (the first user message) go, a turn at a time (an assistant message with the tool
// results that answer it stays together), never the last two turns.
const SLIM_HISTORY_CHARS = 36000
const SLIM_TOOL_RESULT_CHARS = 4000

function msgSize (m) {
  return (typeof m.content === 'string' ? m.content.length : JSON.stringify(m.content || '').length) + (m.tool_calls ? JSON.stringify(m.tool_calls).length : 0)
}

function fitHistory (msgs) {
  msgs = msgs.map((m) => m.role === 'tool' && typeof m.content === 'string' && m.content.length > SLIM_TOOL_RESULT_CHARS
    ? Object.assign({}, m, { content: m.content.slice(0, SLIM_TOOL_RESULT_CHARS) + '\n... (cut: the rest of this output is left out)' }) : m)
  let total = msgs.reduce((a, m) => a + msgSize(m), 0)
  if (total <= SLIM_HISTORY_CHARS) return msgs
  const first = msgs.findIndex((m) => m.role === 'user')
  if (first < 0) return msgs
  // turns after the task: a user message, or an assistant message with the tool messages right after it
  const turns = []
  for (let i = first + 1; i < msgs.length;) {
    let j = i + 1
    if (msgs[i].role === 'assistant') while (j < msgs.length && msgs[j].role === 'tool') j++
    turns.push([i, j])
    i = j
  }
  const drop = new Set()
  for (let t = 0; t < turns.length - 2 && total > SLIM_HISTORY_CHARS; t++) {
    const [a, b] = turns[t]
    for (let k = a; k < b; k++) { drop.add(k); total -= msgSize(msgs[k]) }
  }
  if (!drop.size) return msgs
  const out = msgs.filter((m, k) => !drop.has(k))
  out.splice(first + 1, 0, { role: 'user', content: '(Earlier steps of this task were left out to fit the context.)' })
  return out
}

/** Qwen's template takes one system message, first: several (Codex: instructions + developer items) are joined. */
function mergeSystem (messages) {
  const sys = messages.filter((m) => m.role === 'system').map((m) => m.content).filter(Boolean)
  const rest = messages.filter((m) => m.role !== 'system')
  return sys.length ? [{ role: 'system', content: sys.join('\n\n') }, ...rest] : rest
}

module.exports = { wantsCoder, chatCompletions, responses, messagesApi, countTokens, responsesToChat, anthropicToChat, mergeSystem, slim }
