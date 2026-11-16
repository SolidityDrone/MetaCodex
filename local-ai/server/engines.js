'use strict'
// The engines (speech-to-text, LLM, text-to-speech, sentence vectors): loaded on first use, one job at a time,
// unloaded after some idle minutes. CPU only, few threads: the GPU and the other cores belong to the
// app's frame loop (docs/local-ai.md, "Rules for a stable frame rate").
const fs = require('bare-fs')
const path = require('bare-path')
const { CATALOGUE } = require('./models')
const { parseAssistant, thinkStripper, callGuard, safeJson } = require('./toolcalls')

const KINDS = ['asr', 'llm', 'tts', 'embed', 'coder']
const INTERFACE = ['asr', 'llm', 'tts', 'embed'] // the app's own voice / UI AI
const BULK_EMBED = 8 // more texts than this in one request: background work (a spoken command has a few clauses)
const ASR_MAX_TOKENS = 128 // speech-to-text's token limit (a command is 5-30 tokens)
// whisper.cpp's default temperature step (whisper_full_default_params): speech-to-text runs with it (decodes again
// hotter when a result looks wrong); the wake check turns it off (one pass) and speech-to-text puts it back after one
const ASR_TEMPERATURE_INC = 0.2
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))

class Engines {
  constructor ({ models, cfg, log, cacheDir }) {
    this.models = models
    this.cfg = cfg
    this.log = log
    this.cacheDir = cacheDir
    fs.mkdirSync(cacheDir, { recursive: true })
    const each = (v) => Object.fromEntries(KINDS.map((k) => [k, v]))
    this.inst = each(null)
    this.state = each('unloaded')
    this.lastUsed = each(0)
    this.loadMs = {}
    this.loadedWith = {}
    // one queue per engine: speech-to-text never waits behind an LLM warm-up, and each engine runs one job
    // at a time with `threads` threads (all of them pinned to cores 0-2 by the launcher)
    this.chains = Object.fromEntries(KINDS.map((k) => [k, Promise.resolve()]))
    this.busy = each(null)
    this.queued = each(0)
    this.paused = false
    this.waiters = []
    this.jobs = 0
    this.last = {}
    this.asrWant = { actx: 512, language: null }
    this.ttsWant = { voice: null }
    // speech first (speechActive): spoken answers waiting or being made, the end of the last one, the background LLM
    // run that a new one cancels (warm-up or speculative prefill), and what yielded to speech (for the tts log lines)
    this.speechPending = 0
    this.speechLastEnd = 0
    this._bgLlm = null
    this._yields = []
    this._ttsBeside = null
    this.speechYields = { warmup: 0, prefill: 0, embed: 0 }
    // wake checks ("Hey Whisp", wakeCheck): done, their own time, and the ones dropped because the service was busy
    this.wake = { checks: 0, ms: 0, skipped: { paused: 0, coder: 0, speech: 0, busy: 0, model: 0 } }
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
    return { coderHold: this.coderHolds(), speech: { active: this.speechActive(), yields: this.speechYields }, wake: this.wake, busy: busy.length ? busy.join(',') : null, queued: KINDS.reduce((n, k) => n + this.queued[k], 0), paused: this.paused, jobs: this.jobs, lastMs: this.last, engines }
  }

  /**
   * Speech has priority (cfg.backgroundYieldsToSpeech, default on; docs/voice.md "Speech has priority"): measured on
   * the headset, a 23 s warm-up and the decision model's example vectors ran beside the spoken answer on the same 3
   * cores and made it 3x slower than it plays. While a spoken answer is queued or being made, and for speechHoldMs
   * after the last one (the app asks for the next sentence when one is done or played), our own background jobs wait:
   * the warm-up, a speculative prefill and bulk sentence vectors (more than BULK_EMBED texts) do not start, and a
   * warm-up or prefill that runs is cancelled and done again afterwards. Each request yields for at most
   * maxYieldMs in total (the app's HTTP timeouts), then runs anyway. Real requests (chat, speech-to-text, the
   * vectors of a spoken command) never wait for speech.
   */
  speechActive () {
    if (this.cfg.backgroundYieldsToSpeech === false) return false
    const hold = this.cfg.speechHoldMs != null ? Number(this.cfg.speechHoldMs) : 3000
    return this.speechPending > 0 || Date.now() - this.speechLastEnd < hold
  }

  _yieldBudget () { return { left: this.cfg.maxYieldMs != null ? Number(this.cfg.maxYieldMs) : 60000 } }

  /** Waits while speech is active (within the budget; `stop()` true ends it early) -> ms waited. */
  async _awaitSpeech (what, budget, stop) {
    if (budget.left <= 0 || !this.speechActive() || (stop && stop())) return 0
    const y = this._noteYield(what + ' paused', null)
    while (this.speechActive() && Date.now() - y.t0 < budget.left && !(stop && stop())) await sleep(50)
    y.t1 = Date.now()
    budget.left -= y.t1 - y.t0
    return y.t1 - y.t0
  }

  /** Records that `what` yielded to speech (t1 null: still yielding); kept 2 minutes, for the tts log lines. */
  _noteYield (what, t1) {
    const now = Date.now()
    const y = { what, t0: now, t1 }
    this._yields = this._yields.filter((e) => e.t1 == null || now - e.t1 < 120000)
    this._yields.push(y)
    this.speechYields[what.split(' ')[0]]++
    return y
  }

  /** A spoken answer arrived: the background LLM run (warm-up or speculative prefill) stops now and runs again later. */
  _yieldLlmToSpeech () {
    const h = this._bgLlm
    if (!h || h.yielded || h.budget.left <= 0) return
    h.yielded = true
    this._noteYield(h.what + ' cancelled', Date.now())
    if (h.r) h.r.cancel().catch(() => {})
  }

  /**
   * One background LLM prefill (`what`: warmup | prefill), cancellable for speech: -> { again: true } when speech
   * came (wait, then call again), else { ms, cancelled, stats }. A run cancelled by speech-to-text (opts.abort)
   * ends for good.
   */
  async _bgPrefill (what, llm, prompt, runOpts, budget, abort) {
    const h = { what, r: null, yielded: false, budget }
    this._bgLlm = h
    const t0 = Date.now()
    try {
      h.r = await llm.run(prompt, runOpts)
      if (abort) abort.onabort = () => { h.r.cancel().catch(() => {}) }
      if (h.yielded || (abort && abort.aborted)) h.r.cancel().catch(() => {})
      try {
        for await (const _ of h.r.iterate()) {} // eslint-disable-line no-unused-vars
      } catch (e) { if (!h.yielded && !(abort && abort.aborted)) throw e } // a cancelled run may end with an error
    } finally {
      if (this._bgLlm === h) this._bgLlm = null // before anything else: a later speech request no longer marks this run
      if (abort) abort.onabort = null
    }
    if (abort && abort.aborted) return { ms: Date.now() - t0, cancelled: true, stats: h.r.stats || null }
    if (h.yielded) return { again: true }
    return { ms: Date.now() - t0, cancelled: false, stats: h.r.stats || null }
  }

  /**
   * The local coder and the interface AI never share the 3 AI cores (owner 2026-11-12: "if we choose the local
   * provider ... talk or local AI for interfacing with UI shall await, otherwise we hit a bottleneck"): while the
   * coder works, and for coderHoldMs after its last request (an agent turn is many requests with tool runs between
   * them), speech-to-text, the voice LLM, speech and sentence vectors wait; a coder request waits for an interface
   * job that already runs (they are short).
   */
  coderHolds () {
    return !!(this.busy.coder || this.queued.coder > 0 || Date.now() - (this.coderLastEnd || 0) < (Number(this.cfg.coderHoldMs) || 15000))
  }

  async _gate (kind) {
    if (kind === 'coder') {
      while (INTERFACE.some((k) => this.busy[k])) await sleep(100)
      // the coder gets the memory and the cores while it works (owner 2026-11-13: the headset froze with the coder and
      // the voice models loaded together): the interface models are unloaded; each loads again at its next use, after
      // the hold (TALK says "local coder working" meanwhile)
      if (this.cfg.coderUnloadsInterface !== false) {
        for (const k of INTERFACE) if (this.inst[k] && !this.busy[k]) await this._unload(k)
      }
      return
    }
    if (!INTERFACE.includes(kind)) return
    let waited = 0
    while (this.coderHolds()) { await sleep(250); waited += 250 }
    if (waited) this.log(`${kind} waited ${waited} ms for the local coder`)
  }

  /** Runs the jobs of one engine one at a time; waits while paused. */
  job (kind, label, fn) {
    this.queued[kind]++
    const run = async () => {
      if (label !== 'unload') await this._gate(kind)
      this.queued[kind]--
      while (this.paused) await new Promise((resolve) => this.waiters.push(resolve))
      this.busy[kind] = label
      if (kind !== 'tts' && label !== 'unload' && this._ttsBeside) this._ttsBeside.add(label) // shares the cores with speech
      const t0 = Date.now()
      try { return await fn() } finally {
        if (kind === 'coder') this.coderLastEnd = Date.now()
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
    // speech-to-text goes first: a speculative prefill still running would share the 3 cores with it
    if (this.cfg.prefillYieldsToAsr !== false && this.cancelSpeculative()) this.prefillsYielded = (this.prefillsYielded || 0) + 1
    return this.job('asr', 'asr', async () => {
      const seconds = pcm.length / 16000
      // whisper encodes a fixed 30 s window; commands are short, so encode ~10 s (4x faster, measured)
      // whisper encodes a 30 s window (1500 frames); encode only the clip + a margin: frames = 50 per second
      const actx = Math.min(1500, Math.max(Number(this.cfg.minAudioCtx) || 512, Math.ceil((seconds * 50 + 96) / 64) * 64))
      this.asrWant = { actx: 0, language: opts.language || null } // actx is not part of the load key: set per run below
      const asr = await this._ensure('asr')
      if (asr._mcxWake) {
        // the last run was a wake check (wakeCheck): speech-to-text's own settings go back first, in place
        const r0 = Date.now()
        await asr.reload({ whisperConfig: { audio_ctx: actx, initial_prompt: this.cfg.prompt || '', max_tokens: ASR_MAX_TOKENS, temperature_inc: ASR_TEMPERATURE_INC } })
        asr._mcxWake = false
        asr._mcxActx = actx
        this.last.asrReload = Date.now() - r0
      } else if (asr._mcxActx !== actx) {
        const r0 = Date.now()
        await asr.reload({ whisperConfig: { audio_ctx: actx } })
        asr._mcxActx = actx
        this.last.asrReload = Date.now() - r0
      }
      const t0 = Date.now()
      const text = await asrText(asr, pcm)
      return { text: cleanTranscript(text), ms: Date.now() - t0, audioMs: Math.round(seconds * 1000), audioCtx: actx }
    })
  }

  /** Why a wake check cannot run at once (null: it can). It never waits: the app sends the next segment instead. */
  wakeBlocked () {
    if (this.paused) return 'paused'
    if (!this.models.installed('asr')) return 'model'
    if (this.coderHolds()) return 'coder'
    if (this.speechActive()) return 'speech'
    if (KINDS.some((k) => this.busy[k] || this.queued[k] > 0) || (this._spec && !this._spec.aborted)) return 'busy'
    return null
  }

  /**
   * The wake check (POST /v1/audio/wake; docs/voice.md "Hey Whisp"): the text of one short voiced segment that the app's
   * voice gate cut (0.4-2.5 s with a little audio before it); the app decides whether it is the wake phrase. The lowest
   * speech job: dropped, never queued, when anything else runs or holds the cores -> { skipped: paused | model | coder |
   * speech | busy }. The cheapest decode: an encoder window sized to the clip (opts.audioCtx, else cfg.wakeMinAudioCtx
   * at least; 512 frames is speech-to-text's floor), the prompt "Hey Whisp." (opts.prompt / cfg.wakePrompt), a few
   * tokens (opts.maxTokens / cfg.wakeMaxTokens) and one greedy pass (no hotter retries). Whisper settings changed in
   * place, no model reload; speech-to-text puts its own back on its next run. -> { text, ms, audioMs, audioCtx,
   * reloadMs, loadMs }: ms is the decode, loadMs the model load when it was not loaded.
   */
  wakeCheck (pcm, opts = {}) {
    const why = this.wakeBlocked()
    if (why) {
      this.wake.skipped[why]++
      return Promise.resolve({ skipped: why })
    }
    const seconds = pcm.length / 16000
    const want = Math.floor(Number(opts.audioCtx)) || 0
    const floor = Math.floor(Number(this.cfg.wakeMinAudioCtx)) || 256
    const actx = Math.min(1500, want >= 64 ? Math.ceil(want / 64) * 64 : Math.max(floor, Math.ceil((seconds * 50 + 96) / 64) * 64))
    const prompt = String(opts.prompt || this.cfg.wakePrompt || 'Hey Whisp.').slice(0, 200)
    const tokens = Math.max(1, Math.min(32, Math.floor(Number(opts.maxTokens || this.cfg.wakeMaxTokens)) || 8))
    this.wake.checks++
    return this.job('asr', 'wake', async () => {
      // the load key stays speech-to-text's (its language; actx 0 as it sets it): no model reload between the two
      this.asrWant = { actx: 0, language: this.asrWant.language || null }
      const l0 = Date.now()
      const loaded = !!this.inst.asr
      const asr = await this._ensure('asr')
      const loadMs = loaded ? 0 : Date.now() - l0
      const key = JSON.stringify([actx, prompt, tokens])
      let reloadMs = 0
      if (!asr._mcxWake || asr._mcxWakeKey !== key) { // the same settings as the last wake check: no reload
        const r0 = Date.now()
        asr._mcxWake = true // before the reload: one that fails is put right by the next run
        asr._mcxWakeKey = null
        asr._mcxActx = actx
        await asr.reload({ whisperConfig: { audio_ctx: actx, initial_prompt: prompt, max_tokens: tokens, temperature_inc: 0 } })
        asr._mcxWakeKey = key
        reloadMs = Date.now() - r0
      }
      const t0 = Date.now()
      const text = await asrText(asr, pcm)
      const ms = Date.now() - t0
      this.wake.ms += ms
      return { text: cleanTranscript(text), ms, audioMs: Math.round(seconds * 1000), audioCtx: actx, reloadMs, loadMs }
    })
  }

  /**
   * OpenAI-style chat with optional tools -> { content, tool_calls, ms, firstMs, stats, cancelled }.
   *   opts.onToken(text): streamed answer text (no <think> block, no <tool_call> text), called per decoded token.
   *   The local coder's text is held from the first point where a call could start (callGuard: a `{`, a code fence)
   *   and what is left of it once parsed goes out as one last chunk: a bare JSON call is never streamed as text.
   *   opts.abort: { aborted, onabort } from the HTTP layer (the client went away): a job still queued is skipped,
   *   a running one is cancelled (QVAC response.cancel(); the loop ends with what was made so far).
   */
  chat (messages, tools, opts = {}) {
    const abort = opts.abort || null
    const kind = opts.engine === 'coder' ? 'coder' : 'llm' // coder: the local coding model of the agent harnesses
    return this.job(kind, opts.onToken ? kind + '-stream' : kind, async () => {
      if (abort && abort.aborted) return { content: '', tool_calls: [], raw: '', ms: 0, firstMs: 0, stats: null, cancelled: true, skipped: true }
      const llm = await this._ensure(kind)
      const prompt = [...(tools || []).map(toQvacTool), ...(messages || []).map(toQvacMessage)]
      const gen = { temp: opts.temperature != null ? Number(opts.temperature) : 0, predict: opts.max_tokens != null ? Math.min(Number(opts.max_tokens), kind === 'coder' ? 2048 : 1024) : (kind === 'coder' ? 1024 : 256) }
      if (typeof opts.grammar === 'string' && opts.grammar.trim()) gen.grammar = opts.grammar // GBNF: e.g. one command line
      const runOpts = { generationParams: gen }
      const base = kind === 'llm' ? this._prefixFile(tools, (messages || []).slice(0, -1)) : null // all but the new user turn
      if (base && fs.existsSync(base)) runOpts.baseCacheKey = base // system prompt + examples + tools, prefilled once (warm)
      const t0 = Date.now()
      let first = 0
      let out = ''
      const r = await llm.run(prompt, runOpts)
      if (abort) abort.onabort = () => { r.cancel().catch(() => {}) }
      if (abort && abort.aborted) abort.onabort()
      const guard = kind === 'coder' && opts.onToken ? callGuard(opts.onToken) : null
      const strip = opts.onToken ? thinkStripper(guard ? guard.feed : opts.onToken) : null
      try {
        for await (const tok of r.iterate()) {
          if (!first) first = Date.now() - t0
          out += tok
          if (strip && !(abort && abort.aborted)) strip(tok)
        }
      } finally { if (abort) abort.onabort = null }
      const res = parseAssistant(out, tools || [], { coder: kind === 'coder' })
      if (kind === 'coder') stopRepeat(messages, res)
      if (guard && !(abort && abort.aborted)) { const rest = guard.rest(res.content); if (rest) opts.onToken(rest) }
      return Object.assign(res, { ms: Date.now() - t0, firstMs: first, stats: r.stats || null, cancelled: !!(abort && abort.aborted) })
    })
  }

  /**
   * Speculative prefill (POST /v1/prefill): the session question's system prompt + context, without the question,
   * evaluated while the user still speaks, keyless (the addon keeps it in memory), so the real request decodes only
   * the question's ~17 tokens. Speech-to-text has priority: a transcription that arrives while it runs cancels it
   * (`cancelSpeculative`), the real request then prefills what is missing. Speech too (speechActive): it waits while
   * an answer is spoken, and a spoken answer cancels it while it runs; it then runs again once speech is over.
   */
  prefill (messages) {
    const abort = { aborted: false, onabort: null }
    this._spec = abort
    const budget = this._yieldBudget()
    const skipped = { ms: 0, cancelled: true, skipped: true }
    const t0 = Date.now()
    let yields = 0
    const run = async () => {
      for (;;) {
        await this._awaitSpeech('prefill', budget, () => abort.aborted)
        if (abort.aborted) return skipped
        const r = await this.job('llm', 'prefill', async () => {
          if (abort.aborted) return skipped
          if (budget.left > 0 && this.speechActive()) return { again: true } // speech came while it was queued
          const llm = await this._ensure('llm')
          if (abort.aborted) return skipped
          if (budget.left > 0 && this.speechActive()) return { again: true }
          return this._bgPrefill('prefill', llm, (messages || []).map(toQvacMessage), { prefill: true }, budget, abort)
        })
        if (!r.again) return Object.assign(r, { yields, totalMs: Date.now() - t0 })
        yields++
      }
    }
    return run().finally(() => { if (this._spec === abort) this._spec = null })
  }

  /** Cancels a speculative prefill that is queued or running (speech-to-text needs the cores now). */
  cancelSpeculative () {
    const a = this._spec
    if (!a || a.aborted) return false
    a.aborted = true
    this._spec = null
    if (a.onabort) a.onabort()
    return true
  }

  /**
   * Prefills the system prompt + tools once and saves that state to a file; later requests with the same
   * system prompt and tools start from it (in memory, or from the file after a reload) and only decode the
   * user's words. Without it the first command after a load prefills ~700 tokens (~7 s on 2 cores).
   * Speech has priority (speechActive): the warm-up waits while an answer is spoken; a spoken answer cancels a running
   * one, its part-made state is dropped (never saved as the prefix file) and it runs again from the start afterwards.
   * The reply comes once it is done: { ms (the run that completed), cached, yields, totalMs }.
   */
  warm (messages, tools) {
    const base = this._prefixFile(tools, messages) // the stable prefix: system prompt, example turns
    const ready = () => !!this.inst.llm && fs.existsSync(base) // loaded and cached: nothing to do, no wait
    const budget = this._yieldBudget()
    const t0 = Date.now()
    let yields = 0
    const run = async () => {
      for (;;) {
        if (!ready()) await this._awaitSpeech('warmup', budget)
        const r = await this.job('llm', 'warmup', async () => {
          if (budget.left > 0 && this.speechActive() && !ready()) return { again: true } // speech came while queued
          const llm = await this._ensure('llm')
          if (fs.existsSync(base)) return { ms: 0, cached: true }
          if (budget.left > 0 && this.speechActive()) return { again: true }
          const prefix = (messages || []).filter((m) => m && m.role).map(toQvacMessage)
          const prompt = [...(tools || []).map(toQvacTool), ...(prefix.length ? prefix : [{ role: 'system', content: '' }])]
          const p = await this._bgPrefill('warmup', llm, prompt, { prefill: true, cacheKey: base }, budget, null)
          if (p.again) { // cancelled part-way: drop that state, so no later eviction writes it as the prefix file
            try { await llm.discardCache(base) } catch (e) { this.log('warmup discard: ' + (e.message || e)) }
            try { if (fs.existsSync(base)) fs.unlinkSync(base) } catch {}
            return p
          }
          await llm.saveCache(base)
          this._pruneCache(base)
          return { ms: p.ms, cached: false }
        })
        if (!r.again) return Object.assign(r, { yields, totalMs: Date.now() - t0 })
        yields++
      }
    }
    return run()
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

  /**
   * Speech for `text` -> { pcm: Int16Array, rate, ms, waitMs, loadMs, beside, yielded }. Speech has priority
   * (speechActive): a background warm-up or prefill that runs is cancelled now. For the log: waitMs from the request
   * to the start of the job, loadMs for the model (0 when loaded), `beside` the other jobs that ran on the AI cores
   * meanwhile, `yielded` what waited or was cancelled for it.
   */
  speak (text, opts = {}) {
    const tq = Date.now()
    this.speechPending++
    if (this.cfg.backgroundYieldsToSpeech !== false) this._yieldLlmToSpeech()
    const p = this.job('tts', 'tts', async () => {
      const start = Date.now()
      this._ttsBeside = new Set(KINDS.filter((k) => k !== 'tts' && this.busy[k] && this.busy[k] !== 'unload').map((k) => this.busy[k]))
      try {
        const out = await this._speak(text, opts)
        const end = Date.now()
        const yielded = [...new Set(this._yields.filter((y) => y.t0 <= end && (y.t1 == null || y.t1 >= tq)).map((y) => y.what))]
        return Object.assign(out, { waitMs: start - tq, loadMs: out.t0 - start, beside: [...this._ttsBeside], yielded })
      } finally { this._ttsBeside = null }
    })
    const done = () => { this.speechPending--; this.speechLastEnd = Date.now() }
    p.then(done, done)
    return p
  }

  async _speak (text, opts) {
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
    return { pcm, rate, ms: Date.now() - t0, t0 }
  }

  /** Sentence vectors (multilingual-e5-small, mean pooled; L2-normalised here): texts -> { vectors: number[][], ms }.
   * Batches of 64 per pass. Texts are used as given (e5 wants "query: " in front; the app adds it).
   * Bulk work (more than BULK_EMBED texts: the decision model's examples, sent 64 at a time) yields to speech
   * (speechActive): each pass is its own job and starts only when no answer is spoken, so a spoken command's few
   * vectors still go first. ms: the passes' own time; waitMs: the time it waited for speech. */
  async embed (texts) {
    const bulk = texts.length > BULK_EMBED
    const budget = this._yieldBudget()
    const vectors = []
    let ms = 0
    let waitMs = 0
    for (let i = 0; i < texts.length; i += 64) {
      for (;;) {
        if (bulk) waitMs += await this._awaitSpeech('embed', budget)
        const r = await this.job('embed', 'embed', async () => {
          if (bulk && budget.left > 0 && this.speechActive()) return null // speech came while it was queued
          const m = await this._ensure('embed')
          if (bulk && budget.left > 0 && this.speechActive()) return null
          const t0 = Date.now()
          const out = await embedPass(m, texts.slice(i, i + 64))
          ms += Date.now() - t0
          return out
        })
        if (r) { for (const v of r) vectors.push(v); break }
      }
    }
    return { vectors, ms, waitMs }
  }

  /**
   * Threads for one engine: llmThreads / ttsThreads / asrThreads / embedThreads when set (1-6), else `threads`.
   * The LLM's prompt evaluation has its own count, llmBatchThreads (llama.cpp threads-batch), else the LLM's.
   * docs/voice.md "Streaming answers": 3 threads on the 3 cores keep LLM and TTS at solo speed while they overlap.
   */
  threadsFor (kind) {
    const v = Number(this.cfg[kind + 'Threads'])
    return v >= 1 ? Math.min(6, Math.floor(v)) : this.cfg.threads
  }

  batchThreads () {
    const v = Number(this.cfg.llmBatchThreads)
    return v >= 1 ? Math.min(6, Math.floor(v)) : this.threadsFor('llm')
  }

  /**
   * The voice LLM evaluates a prompt in pieces of llmPromptBatch tokens (llama.cpp batch-size = ubatch-size; 0 = the
   * addon's default). A cancel takes effect only between pieces: measured 2026-11-13 on the PC (3 cores, a 1549-token
   * prefill), the default (one piece) stopped 11.6 s after the cancel, 128 stopped after 114 ms, at the same speed
   * (12.9 s vs 13.3 s for the whole prompt). So speech can stop a warm-up or a prefill at once.
   */
  promptBatch () {
    const v = Number(this.cfg.llmPromptBatch)
    return v >= 1 ? Math.max(32, Math.min(2048, Math.floor(v))) : 0
  }

  _key (kind) {
    const c = Object.assign({}, this.cfg, { threads: this.threadsFor(kind) })
    if (kind === 'embed') return JSON.stringify([c.threads])
    if (kind === 'asr') return JSON.stringify([c.threads, this.asrWant.language || c.language, c.prompt, this.asrWant.actx])
    if (kind === 'llm') return JSON.stringify([c.threads, this.batchThreads(), c.ctx, this.promptBatch()])
    if (kind === 'coder') return JSON.stringify([c.threads, c.coderCtx])
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
    const c = Object.assign({}, this.cfg, { threads: this.threadsFor(kind) })
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
        max_tokens: ASR_MAX_TOKENS,
        audio_ctx: 512
      }
      if (c.prompt) whisperConfig.initial_prompt = c.prompt
      m = new ASRGgml({ files: { model: file }, config: { engine: 'whisper', whisperConfig, contextParams: { use_gpu: false } } })
    } else if (kind === 'llm') {
      const Llm = require('@qvac/llm-llamacpp')
      const config = { device: 'cpu', ctx_size: String(c.ctx), threads: String(c.threads), 'threads-batch': String(this.batchThreads()), tools: 'true', reasoning_budget: '0', temp: '0', predict: '256' }
      const pb = this.promptBatch()
      if (pb) { config['batch-size'] = String(pb); config['ubatch-size'] = String(pb) }
      m = new Llm({ files: { model: [file] }, config, opts: { stats: true } })
    } else if (kind === 'coder') {
      // an agent harness's system prompt + tools are 6-20k tokens (3-4k once slimmed, compat.js): a 16k context (KV
      // ~190 MB at f16 for Qwen2.5-Coder 0.5B: 2 KV heads of 64); thinking off (reasoning_budget 0) keeps turns short
      const Llm = require('@qvac/llm-llamacpp')
      m = new Llm({
        files: { model: [file] },
        config: { device: 'cpu', ctx_size: String(c.coderCtx || 16384), threads: String(c.threads), 'threads-batch': String(c.threads), tools: 'true', reasoning_budget: '0', temp: '0', predict: '1024' },
        opts: { stats: true }
      })
    } else if (kind === 'embed') {
      const GGMLBert = require('@qvac/embed-llamacpp')
      m = new GGMLBert({ files: { model: [file] }, config: { device: 'cpu', threads: String(c.threads), batch_size: '512', ctx_size: '512' } })
    } else {
      const TTSGgml = require('@qvac/tts-ggml')
      m = new TTSGgml({
        engine: TTSGgml.ENGINE_SUPERTONIC,
        files: { supertonicModel: file },
        voice: this.ttsWant.voice || c.voice,
        threads: c.threads,
        steps: Math.max(1, Math.min(10, Number(c.ttsSteps) || 3)), // diffusion steps 1-10: time grows with them (model default 5, ours 3)
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
      const minutes = Number((this.cfg.idleMinutes || {})[k] || (k === 'coder' ? 10 : 0)) // the coder (1-2 GB) goes after 10 idle minutes
      if (this.inst[k] && minutes > 0 && now - this.lastUsed[k] > minutes * 60000) {
        this.job(k, 'unload', async () => {
          if (this.inst[k] && Date.now() - this.lastUsed[k] > minutes * 60000) await this._unload(k)
        }).catch(() => {})
      }
    }
  }
}

/** One speech-to-text run of 16 kHz mono Int16 PCM -> the text as Whisper wrote it. */
async function asrText (asr, pcm) {
  const r = await asr.run(pcm)
  let text = ''
  await r.onUpdate((out) => {
    for (const s of Array.isArray(out) ? out : [out]) if (s && typeof s.text === 'string') text += s.text
  }).await()
  return text
}

/** One pass of sentence vectors (up to 64 texts), L2-normalised. */
async function embedPass (m, texts) {
  const r = await m.run(texts)
  let out = await r.await()
  const isVec = (x) => ArrayBuffer.isView(x) || (Array.isArray(x) && typeof x[0] === 'number')
  while (Array.isArray(out) && out.length && !isVec(out[0])) out = out[0]
  const vectors = []
  for (const v of out) {
    let n = 0
    for (let k = 0; k < v.length; k++) n += v[k] * v[k]
    n = Math.sqrt(n) || 1
    const a = new Array(v.length)
    for (let k = 0; k < v.length; k++) a[k] = Math.round((v[k] / n) * 1e5) / 1e5
    vectors.push(a)
  }
  return vectors
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
  // content as a list of parts (OpenCode, the compat layers): the text parts, joined
  const content = Array.isArray(m.content) ? m.content.map((p) => typeof p === 'string' ? p : (p && (p.text != null ? p.text : p.content)) || '').join('') : m.content
  const out = { role: m.role, content: content == null ? '' : content }
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

/**
 * A small coder repeats the call that just worked (Qwen2.5-Coder 0.5B wrote hello.txt 29 times, 2026-11-13): when the
 * new tool calls are exactly the previous assistant turn's (names and arguments) and that turn's results were no
 * error, the turn ends with a short answer instead, so the harness stops.
 */
function stopRepeat (messages, res) {
  if (!res.tool_calls.length || !Array.isArray(messages)) return
  let i = messages.length - 1
  const results = []
  while (i >= 0 && messages[i].role === 'tool') { results.push(String(messages[i].content || '')); i-- }
  const prev = i >= 0 ? messages[i] : null
  if (!prev || prev.role !== 'assistant' || !Array.isArray(prev.tool_calls) || !results.length) return
  const sig = (name, args) => name + ' ' + JSON.stringify(typeof args === 'string' ? safeJson(args) : (args || {}))
  const before = prev.tool_calls.map((c) => sig((c.function || c).name, (c.function || c).arguments)).sort().join('\n')
  const now = res.tool_calls.map((c) => sig(c.name, c.arguments)).sort().join('\n')
  if (before !== now || results.some((r) => /^\s*(error|ERROR)\b|failed|not found/i.test(r))) return
  res.content = (res.content ? res.content + '\n' : '') + 'Done.'
  res.tool_calls = []
  res.repeatStopped = true
}

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

module.exports = { Engines, thinkStripper, parseAssistant, cleanTranscript, toQvacTool, toQvacMessage }
