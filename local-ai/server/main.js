'use strict'
// MetaCodex local AI service: QVAC speech-to-text, a small LLM with tool calling and text-to-speech on the
// Bare runtime, in its own process on Horizon OS (the app starts it pinned to cores 0-2 at low priority).
// HTTP/1.1 on 127.0.0.1 with a bearer token; OpenAI-style endpoints where a standard exists. docs/local-ai.md.
//
//   bare main.js --data <dir> [--port 47810] [--host 127.0.0.1] [--token-file <file>] [--threads 2]
const http = require('bare-http1')
const fs = require('bare-fs')
const path = require('bare-path')
const os = require('bare-os')
const { Models, CATALOGUE } = require('./models')
const { Engines } = require('./engines')
const compat = require('./compat')
const audio = require('./audio')

const VERSION = '0.1.0'
const MAX_BODY = 32 * 1024 * 1024
const args = parseArgs(Bare.argv.slice(2))
const DATA = path.resolve(args.data || 'qvac-data')
const PORT = Number(args.port || 47810)
const HOST = args.host || '127.0.0.1'
const TOKEN = args['token-file'] ? fs.readFileSync(args['token-file'], 'utf8').trim() : (args.token || '')
// the agent harnesses' key for the local coder (docs/local-coder.md): stable across starts (the app makes it once and
// shares it as /mnt/quest/local-coder.key), so their configs keep working; it opens only the coder's routes
const CODER_KEY = (() => { try { return fs.readFileSync(path.join(args.data || '.', 'coder.key'), 'utf8').trim() } catch { return '' } })()
const CODER_ROUTES = new Set(['POST /v1/responses', 'POST /v1/messages', 'POST /v1/messages/count_tokens', 'POST /v1/chat/completions', 'GET /v1/models'])
const T0 = Date.now()

fs.mkdirSync(DATA, { recursive: true })
const LOG = path.join(DATA, 'service.log')

function log (msg) {
  const line = new Date().toISOString() + ' ' + msg
  console.log('[mcx-ai] ' + line)
  try {
    if (fs.existsSync(LOG) && fs.statSync(LOG).size > 1048576) fs.renameSync(LOG, LOG + '.1')
    fs.appendFileSync(LOG, line + '\n')
  } catch {}
}

const CONFIG = path.join(DATA, 'config.json')
const DEFAULTS = {
  threads: 2, // measured on the Quest 3S: 72 fps held with 2 inference threads; 4 adds stale frames
  language: 'en', // speech-to-text language ('auto' detects; Whisper base is multilingual)
  prompt: '', // speech-to-text vocabulary (the app sends panel, command and workspace names)
  ctx: 2048, // LLM context: system prompt + command tools + the request
  voice: 'F1',
  ttsLanguage: 'en',
  // Supertonic diffusion steps, 1-10 (model default 5). 3 is faster; it garbles a few words a little (measured), and the
  // app's voice effect hides that (owner, 2026-11-13). The app sends its own value (VOICE > speech quality)
  ttsSteps: 3,
  minAudioCtx: 512, // smallest whisper encoder window (frames, 50 per second): 256 repeats and mishears (measured)
  idleMinutes: { asr: 30, llm: 30, tts: 10, embed: 60 }, // keep models loaded while voice is in use: a reload costs 1-4 s
  // threads per engine (null = `threads`). Measured 2026-11-12 (docs/voice.md "Streaming answers"): with the LLM
  // decoding on 1 thread (prompt evaluation on 2) and the voice on 2, both keep their solo speed while they overlap
  // on the 3 cores; 2 + 2 made the LLM 29 % and the voice 62 % slower
  llmThreads: null,
  llmBatchThreads: null,
  ttsThreads: null,
  asrThreads: null,
  embedThreads: null,
  prefillYieldsToAsr: true, // a speculative prefill is cancelled when speech-to-text needs the cores
  // speech has priority (engines.speechActive): while an answer is spoken, the warm-up, a speculative prefill and bulk
  // sentence vectors wait (a running warm-up or prefill is cancelled and done again later); real requests never wait
  backgroundYieldsToSpeech: true,
  speechHoldMs: 3000, // speech counts as active this long after the last sentence (the app asks for the next one)
  maxYieldMs: 60000, // a background request waits for speech at most this long in total, then runs anyway
  llmPromptBatch: 128, // the voice LLM evaluates prompts 128 tokens at a time: a cancel stops it in ~0.1 s (engines.promptBatch)
  coderThreads: 3, // the local coding model (compat.js) may use the 3 AI cores: it runs when voice is idle
  coderCtx: 16384,
  coderSlim: true, // the slim agent profile for the local coder (compat.slim)
  coderHoldMs: 15000 // the interface AI waits this long after the coder's last request (engines.coderHolds)
}
const cfg = Object.assign({}, DEFAULTS, readJson(CONFIG) || {})
if (args.threads) cfg.threads = Number(args.threads)

const models = new Models({ dir: path.join(DATA, 'models'), registryDir: path.join(DATA, 'registry'), log })
const engines = new Engines({ models, cfg, log, cacheDir: path.join(DATA, 'cache') })

try { os.setPriority(10) } catch {} // threads created from here on inherit it (the launcher also uses nice)

function status () {
  let mem = {}
  try {
    const m = os.memoryUsage()
    mem = { rssMB: Math.round(m.rss / 1048576), availableMB: Math.round(os.availableMemory() / 1048576) }
  } catch {}
  return Object.assign({ ok: true, service: 'metacodex-ai', version: VERSION, pid: Bare.pid, uptimeS: Math.round((Date.now() - T0) / 1000), threads: cfg.threads, language: cfg.language, placement: placement(), models: models.status() }, engines.status(), mem)
}

/** Where the system runs us: the CPU set and the cores this process may use (Horizon OS: 0-2 wanted). */
function placement () {
  const read = (f) => { try { return fs.readFileSync(f, 'utf8') } catch { return '' } }
  const cpus = (/Cpus_allowed_list:\s*(\S+)/.exec(read('/proc/self/status')) || [])[1] || ''
  return { cpuset: read('/proc/self/cpuset').trim(), cpus }
}

async function handle (req, res, body) {
  const u = new URL(req.url, 'http://127.0.0.1')
  const route = req.method + ' ' + u.pathname
  if (route === 'GET /health') return send(res, 200, { ok: true, service: 'metacodex-ai', version: VERSION })
  if (TOKEN && !authorized(req) && !(CODER_KEY && CODER_ROUTES.has(route) && authorized(req, CODER_KEY))) return send(res, 401, { error: 'missing or wrong bearer token' })
  switch (route) {
    case 'GET /status':
      return send(res, 200, status())
    case 'GET /v1/models':
      return send(res, 200, { object: 'list', data: Object.keys(CATALOGUE).map((k) => ({ id: CATALOGUE[k].id, object: 'model', kind: k, owned_by: 'qvac', installed: models.installed(k) })) })
    case 'POST /v1/models/install': {
      models.install(kindsOf(parseJson(body)))
      return send(res, 202, models.status())
    }
    case 'POST /v1/models/adopt': { // a model file pushed over adb: verify and use it
      const out = {}
      for (const k of kindsOf(parseJson(body))) out[k] = await models.adopt(k)
      return send(res, 200, out)
    }
    case 'POST /v1/models/remove': {
      const kinds = kindsOf(parseJson(body))
      await engines.unload(kinds)
      models.remove(kinds)
      return send(res, 200, models.status())
    }
    case 'POST /v1/audio/transcriptions':
      return transcriptions(req, res, body, u)
    case 'POST /v1/audio/speech':
      return speech(res, parseJson(body))
    case 'POST /v1/chat/completions':
      return completions(req, res, parseJson(body))
    case 'POST /v1/responses': // Codex (wire_api = "responses") on the local coding model
      return compat.responses(req, res, parseJson(body), { engines, send, sse, log, cfg })
    case 'POST /v1/messages': // Claude Code (ANTHROPIC_BASE_URL) on the local coding model
      return compat.messagesApi(req, res, parseJson(body), { engines, send, sse, log, cfg })
    case 'POST /v1/messages/count_tokens':
      return send(res, 200, compat.countTokens(parseJson(body)))
    case 'POST /v1/prefill': { // the session prompt without its question, evaluated while the user still speaks
      const j = parseJson(body)
      if (!Array.isArray(j.messages) || !j.messages.length) throw httpError(400, 'messages is required')
      engines.cancelSpeculative() // only the newest one is worth keeping
      const p = engines.prefill(j.messages)
      p.then((r) => log(`prefill ${r.cancelled ? 'cancelled' : r.ms + ' ms'}${yieldNote(r)}`)).catch((e) => log('prefill: ' + (e.message || e)))
      return send(res, 202, { ok: true }) // fire and forget: the next chat with the same prefix starts from it
    }
    case 'POST /v1/embeddings': { // OpenAI-style: { input: string | string[] } -> { data: [{ index, embedding }] }
      const j = parseJson(body)
      const input = Array.isArray(j.input) ? j.input.map(String) : [String(j.input == null ? '' : j.input)]
      if (!input.length || input.length > 4096) return send(res, 400, { error: 'input: 1-4096 strings' })
      const r = await engines.embed(input)
      return send(res, 200, { object: 'list', model: CATALOGUE.embed.id, ms: r.ms, data: r.vectors.map((embedding, index) => ({ object: 'embedding', index, embedding })) })
    }
    case 'POST /v1/warmup': { // prefills system prompt + tools once (kept on disk), so commands decode only the user's words
      const j = parseJson(body)
      const r = await engines.warm(j.messages || [], Array.isArray(j.tools) ? j.tools : [])
      log(`warmup ${r.cached ? 'already cached' : r.ms + ' ms'}${yieldNote(r)}`)
      return send(res, 200, Object.assign({ ok: true }, r))
    }
    case 'POST /unload': {
      await engines.unload(kindsOf(parseJson(body)))
      return send(res, 200, engines.status())
    }
    case 'POST /pause':
      engines.pause()
      return send(res, 200, { paused: true })
    case 'POST /resume':
      engines.resume()
      return send(res, 200, { paused: false })
    case 'GET /config':
      return send(res, 200, cfg)
    case 'POST /config': {
      const j = parseJson(body)
      for (const k of Object.keys(DEFAULTS)) if (j[k] !== undefined) cfg[k] = j[k]
      cfg.threads = Math.max(1, Math.min(6, Number(cfg.threads) || 2))
      fs.writeFileSync(CONFIG, JSON.stringify(cfg, null, 2))
      return send(res, 200, cfg) // engines reload on their next use when a load setting changed
    }
    case 'GET /log': {
      let text = ''
      try { text = fs.readFileSync(LOG, 'utf8') } catch {}
      const n = Number(u.searchParams.get('lines') || 100)
      return sendBytes(res, 200, 'text/plain; charset=utf-8', Buffer.from(text.split('\n').slice(-n - 1).join('\n')))
    }
    case 'POST /shutdown':
      send(res, 200, { bye: true })
      setTimeout(shutdown, 100)
      return
  }
  send(res, 404, { error: 'no route ' + route })
}

async function transcriptions (req, res, body, u) {
  const type = String(req.headers['content-type'] || '').toLowerCase()
  let fields = {}
  let pcm
  let rate = Number(u.searchParams.get('rate') || 16000)
  if (type.startsWith('multipart/form-data')) {
    const mp = audio.parseMultipart(body, req.headers['content-type'])
    fields = mp.fields
    const file = mp.files.file || Object.values(mp.files)[0]
    if (!file) throw httpError(400, 'multipart upload without a file')
    const w = audio.parseWav(file)
    pcm = w.pcm
    rate = w.rate
  } else if (type.startsWith('audio/pcm') || type.startsWith('audio/l16')) {
    pcm = audio.fromPcm16(body)
    const m = /rate=(\d+)/.exec(type)
    if (m) rate = Number(m[1])
  } else {
    const w = audio.parseWav(body)
    pcm = w.pcm
    rate = w.rate
  }
  const language = fields.language || u.searchParams.get('language') || null
  const r = await engines.transcribe(audio.resample(pcm, rate, 16000), { language })
  log(`asr ${r.audioMs} ms audio -> ${r.ms} ms: ${JSON.stringify(r.text).slice(0, 120)}`)
  if ((fields.response_format || u.searchParams.get('response_format')) === 'text') return sendBytes(res, 200, 'text/plain; charset=utf-8', Buffer.from(r.text))
  send(res, 200, r)
}

// short replies repeat ("Opened GIT", "Closed 2 panels"): keep their audio, so the second time is instant
const ttsCache = new Map()
const TTS_CACHE_MAX = 64

async function speech (res, j) {
  const text = String(j.input || '').trim()
  if (!text) throw httpError(400, 'input is empty')
  if (text.length > 2000) throw httpError(400, 'input longer than 2000 characters')
  const key = JSON.stringify([j.voice || cfg.voice, cfg.ttsSteps, cfg.ttsLanguage, text])
  if (j.response_format !== 'pcm' && ttsCache.has(key)) {
    const wav = ttsCache.get(key)
    ttsCache.delete(key)
    ttsCache.set(key, wav) // most recently used last
    res.setHeader('X-Synthesis-Ms', '0')
    res.setHeader('X-Cache', 'hit')
    return sendBytes(res, 200, 'audio/wav', wav)
  }
  const r = await engines.speak(text, { voice: j.voice })
  // "tts N chars -> X ms audio in Y ms" is parsed: new fields go after it. waited: from the request to the start;
  // load: the voice model; beside: other jobs on the AI cores meanwhile; yielded: what waited or was cancelled for it
  const extra = [`waited ${r.waitMs} ms`]
  if (r.loadMs >= 50) extra.push(`load ${r.loadMs} ms`)
  extra.push('beside ' + (r.beside.length ? r.beside.join('+') : 'none'))
  extra.push('yielded ' + (r.yielded.length ? r.yielded.join('+') : 'none'))
  log(`tts ${text.length} chars -> ${Math.round(r.pcm.length / r.rate * 1000)} ms audio in ${r.ms} ms (${extra.join(', ')})`)
  res.setHeader('X-Synthesis-Ms', String(r.ms))
  if (j.response_format === 'pcm') return sendBytes(res, 200, 'audio/pcm; rate=' + r.rate, Buffer.from(r.pcm.buffer, r.pcm.byteOffset, r.pcm.byteLength))
  const wav = audio.toWav(r.pcm, r.rate)
  if (text.length <= 160) {
    ttsCache.set(key, wav)
    if (ttsCache.size > TTS_CACHE_MAX) ttsCache.delete(ttsCache.keys().next().value)
  }
  sendBytes(res, 200, 'audio/wav', wav)
}

/** ", yielded to speech Nx, done after T ms" for a warm-up or prefill that waited for spoken answers. */
function yieldNote (r) {
  return r.yields ? `, yielded to speech ${r.yields}x, done after ${r.totalMs} ms` : ''
}

async function completions (req, res, j) {
  if (!Array.isArray(j.messages) || !j.messages.length) throw httpError(400, 'messages is required')
  // the local coder gets the slim profile (compat.slim): a short system prompt, the core tools (OpenCode threads)
  if (compat.wantsCoder(j.model) && cfg.coderSlim !== false) Object.assign(j, compat.slim({ messages: j.messages.map((m) => Object.assign({}, m, { content: Array.isArray(m.content) ? m.content.map((p) => typeof p === 'string' ? p : p.text || '').join('') : m.content })), tools: j.tools || [] }))
  if (j.stream) return completionsStream(req, res, j)
  const r = await engines.chat(j.messages, Array.isArray(j.tools) ? j.tools : [], { max_tokens: j.max_tokens, temperature: j.temperature, grammar: j.grammar, engine: compat.wantsCoder(j.model) ? 'coder' : 'llm' })
  const calls = r.tool_calls.map((c, i) => ({ id: 'call_' + Date.now().toString(36) + i, type: 'function', function: { name: c.name, arguments: JSON.stringify(c.arguments || {}) } }))
  const st = r.stats || {}
  log(`llm ${r.ms} ms (first ${r.firstMs} ms, prompt ${st.promptTokens || '?'} tok): ${calls.length ? calls.map((c) => c.function.name + c.function.arguments).join(' ') : JSON.stringify(r.content).slice(0, 120)}`)
  send(res, 200, {
    id: 'chatcmpl-' + Date.now().toString(36),
    object: 'chat.completion',
    created: Math.floor(Date.now() / 1000),
    model: (compat.wantsCoder(j.model) ? CATALOGUE.coder : CATALOGUE.llm).id,
    choices: [{ index: 0, message: Object.assign({ role: 'assistant', content: r.content || null }, calls.length ? { tool_calls: calls } : {}), finish_reason: calls.length ? 'tool_calls' : 'stop' }],
    usage: { prompt_tokens: st.promptTokens || 0, completion_tokens: st.generatedTokens || 0, total_tokens: (st.promptTokens || 0) + (st.generatedTokens || 0) },
    timings: { ms: r.ms, firstMs: r.firstMs, tps: st.TPS || null }
  })
}

/** stream: true -> text/event-stream, OpenAI chunk format: a role chunk, one delta.content per decoded token (no
 * <think> block, no <tool_call> text), a final chunk with finish_reason (and parsed tool_calls), then [DONE]. The
 * client closing the connection cancels the generation (a queued one is skipped). */
async function completionsStream (req, res, j) {
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
    const o = { id, object: 'chat.completion.chunk', created, model: (compat.wantsCoder(j.model) ? CATALOGUE.coder : CATALOGUE.llm).id, choices: [{ index: 0, delta, finish_reason: finish || null }] }
    try { res.write('data: ' + JSON.stringify(Object.assign(o, extra || {})) + '\n\n') } catch { gone() }
  }
  chunk({ role: 'assistant' })
  let r
  try {
    r = await engines.chat(j.messages, Array.isArray(j.tools) ? j.tools : [], {
      max_tokens: j.max_tokens, temperature: j.temperature, grammar: j.grammar, abort, engine: compat.wantsCoder(j.model) ? 'coder' : 'llm',
      onToken: (t) => chunk({ content: t })
    })
  } catch (e) {
    chunk({}, 'error', { error: String(e.message || e) })
    try { res.end('data: [DONE]\n\n') } catch {}
    throw Object.assign(e, { streamed: true })
  }
  const calls = r.tool_calls.map((c, i) => ({ index: i, id: 'call_' + Date.now().toString(36) + i, type: 'function', function: { name: c.name, arguments: JSON.stringify(c.arguments || {}) } }))
  const st = r.stats || {}
  log(`llm stream ${r.ms} ms (first ${r.firstMs} ms, prompt ${st.promptTokens || '?'} tok)${r.cancelled ? ' cancelled' : ''}: ${JSON.stringify(r.content).slice(0, 120)}`)
  chunk(calls.length ? { tool_calls: calls } : {}, calls.length ? 'tool_calls' : 'stop', {
    usage: { prompt_tokens: st.promptTokens || 0, completion_tokens: st.generatedTokens || 0 }, timings: { ms: r.ms, firstMs: r.firstMs, tps: st.TPS || null }
  })
  try { res.end('data: [DONE]\n\n') } catch {}
}

/** A server-sent-events response: event(name, obj) writes `event:` + `data:` lines, end() closes it; `abort` is set
 * when the client goes away (the socket, not the request: bare-http1 closes the request once its body is read). */
function sse (req, res) {
  const abort = { aborted: false, onabort: null }
  const gone = () => { if (abort.aborted) return; abort.aborted = true; if (abort.onabort) abort.onabort() }
  try { if (req.socket) req.socket.on('close', gone) } catch {}
  res.statusCode = 200
  res.setHeader('Content-Type', 'text/event-stream')
  res.setHeader('Cache-Control', 'no-cache')
  return {
    abort,
    event (name, obj) { if (abort.aborted) return; try { res.write('event: ' + name + '\ndata: ' + JSON.stringify(obj) + '\n\n') } catch { gone() } },
    end () { try { res.end() } catch {} }
  }
}

function authorized (req, key) {
  // Anthropic clients send the key as x-api-key (Claude Code with ANTHROPIC_API_KEY), the others as a bearer token
  if (req.headers['x-api-key'] && !req.headers.authorization) req.headers.authorization = 'Bearer ' + req.headers['x-api-key']
  const h = String(req.headers.authorization || '')
  const want = 'Bearer ' + (key || TOKEN)
  if (h.length !== want.length) return false
  let d = 0
  for (let i = 0; i < h.length; i++) d |= h.charCodeAt(i) ^ want.charCodeAt(i)
  return d === 0
}

function kindsOf (j) {
  const k = j.kinds || j.models || j.kind
  if (!k) return Object.keys(CATALOGUE).filter((x) => !CATALOGUE[x].optional) // the coder only on request
  return (Array.isArray(k) ? k : [k]).map(String)
}

function send (res, code, obj) {
  sendBytes(res, code, 'application/json', Buffer.from(JSON.stringify(obj)))
}

function sendBytes (res, code, type, buf) {
  res.statusCode = code
  res.setHeader('Content-Type', type)
  res.setHeader('Content-Length', buf.length)
  res.end(buf)
}

function httpError (status, message) {
  const e = new Error(message)
  e.status = status
  return e
}

function parseJson (buf) {
  if (!buf || !buf.length) return {}
  try { return JSON.parse(buf.toString('utf8')) } catch { throw httpError(400, 'body is not JSON') }
}

function readJson (file) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8')) } catch { return null }
}

function parseArgs (argv) {
  const o = {}
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i]
    if (!a.startsWith('--')) continue
    const eq = a.indexOf('=')
    if (eq > 0) o[a.slice(2, eq)] = a.slice(eq + 1)
    else if (i + 1 < argv.length && !argv[i + 1].startsWith('--')) o[a.slice(2)] = argv[++i]
    else o[a.slice(2)] = 'true'
  }
  return o
}

const server = http.createServer((req, res) => {
  const chunks = []
  let size = 0
  let tooBig = false
  req.on('data', (c) => {
    size += c.length
    if (size > MAX_BODY) tooBig = true
    else chunks.push(c)
  })
  req.on('end', () => {
    if (tooBig) return send(res, 413, { error: 'body larger than ' + MAX_BODY + ' bytes' })
    handle(req, res, Buffer.concat(chunks)).catch((e) => {
      if (!e.status || e.status >= 500) log(`${req.method} ${req.url}: ${e.stack || e}`)
      if (e.streamed) return // the stream already carried the error
      try { send(res, e.status || 500, { error: String(e.message || e) }) } catch {}
    })
  })
  req.on('error', (e) => log('request error: ' + (e.message || e)))
})

const PIDFILE = path.join(DATA, 'service.pid')

function shutdown () {
  log('shutting down')
  try { fs.unlinkSync(PIDFILE) } catch {}
  engines.unload(['asr', 'llm', 'tts']).finally(() => Bare.exit(0))
}

server.on('error', (e) => {
  log('server error: ' + (e.stack || e))
  Bare.exit(3)
})

server.listen(PORT, HOST, () => {
  fs.writeFileSync(PIDFILE, String(Bare.pid))
  log(`metacodex-ai ${VERSION} listening on ${HOST}:${PORT}, data ${DATA}, threads ${cfg.threads}, token ${TOKEN ? 'on' : 'OFF'}`)
})
