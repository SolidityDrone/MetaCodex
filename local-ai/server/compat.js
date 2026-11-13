'use strict'
// Agent harnesses on the local coding model (docs/local-coder.md): the API shapes they speak, translated to the
// service's chat engine (engines.chat with engine 'coder'):
//   OpenCode      OpenAI chat completions      POST /v1/chat/completions  (main.js; model "local-coder")
//   Codex         OpenAI Responses API          POST /v1/responses        (wire_api = "responses"; chat is gone)
//   Claude Code   Anthropic Messages API        POST /v1/messages         (ANTHROPIC_BASE_URL)
// Each takes "stream": true (server-sent events in that API's own event format). Tool calls go both ways.

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
  const opts = { engine: 'coder', max_tokens: j.max_output_tokens, temperature: j.temperature }
  if (!j.stream) {
    const r = await engines.chat(messages, tools, opts)
    log(`coder responses ${r.ms} ms, prompt ${(r.stats || {}).promptTokens || '?'} tok: ${r.tool_calls.length ? r.tool_calls.map((c) => c.name).join(' ') : JSON.stringify(r.content).slice(0, 80)}`)
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
  log(`coder responses stream ${r.ms} ms, prompt ${(r.stats || {}).promptTokens || '?'} tok${r.cancelled ? ' cancelled' : ''}: ${final.output.map((o) => o.type === 'function_call' ? o.name : 'text').join(' ')}`)
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
  const opts = { engine: 'coder', max_tokens: j.max_tokens, temperature: j.temperature }
  if (!j.stream) {
    const r = await engines.chat(messages, tools, opts)
    const st = r.stats || {}
    const content = anthropicContent(r)
    log(`coder messages ${r.ms} ms, prompt ${(r.stats || {}).promptTokens || '?'} tok: ${content.map((b) => b.type === 'tool_use' ? b.name : 'text').join(' ')}`)
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
  log(`coder messages stream ${r.ms} ms, prompt ${(r.stats || {}).promptTokens || '?'} tok${r.cancelled ? ' cancelled' : ''}: ${uses.length ? uses.map((b) => b.name).join(' ') : 'text'}`)
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
// model gets lost in it anyway. For the local coder the system prompt becomes a short one plus the harness's own
// environment facts (working folder, platform, date), and only the core file / shell tools go, their descriptions cut.
// The 0.5B coder (2026-11-13) picked "edit" for a file that did not exist and looped on the error: only three tools
// now, shell, read and write (a whole file), so every task has one obvious way.
const SLIM_TOOLS = /^(read|write|bash|read_file|write_file|shell|exec_command)$/i
const SLIM_SYSTEM = [
  'You are a coding agent running on a small local model on a VR headset. Work in small steps.',
  'Tools: bash runs a shell command; read reads a file; write writes a whole file (new or changed). To create or change a file, use write with its full content.',
  'Call one tool at a time. Use paths inside the working directory. When the task is done, say in one or two sentences what you did.'
].join('\n')

function slimSystem (text) {
  const facts = []
  for (const m of String(text || '').matchAll(/<env>[\s\S]*?<\/env>/g)) facts.push(m[0])
  if (!facts.length) {
    for (const line of String(text || '').split('\n')) {
      if (/working directory|current directory|\bcwd\b|platform|operating system|today'?s date|is a git repo/i.test(line) && line.length < 300) facts.push(line.trim())
      if (facts.length >= 8) break
    }
  }
  return SLIM_SYSTEM + (facts.length ? '\n\n' + facts.join('\n') : '')
}

function cut (s, n) { s = String(s || ''); return s.length > n ? s.slice(0, n).replace(/\s+\S*$/, '') + '…' : s }

/** The slim profile for one request: a short system prompt with the environment facts, the core tools only. */
function slim ({ messages, tools }, on = true) {
  if (!on) return { messages, tools }
  // harness reminders inside user turns (Claude Code: <system-reminder> blocks with skill lists, todos) go too, except
  // the project's own instructions (CLAUDE.md / AGENTS.md: e.g. how a native app is built and installed here)
  const msgs = messages.map((m) => m.role === 'system' ? { role: 'system', content: slimSystem(m.content) }
    : m.role === 'user' && typeof m.content === 'string' ? Object.assign({}, m, { content: m.content.replace(/<system-reminder>[\s\S]*?<\/system-reminder>\s*/g, (r) => /claudeMd|Contents of [^\n]*(CLAUDE|AGENTS)\.md/.test(r) ? r : '').trim() || m.content }) : m)
  let kept = (tools || []).filter((t) => SLIM_TOOLS.test(((t.function || t).name) || ''))
  if (!kept.length) kept = (tools || []).slice(0, 6)
  kept = kept.map((t) => {
    const f = t.function || t
    return { type: 'function', function: { name: f.name, description: cut((f.description || '').split('\n')[0], 200), parameters: f.parameters } }
  })
  return { messages: msgs, tools: kept }
}

/** Qwen's template takes one system message, first: several (Codex: instructions + developer items) are joined. */
function mergeSystem (messages) {
  const sys = messages.filter((m) => m.role === 'system').map((m) => m.content).filter(Boolean)
  const rest = messages.filter((m) => m.role !== 'system')
  return sys.length ? [{ role: 'system', content: sys.join('\n\n') }, ...rest] : rest
}

module.exports = { wantsCoder, responses, messagesApi, countTokens, responsesToChat, anthropicToChat, mergeSystem, slim }
