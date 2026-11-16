'use strict'
// Tool calls in the text of the service's small models: the parser (parseAssistant), the streaming filters
// (thinkStripper, callGuard) and the loose JSON reader they share. Plain JavaScript with no Bare modules: engines.js
// uses it on the headset, tests/local-coder/ runs it under Node. docs/local-coder.md, "Tool calls in the model's text".
//
// Qwen2.5-Coder's chat template (in its GGUF) lists the tools in <tools> and asks for
// <tool_call>{"name": ..., "arguments": {...}}</tool_call>. The 0.5B coder often writes the call without the tags,
// as a bare JSON object or in a ```json block, with the tool's name in another case or another word ("bash" for
// Claude Code's "Bash" or Codex's "exec_command"), and sometimes with broken JSON (a stray quote, a missing brace).
// Owner 2026-11-16: "it only answers with JSON". For the local coder (opts.coder) all those forms become real calls,
// and the text that could hold a call is never streamed before the parse (callGuard).

// ---------------------------------------------------------------- the request's tools

// What a small model calls the three tools of the slim profile (compat.js), mapped to the request's own tool
const FAMILIES = {
  shell: /^(bash|shell|sh|terminal|exec|execute|run|run_command|run_shell_command|execute_command|exec_command|shell_command|command|cmd)$/i,
  read: /^(read|read_file|readfile|cat|view|view_file|open_file|file_read)$/i,
  write: /^(write|write_file|writefile|create_file|save_file|file_write|create)$/i
}
// argument names: the request's schema has one of a group, the model wrote another
const ARG_SYNONYMS = [
  ['command', 'cmd', 'commands', 'script', 'shell_command', 'bash_command'],
  ['file_path', 'filePath', 'path', 'file', 'filename', 'file_name', 'filepath', 'target_file'],
  ['content', 'contents', 'text', 'data', 'file_content', 'file_text', 'body', 'code'],
  ['workdir', 'cwd', 'dir', 'directory', 'working_directory']
]
const NAME_KEYS = ['name', 'function', 'tool', 'tool_name', 'action']
const ARG_KEYS = ['arguments', 'parameters', 'args', 'input', 'tool_input', 'action_input', 'params']

function familyOf (name) {
  for (const f of Object.keys(FAMILIES)) if (FAMILIES[f].test(String(name || ''))) return f
  return null
}

/** Tools as names, OpenAI {type, function} or QVAC/Anthropic {name, parameters | input_schema} -> [{ name, props, required }]. */
function toolList (tools) {
  const out = []
  for (const t of tools || []) {
    if (typeof t === 'string') { out.push({ name: t, props: null, required: [] }); continue }
    const f = t && t.function ? t.function : t
    if (!f || typeof f.name !== 'string') continue
    const p = f.parameters || f.input_schema || null
    out.push({ name: f.name, props: p && p.properties ? p.properties : null, required: p && Array.isArray(p.required) ? p.required : [], strict: !!p && p.additionalProperties === false })
  }
  return out
}

/** The request's tool a call names: the same name; for the coder also in another case, or another word for it. */
function resolveTool (name, list, coder) {
  if (typeof name !== 'string' || !name) return null
  const exact = list.find((t) => t.name === name)
  if (exact || !coder) return exact || null
  const lower = name.toLowerCase()
  const ci = list.find((t) => t.name.toLowerCase() === lower)
  if (ci) return ci
  const fam = familyOf(name)
  return fam ? list.find((t) => familyOf(t.name) === fam) || null : null
}

/** The model's arguments fitted to the tool's schema: synonyms renamed, a command string where an array is wanted. */
function fitArguments (args, tool) {
  if (typeof args === 'string') {
    const p = parseLoose(args)
    if (p.value && typeof p.value === 'object' && !Array.isArray(p.value)) args = p.value
    else {
      const req = tool && tool.props ? tool.required.filter((k) => tool.props[k] && tool.props[k].type === 'string') : []
      args = req.length === 1 ? { [req[0]]: args } : {}
    }
  }
  if (!args || typeof args !== 'object' || Array.isArray(args)) args = {}
  if (!tool || !tool.props) return args
  const out = Object.assign({}, args)
  for (const p of Object.keys(tool.props)) {
    if (p in out) continue
    const group = ARG_SYNONYMS.find((g) => g.includes(p))
    const k = group && group.find((s) => s in out && !(s in tool.props))
    if (k) { out[p] = out[k]; delete out[k] }
  }
  for (const [p, s] of Object.entries(tool.props)) {
    if (!s || !(p in out)) continue
    if (s.type === 'array' && typeof out[p] === 'string' && ARG_SYNONYMS[0].includes(p)) out[p] = ['bash', '-lc', out[p]] // an older shell tool: argv
    else if (s.type === 'string' && Array.isArray(out[p])) {
      const a = out[p].map(String)
      out[p] = /^(ba)?sh$/.test(a[0]) && /^-l?c$/.test(a[1] || '') ? a.slice(2).join(' ') : a.join(' ')
    }
  }
  // a required description the model left out (OpenCode's bash wants one): the command itself
  for (const p of tool.required) {
    if (!(p in out) && /^(description|justification)$/.test(p)) {
      const c = out.command != null ? out.command : out.cmd
      if (c != null) out[p] = String(Array.isArray(c) ? c.join(' ') : c).slice(0, 80)
    }
  }
  // a strict schema (Claude Code's: additionalProperties false) rejects a call with arguments it does not declare
  if (tool.strict) for (const k of Object.keys(out)) if (!(k in tool.props)) delete out[k]
  return out
}

const shQuote = (s) => "'" + String(s).replace(/'/g, "'\\''") + "'"

/**
 * A read or write call when the request has a shell tool but no such tool (Codex: exec_command only, in the slim
 * profile): the same work as a shell command -> { tool, args } or null.
 */
function viaShell (name, args, list) {
  const fam = familyOf(name)
  const shell = list.find((t) => familyOf(t.name) === 'shell')
  if (!shell || (fam !== 'read' && fam !== 'write')) return null
  if (typeof args === 'string') { const p = parseLoose(args); args = p.value && typeof p.value === 'object' ? p.value : {} }
  args = args || {}
  const pick = (g) => { const k = ARG_SYNONYMS[g].find((x) => typeof args[x] === 'string'); return k ? args[k] : null }
  const file = pick(1)
  if (!file) return null
  const content = pick(2)
  if (fam === 'write' && content == null) return null
  const command = fam === 'read' ? 'cat -- ' + shQuote(file)
    : 'mkdir -p -- "$(dirname -- ' + shQuote(file) + ')" && printf %s ' + shQuote(content) + ' > ' + shQuote(file)
  return { tool: shell, args: { command } }
}

/** A parsed JSON value that has the shape of a call -> { name, arguments, tool } or null. */
function callFrom (j, list, coder, tagged) {
  if (!j || typeof j !== 'object' || Array.isArray(j)) return null
  const base = j.function && typeof j.function === 'object' && !Array.isArray(j.function) ? j.function : j // {type, function: {...}}
  let name = null
  for (const k of coder ? NAME_KEYS : ['name']) if (typeof base[k] === 'string' && base[k]) { name = base[k]; break }
  if (!name) return null
  const argKey = (coder ? ARG_KEYS : ['arguments', 'parameters']).find((k) => k in base)
  const tool = resolveTool(name, list, coder)
  if (coder && !tool && argKey) {
    const v = viaShell(name, base[argKey], list)
    if (v) return { name: v.tool.name, arguments: fitArguments(v.args, v.tool), tool: v.tool }
  }
  let args
  if (argKey) args = base[argKey]
  else {
    // {"name": "write", "path": "a.txt", "content": "hi"}: the arguments written next to the name
    const rest = {}
    if (coder) for (const k of Object.keys(base)) if (!NAME_KEYS.includes(k) && k !== 'type' && k !== 'id') rest[k] = base[k]
    const fits = coder && tool && tool.props && Object.keys(fitArguments(rest, tool)).some((k) => k in tool.props)
    if (!fits && !tagged) return null
    args = rest
  }
  return { name: tool ? tool.name : name, arguments: coder ? fitArguments(args, tool) : (typeof args === 'string' ? safeJson(args) : (args || {})), tool }
}

// ---------------------------------------------------------------- loose JSON

/**
 * JSON.parse, then the small repairs a 0.5B model needs: raw line breaks inside strings, a stray quote after a
 * number ("max_output_tokens": 10000"}), trailing commas, missing closing brackets, extra closing ones, single
 * quotes when the text has no double quote. -> { value, cut } (cut: the text ended inside a string, so a value is
 * missing its end: a call cut off by the output limit) or { value: null }.
 */
function parseLoose (s) {
  s = String(s == null ? '' : s).trim()
  if (!s) return { value: null, cut: false }
  try { return { value: JSON.parse(s), cut: false } } catch {}
  if (!s.includes('"') && s.includes("'")) s = s.replace(/'/g, '"')
  let out = ''
  let inStr = false
  let esc = false
  const stack = []
  for (let i = 0; i < s.length; i++) {
    const ch = s[i]
    if (inStr) {
      if (esc) { esc = false; out += ch; continue }
      if (ch === '\\') { esc = true; out += ch; continue }
      if (ch === '"') { inStr = false; out += ch; continue }
      out += ch === '\n' ? '\\n' : ch === '\r' ? '\\r' : ch === '\t' ? '\\t' : ch
      continue
    }
    if (ch === '"') {
      if (/(\d|true|false|null)$/.test(out) && /^\s*[,}\]]/.test(s.slice(i + 1))) continue // stray quote after a value
      inStr = true
      out += ch
      continue
    }
    if (ch === '{' || ch === '[') { stack.push(ch === '{' ? '}' : ']'); out += ch; continue }
    if (ch === '}' || ch === ']') {
      if (!stack.length) break // an extra closing bracket: the value ended before it
      out = out.replace(/,\s*$/, '')
      stack.pop()
      out += ch
      if (!stack.length) break // the first complete value
      continue
    }
    out += ch
  }
  const cut = inStr
  if (inStr) out += '"'
  out = out.replace(/,\s*$/, '')
  while (stack.length) out += stack.pop()
  try { return { value: JSON.parse(out), cut } } catch { return { value: null, cut } }
}

function safeJson (s) { try { return JSON.parse(s) } catch { return {} } }

/** The top-level {...} spans of a text (strings inside them respected); `closed` false for one still open at the end. */
function jsonSpans (text) {
  const out = []
  let depth = 0
  let start = -1
  let inStr = false
  let esc = false
  for (let i = 0; i < text.length; i++) {
    const ch = text[i]
    if (inStr) { if (esc) esc = false; else if (ch === '\\') esc = true; else if (ch === '"') inStr = false; continue }
    if (ch === '"') { if (depth > 0) inStr = true; continue }
    if (ch === '{') { if (depth === 0) start = i; depth++ } else if (ch === '}' && depth > 0) {
      depth--
      if (depth === 0 && start >= 0) { out.push({ start, end: i + 1, body: text.slice(start, i + 1), closed: true }); start = -1 }
    }
  }
  if (depth > 0 && start >= 0) out.push({ start, end: text.length, body: text.slice(start), closed: false })
  return out
}

/** The top-level {...} texts (the older helper, kept for callers). */
function jsonObjects (text) { return jsonSpans(text).filter((s) => s.closed).map((s) => s.body) }

// ---------------------------------------------------------------- the parser

// a bare or fenced call next to more prose than this is an example inside an answer, not a call
const CALL_LEAD_CHARS = 400
const BROKEN_CALL_NOTE = '(The local model wrote a tool call that could not be read: its JSON is broken or cut off. Ask again, or ask for a smaller step.)'

/**
 * Qwen-style output -> { content, tool_calls: [{ name, arguments }], raw }: an optional <think> block, then text
 * and/or calls. Calls in <tool_call> tags always count. Bare JSON objects with a name and arguments count when the
 * name is one of the request's tools (any name when the tools are not known, except for the coder).
 * opts.coder (the local coder): the name may differ in case or be another word for the tool (bash -> Bash or
 * exec_command), the arguments are fitted to the tool's schema, a ```json block counts like a bare object, broken
 * JSON is repaired or, when it cannot be, replaced by a short note (never shown as JSON), a call is an example (left
 * as text) when it sits in a longer answer, a made-up tool turn after a call is dropped, and a lone `echo` call
 * becomes its text (it only prints: the answer, without a slow extra turn).
 */
function parseAssistant (raw, tools, opts = {}) {
  const coder = !!opts.coder
  const list = toolList(tools)
  let text = String(raw || '').replace(/<think>[\s\S]*?<\/think>/g, '')
  if (coder) { const k = text.search(/<tool_response>|<\|im_start\|>|<\|im_end\|>/); if (k >= 0) text = text.slice(0, k) }
  const calls = []
  let broken = false
  text = text.replace(/<tool_call>\s*([\s\S]*?)\s*(<\/tool_call>|$)/g, (_, body) => {
    const p = parseLoose(unfence(body))
    const items = Array.isArray(p.value) ? p.value : [p.value]
    let ok = false
    if (p.value && !p.cut) for (const v of items) { const c = callFrom(v, list, coder, true); if (c) { calls.push(c); ok = true } }
    if (!ok) broken = true
    return ''
  })
  text = text.replace(/<\/?tool_call>/g, '').trim()
  if (!calls.length && text.includes('{') && (!coder || list.length)) {
    const found = []
    const unknown = []
    let brokenSpan = null
    for (const s of jsonSpans(text)) {
      const p = parseLoose(s.body)
      const c = p.value ? callFrom(p.value, list, coder, false) : null
      const named = c ? c.tool : coder ? leadTool(s.body, list) : null
      if (coder && named && (!c || p.cut)) { if (!brokenSpan) brokenSpan = s; continue }
      if (!c || p.cut) continue
      if (list.length && !c.tool) { unknown.push({ s, call: c }); continue } // names no tool of the request: data or an example
      found.push({ s, call: c })
    }
    // the coder's whole answer is one call to a tool the request lacks (the 0.5B model wrote "edit" calls out of
    // habit): a call all the same, so the harness answers that the tool does not exist and the model can correct it
    if (coder && !found.length && !brokenSpan && unknown.length === 1 && !cleanup(removeSpans(text, [unknown[0].s]))) found.push(unknown[0])
    const kept = found.length ? removeSpans(text, found.map((f) => f.s)) : text
    if (found.length && (!coder || cleanup(kept).length <= CALL_LEAD_CHARS)) {
      for (const f of found) calls.push({ name: f.call.name, arguments: f.call.arguments })
      // and the call-shaped objects naming no tool of the request (made up next to a real one)
      text = removeSpans(kept, jsonSpans(kept).filter((s) => { const v = parseLoose(s.body).value; return v && typeof v === 'object' && typeof v.name === 'string' && ('arguments' in v || 'parameters' in v) }))
      text = cleanup(text)
    } else if (brokenSpan && cleanup(removeSpans(text, [brokenSpan])).length <= CALL_LEAD_CHARS) {
      broken = true
      text = cleanup(removeSpans(text, [brokenSpan]))
    }
  }
  const res = { content: text, tool_calls: calls.filter((c) => c.name).map((c) => ({ name: c.name, arguments: c.arguments || {} })), raw }
  if (coder && res.tool_calls.length === 1) {
    const said = echoText(res.tool_calls[0], list)
    if (said != null) { res.content = (res.content ? res.content + '\n' : '') + said; res.tool_calls = []; res.echoed = true }
  }
  if (coder && broken && !res.tool_calls.length) { res.content = (res.content ? res.content + '\n\n' : '') + BROKEN_CALL_NOTE; res.broken = true }
  return res
}

/** The request's tool a broken call-shaped object names ({"name": "bash", ... that does not parse), for the coder. */
function leadTool (body, list) {
  const m = /^\{\s*["']?(?:name|function|tool|tool_name|action)["']?\s*:\s*["']([\w.:-]+)["']/.exec(body)
  return m ? resolveTool(m[1], list, true) : null
}

function removeSpans (text, spans) {
  const sorted = spans.slice().sort((a, b) => b.start - a.start)
  for (const s of sorted) text = text.slice(0, s.start) + text.slice(s.end)
  return text
}

/** What is left around removed calls: empty code fences, lone brackets and commas, extra blank lines. */
function cleanup (text) {
  return text.replace(/```[\w-]*\s*```/g, '').replace(/^[ \t]*[[\],;]+[ \t]*$/gm, '').replace(/\n{3,}/g, '\n\n').replace(/^[\s,;]+|[\s,;]+$/g, '')
}

function unfence (body) {
  return String(body || '').replace(/^\s*```[\w-]*\s*/, '').replace(/\s*```\s*$/, '')
}

/** A shell call that only prints text (echo with no redirection, pipe, variable or second command) -> that text. */
function echoText (call, list) {
  const tool = resolveTool(call.name, list, true)
  if (familyOf(call.name) !== 'shell' && !(tool && familyOf(tool.name) === 'shell')) return null
  const a = call.arguments || {}
  let cmd = a.command != null ? a.command : a.cmd
  if (Array.isArray(cmd)) cmd = /^(ba)?sh$/.test(cmd[0]) && /^-l?c$/.test(cmd[1] || '') ? cmd.slice(2).join(' ') : cmd.join(' ')
  if (typeof cmd !== 'string') return null
  const m = /^\s*echo(?:\s+-[neE]+)?\s+([\s\S]*?)\s*$/.exec(cmd)
  if (!m || /[<>|;&$`\\\n(){}*?~#]/.test(m[1])) return null
  let t = m[1]
  const q = /^"([^"]*)"$|^'([^']*)'$/.exec(t)
  if (q) t = q[1] != null ? q[1] : q[2]
  else if (/["']/.test(t)) return null
  return t.trim() ? t : null
}

// ---------------------------------------------------------------- streaming

/** Forwards streamed text without a leading <think>...</think> block (Qwen3 may write an empty one even with
 * /no_think). Text is held only while it could still be the start of "<think>" or "<tool_call>"; a <tool_call> block
 * is not streamed (the final chunk carries the parsed call). */
function thinkStripper (emit) {
  let buf = ''
  let state = 'start' // start | think | text | tool
  const feed = (tok) => {
    if (state === 'tool') return
    if (state === 'text') {
      const i = tok.indexOf('<tool_call>')
      if (i < 0) return emit(tok)
      if (i > 0) emit(tok.slice(0, i))
      state = 'tool'
      return
    }
    buf += tok
    if (state === 'think') {
      const j = buf.indexOf('</think>')
      if (j < 0) return
      const rest = buf.slice(j + 8)
      buf = ''
      state = 'start'
      if (rest) feed(rest)
      return
    }
    const t = buf.trimStart() // state start
    if (!t) return
    if (t.startsWith('<think>')) { state = 'think'; buf = t.slice(7); if (buf) { const b = buf; buf = ''; feed(b) } return }
    if (t.startsWith('<tool_call>')) { state = 'tool'; return }
    if ('<think>'.startsWith(t) || '<tool_call>'.startsWith(t)) return // could still become a tag
    state = 'text'
    buf = ''
    feed(t)
  }
  return feed
}

const GUARD_TRIGGERS = ['{', '```', '<tool_call>']

/**
 * The local coder's stream (after thinkStripper): text goes out as it comes until a point where a tool call could
 * start (a `{`, a code fence, a <tool_call> tag); from there on it is held to the end of the answer. Then
 * rest(parsedContent) is what the client has not had yet of the parsed answer: the held text when it was no call,
 * nothing when it was one (a bare JSON call never reaches the user as text).
 */
function callGuard (emit) {
  let sent = ''
  let pend = ''
  let holding = false
  const out = (s) => { if (s) { sent += s; emit(s) } }
  const feed = (tok) => {
    if (holding || !tok) return
    pend += tok
    let cut = -1
    for (const t of GUARD_TRIGGERS) { const i = pend.indexOf(t); if (i >= 0 && (cut < 0 || i < cut)) cut = i }
    if (cut >= 0) { out(pend.slice(0, cut).trimEnd()); pend = ''; holding = true; return }
    let keep = 0 // a tail that could still become a trigger ("`", "<tool_")
    for (const t of GUARD_TRIGGERS) for (let n = Math.min(t.length - 1, pend.length); n > keep; n--) if (pend.endsWith(t.slice(0, n))) { keep = n; break }
    // and trailing white space, which the parsed answer drops when a call or the end follows it
    const part = pend.slice(0, pend.length - keep)
    const go = part.trimEnd()
    out(go)
    pend = pend.slice(go.length)
  }
  const rest = (content) => {
    content = String(content || '')
    if (content.startsWith(sent)) return content.slice(sent.length)
    const base = sent.trimEnd()
    if (content.startsWith(base)) return content.slice(base.length).trimStart()
    return ''
  }
  return { feed, rest, sent: () => sent, holding: () => holding }
}

module.exports = { parseAssistant, viaShell, thinkStripper, callGuard, parseLoose, jsonSpans, jsonObjects, safeJson, toolList, resolveTool, fitArguments, familyOf, BROKEN_CALL_NOTE }
