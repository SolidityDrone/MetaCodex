'use strict'
// The three engines (speech-to-text, LLM, text-to-speech): loaded on first use, one job at a time,
// unloaded after some idle minutes. CPU only, few threads: the GPU and the other cores belong to the
// app's frame loop (docs/qvac.md, "Rules for a stable frame rate").
const fs = require('bare-fs')
const path = require('bare-path')
const { CATALOGUE } = require('./models')

const KINDS = ['asr', 'llm', 'tts']

class Engines {
  constructor ({ models, cfg, log, cacheDir }) {
    this.models = models
    this.cfg = cfg
    this.log = log
    this.cacheDir = cacheDir
    fs.mkdirSync(cacheDir, { recursive: true })
    this.inst = { asr: null, llm: null, tts: null }
    this.state = { asr: 'unloaded', llm: 'unloaded', tts: 'unloaded' }
    this.lastUsed = { asr: 0, llm: 0, tts: 0 }
    this.loadMs = {}
    this.loadedWith = {}
    // one queue per engine: speech-to-text never waits behind an LLM warm-up, and each engine runs one job
    // at a time with `threads` threads (all of them pinned to cores 0-2 by the launcher)
    this.chains = { asr: Promise.resolve(), llm: Promise.resolve(), tts: Promise.resolve() }
    this.busy = { asr: null, llm: null, tts: null }
    this.queued = { asr: 0, llm: 0, tts: 0 }
    this.paused = false
    this.waiters = []
    this.jobs = 0
    this.last = {}
    this.asrWant = { actx: 512, language: null }
    this.ttsWant = { voice: null }
    this.idleTimer = setInterval(() => this._unloadIdle(), 15000)
  }

  status () {
    const now = Date.now()
    const engines = {}
    for (const k of KINDS) {
      engines[k] = { state: this.state[k], model: CATALOGUE[k].id }
      if (this.inst[k]) engines[k].idleS = Math.round((now - this.lastUsed[k]) / 1000)
      if (this.loadMs[k]) engines[k].loadMs = this.loadMs[k]
    }
    const busy = KINDS.map((k) => this.busy[k]).filter(Boolean)
    return { busy: busy.length ? busy.join(',') : null, queued: KINDS.reduce((n, k) => n + this.queued[k], 0), paused: this.paused, jobs: this.jobs, lastMs: this.last, engines }
  }

  /** Runs the jobs of one engine one at a time; waits while paused. */
  job (kind, label, fn) {
    this.queued[kind]++
    const run = async () => {
      this.queued[kind]--
      while (this.paused) await new Promise((resolve) => this.waiters.push(resolve))
      this.busy[kind] = label
      const t0 = Date.now()
      try { return await fn() } finally {
        this.busy[kind] = null
        this.jobs++
        this.last[label] = Date.now() - t0
      }
    }
    const p = this.chains[kind].then(run, run)
    this.chains[kind] = p.catch(() => {})
    return p
  }

  pause () { this.paused = true }

  resume () {
    this.paused = false
    const w = this.waiters
    this.waiters = []
    for (const r of w) r()
  }

  unload (kinds) {
    return Promise.all(kinds.filter((k) => KINDS.includes(k)).map((k) => this.job(k, 'unload', () => this._unload(k))))
  }

  /** Text from 16 kHz mono Int16 PCM. */
  transcribe (pcm, opts = {}) {
    return this.job('asr', 'asr', async () => {
      const seconds = pcm.length / 16000
      // whisper encodes a fixed 30 s window; commands are short, so encode ~10 s (4x faster, measured)
      // whisper encodes a 30 s window (1500 frames); encode only the clip + a margin: frames = 50 per second
      const actx = Math.min(1500, Math.max(Number(this.cfg.minAudioCtx) || 512, Math.ceil((seconds * 50 + 96) / 64) * 64))
      this.asrWant = { actx: 0, language: opts.language || null } // actx is not part of the load key: set per run below
      const asr = await this._ensure('asr')
      if (asr._mcxActx !== actx) {
        const r0 = Date.now()
        await asr.reload({ whisperConfig: { audio_ctx: actx } })
        asr._mcxActx = actx
        this.last.asrReload = Date.now() - r0
      }
      const t0 = Date.now()
      const r = await asr.run(pcm)
      let text = ''
      await r.onUpdate((out) => {
        for (const s of Array.isArray(out) ? out : [out]) if (s && typeof s.text === 'string') text += s.text
      }).await()
      return { text: cleanTranscript(text), ms: Date.now() - t0, audioMs: Math.round(seconds * 1000), audioCtx: actx }
    })
  }

  /** OpenAI-style chat with optional tools -> { content, tool_calls, ms, firstMs, stats }. */
  chat (messages, tools, opts = {}) {
    return this.job('llm', 'llm', async () => {
      const llm = await this._ensure('llm')
      const prompt = [...(tools || []).map(toQvacTool), ...(messages || []).map(toQvacMessage)]
      const gen = { temp: opts.temperature != null ? Number(opts.temperature) : 0, predict: opts.max_tokens != null ? Number(opts.max_tokens) : 256 }
      if (typeof opts.grammar === 'string' && opts.grammar.trim()) gen.grammar = opts.grammar // GBNF: e.g. one command line
      const runOpts = { generationParams: gen }
      const base = this._prefixFile(tools, (messages || []).slice(0, -1)) // all but the new user turn
      if (fs.existsSync(base)) runOpts.baseCacheKey = base // system prompt + examples + tools, prefilled once (warm)
      const t0 = Date.now()
      let first = 0
      let out = ''
      const r = await llm.run(prompt, runOpts)
      for await (const tok of r.iterate()) {
        if (!first) first = Date.now() - t0
        out += tok
      }
      return Object.assign(parseAssistant(out), { ms: Date.now() - t0, firstMs: first, stats: r.stats || null })
    })
  }

  /**
   * Prefills the system prompt + tools once and saves that state to a file; later requests with the same
   * system prompt and tools start from it (in memory, or from the file after a reload) and only decode the
   * user's words. Without it the first command after a load prefills ~700 tokens (~7 s on 2 cores).
   */
  warm (messages, tools) {
    return this.job('llm', 'warmup', async () => {
      const llm = await this._ensure('llm')
      const base = this._prefixFile(tools, messages) // the stable prefix: system prompt, example turns
      if (fs.existsSync(base)) return { ms: 0, cached: true }
      const prefix = (messages || []).filter((m) => m && m.role).map(toQvacMessage)
      const prompt = [...(tools || []).map(toQvacTool), ...(prefix.length ? prefix : [{ role: 'system', content: '' }])]
      const t0 = Date.now()
      const r = await llm.run(prompt, { prefill: true, cacheKey: base })
      for await (const _ of r.iterate()) {} // eslint-disable-line no-unused-vars
      await llm.saveCache(base)
      this._pruneCache(base)
      return { ms: Date.now() - t0, cached: false }
    })
  }

  /** The cache file for a stable prompt prefix (tools + the messages before the new user turn). */
  _prefixFile (tools, prefixMessages) {
    const prefix = (prefixMessages || []).filter((m) => m && m.role).map((m) => [m.role, m.content])
    const key = fnv1a(JSON.stringify([CATALOGUE.llm.id, this.cfg.ctx, (tools || []).map(toQvacTool), prefix]))
    return path.join(this.cacheDir, 'prefix-' + key + '.kv')
  }

  _pruneCache (keep) {
    try {
      const files = fs.readdirSync(this.cacheDir).filter((f) => f.startsWith('prefix-')).map((f) => path.join(this.cacheDir, f))
      files.sort((a, b) => fs.statSync(b).mtimeMs - fs.statSync(a).mtimeMs)
      for (const f of files.slice(4)) if (f !== keep) fs.unlinkSync(f)
    } catch (e) { this.log('cache prune: ' + (e.message || e)) }
  }

  /** Speech for `text` -> { pcm: Int16Array, rate, ms }. */
  speak (text, opts = {}) {
    return this.job('tts', 'tts', async () => {
      this.ttsWant = { voice: opts.voice || null }
      const tts = await this._ensure('tts')
      const t0 = Date.now()
      const r = await tts.run({ input: text, type: 'text' })
      const parts = []
      let n = 0
      let rate = 44100
      await r.onUpdate((d) => {
        if (!d) return
        if (d.sampleRate) rate = d.sampleRate
        if (d.outputArray) {
          const a = Int16Array.from(d.outputArray)
          parts.push(a)
          n += a.length
        }
      }).await()
      const pcm = new Int16Array(n)
      let o = 0
      for (const p of parts) { pcm.set(p, o); o += p.length }
      return { pcm, rate, ms: Date.now() - t0 }
    })
  }

  _key (kind) {
    const c = this.cfg
    if (kind === 'asr') return JSON.stringify([c.threads, this.asrWant.language || c.language, c.prompt, this.asrWant.actx])
    if (kind === 'llm') return JSON.stringify([c.threads, c.ctx])
    return JSON.stringify([c.threads, this.ttsWant.voice || c.voice, c.ttsLanguage, c.ttsSteps])
  }

  async _ensure (kind) {
    if (!this.models.installed(kind)) {
      const e = new Error(`model not installed: ${kind} (${CATALOGUE[kind].id}); POST /v1/models/install`)
      e.status = 409
      throw e
    }
    const key = this._key(kind)
    if (this.inst[kind] && this.loadedWith[kind] !== key) await this._unload(kind)
    if (!this.inst[kind]) {
      this.state[kind] = 'loading'
      const t0 = Date.now()
      try {
        this.inst[kind] = await this._load(kind)
      } catch (e) {
        this.state[kind] = 'error'
        throw e
      }
      this.loadedWith[kind] = key
      this.state[kind] = 'loaded'
      this.loadMs[kind] = Date.now() - t0
      this.log(`loaded ${kind} (${CATALOGUE[kind].id}) in ${this.loadMs[kind]} ms, key ${key}`)
    }
    this.lastUsed[kind] = Date.now()
    return this.inst[kind]
  }

  async _load (kind) {
    const c = this.cfg
    const file = this.models.file(kind)
    let m
    if (kind === 'asr') {
      const ASRGgml = require('@qvac/asr-ggml')
      const whisperConfig = {
        language: this.asrWant.language || c.language,
        audio_format: 's16le',
        n_threads: c.threads,
        no_timestamps: true,
        single_segment: true,
        max_tokens: 128,
        audio_ctx: 512
      }
      if (c.prompt) whisperConfig.initial_prompt = c.prompt
      m = new ASRGgml({ files: { model: file }, config: { engine: 'whisper', whisperConfig, contextParams: { use_gpu: false } } })
    } else if (kind === 'llm') {
      const Llm = require('@qvac/llm-llamacpp')
      m = new Llm({
        files: { model: [file] },
        config: { device: 'cpu', ctx_size: String(c.ctx), threads: String(c.threads), tools: 'true', reasoning_budget: '0', temp: '0', predict: '256' },
        opts: { stats: true }
      })
    } else {
      const TTSGgml = require('@qvac/tts-ggml')
      m = new TTSGgml({
        engine: TTSGgml.ENGINE_SUPERTONIC,
        files: { supertonicModel: file },
        voice: this.ttsWant.voice || c.voice,
        threads: c.threads,
        steps: Math.max(1, Math.min(10, Number(c.ttsSteps) || 5)), // diffusion steps: time grows with them (model default 5)
        config: { language: c.ttsLanguage, useGPU: false },
        opts: { stats: true }
      })
    }
    await m.load()
    return m
  }

  async _unload (kind) {
    const m = this.inst[kind]
    if (!m) return
    this.inst[kind] = null
    this.state[kind] = 'unloaded'
    try {
      if (typeof m.destroy === 'function' && kind === 'asr') await m.destroy()
      else await m.unload()
    } catch (e) { this.log(`unload ${kind}: ${e.message || e}`) }
    this.log('unloaded ' + kind)
  }

  _unloadIdle () {
    const now = Date.now()
    for (const k of KINDS) {
      if (this.busy[k] || this.queued[k]) continue
      const minutes = Number((this.cfg.idleMinutes || {})[k] || 0)
      if (this.inst[k] && minutes > 0 && now - this.lastUsed[k] > minutes * 60000) {
        this.job(k, 'unload', async () => {
          if (this.inst[k] && Date.now() - this.lastUsed[k] > minutes * 60000) await this._unload(k)
        }).catch(() => {})
      }
    }
  }
}

/** Whisper marks non-speech as [BLANK_AUDIO], (music) and the like; drop those. */
function cleanTranscript (t) {
  return String(t || '').replace(/\[[^\]]*\]|\([^)]*\)|\*[^*]*\*/g, ' ').replace(/\s+/g, ' ').trim()
}

/** OpenAI tool {type, function: {name, description, parameters}} -> QVAC {type, name, description, parameters}. */
function toQvacTool (t) {
  const f = t && t.function ? t.function : t
  return { type: 'function', name: f.name, description: f.description || '', parameters: f.parameters || { type: 'object', properties: {} } }
}

/** OpenAI assistant tool_calls (arguments as JSON text) -> QVAC tool_calls (arguments object). */
function toQvacMessage (m) {
  if (!m || typeof m !== 'object') return m
  const out = { role: m.role, content: m.content == null ? '' : m.content }
  if (Array.isArray(m.tool_calls)) {
    out.tool_calls = m.tool_calls.map((c, i) => {
      const f = c.function || c
      let args = f.arguments
      if (typeof args === 'string') { try { args = JSON.parse(args) } catch { args = {} } }
      return { id: c.id || 'call_' + i, name: f.name, arguments: args || {} }
    })
  }
  if (m.tool_call_id) out.tool_call_id = m.tool_call_id
  if (m.name) out.name = m.name
  return out
}

/** Qwen-style output: optional <think>, then text and/or <tool_call>{json}</tool_call> blocks. */
function parseAssistant (raw) {
  let text = String(raw || '').replace(/<think>[\s\S]*?<\/think>/g, '')
  const calls = []
  text = text.replace(/<tool_call>\s*([\s\S]*?)\s*(<\/tool_call>|$)/g, (_, body) => {
    try {
      const j = JSON.parse(body)
      calls.push({ name: j.name, arguments: typeof j.arguments === 'string' ? safeJson(j.arguments) : (j.arguments || {}) })
    } catch {
      calls.push({ name: null, arguments: {}, error: 'unparsed tool call: ' + body.slice(0, 200) })
    }
    return ''
  })
  text = text.trim()
  // small models sometimes write the call as bare JSON instead of a <tool_call> block
  if (!calls.length && /^\{[\s\S]*"name"[\s\S]*\}$/.test(text)) {
    try {
      const j = JSON.parse(text)
      if (typeof j.name === 'string') {
        calls.push({ name: j.name, arguments: typeof j.arguments === 'string' ? safeJson(j.arguments) : (j.arguments || {}) })
        text = ''
      }
    } catch {}
  }
  return { content: text, tool_calls: calls.filter((c) => c.name), raw }
}

function safeJson (s) { try { return JSON.parse(s) } catch { return {} } }

/** 64-bit FNV-1a of a string, hex (file names for prompt caches). */
function fnv1a (str) {
  let h = 0xcbf29ce484222325n
  const p = 0x100000001b3n
  for (let i = 0; i < str.length; i++) {
    h ^= BigInt(str.charCodeAt(i))
    h = (h * p) & 0xffffffffffffffffn
  }
  return h.toString(16).padStart(16, '0')
}

module.exports = { Engines, parseAssistant, cleanTranscript, toQvacTool, toQvacMessage }
