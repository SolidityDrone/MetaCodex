'use strict'
// Model catalogue and installer. Models are never inside the APK: they are downloaded on request
// (Hugging Face over HTTPS, or the QVAC registry over Hyperswarm for models only published there),
// checked against the sha256 recorded in the QVAC model registry, and kept in <data>/models.
const fs = require('bare-fs')
const path = require('bare-path')

const CATALOGUE = {
  asr: {
    id: 'whisper-base-q8_0',
    name: 'Whisper base, multilingual (q8_0)',
    file: 'ggml-base-q8_0.bin',
    bytes: 81768585,
    sha256: 'c577b9a86e7e048a0b7eada054f4dd79a56bbfa911fbdacf900ac5b567cbb7d9',
    url: 'https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-base-q8_0.bin'
  },
  llm: {
    id: 'qwen3-0.6b-q4_0',
    name: 'Qwen3 0.6B instruct, tool calling (q4_0)',
    file: 'Qwen3-0.6B-Q4_0.gguf',
    bytes: 382156480,
    sha256: '33bcc57074ec7b6eada5a90651ee546ec0c2b271002c22baf9f1b2dd1e8f75cb',
    url: 'https://huggingface.co/unsloth/Qwen3-0.6B-GGUF/resolve/50968a4468ef4233ed78cd7c3de230dd1d61a56b/Qwen3-0.6B-Q4_0.gguf'
  },
  // voice commands decided Jev-style (VoiceDecider in the app): sentence vectors compared with the commands' examples;
  // multilingual (English, Italian, ~100 languages), 118M parameters, ~10 ms a sentence on 2 CPU threads
  embed: {
    id: 'multilingual-e5-small-q8_0',
    name: 'multilingual-e5-small, command decisions (q8_0)',
    file: 'multilingual-e5-small-q8_0.gguf',
    bytes: 132439008,
    sha256: 'e011debc1208e31bf7b6aebee2d9fc8bd2ca11694a77ed66ac9d0c9d0a877c93',
    url: 'https://huggingface.co/TwinSunsLLC/multilingual-e5-small-gguf/resolve/b6cac9615d4e/multilingual-e5-small-q8_0.gguf'
  },
  tts: {
    id: 'supertonic3-q4_0',
    name: 'Supertonic 3, 31 languages (q4_0)',
    file: 'supertonic3-q4_0.gguf',
    bytes: 84658368,
    sha256: 'e3f3edc816b3fb4d8dae0067b2cc7e3dbeaac1e78e21293c803bd57011425fb7',
    registry: 'qvac_models_compiled/ggml/supertonic/2026-06-24/supertonic3-q4_0.gguf'
  }
}

class Models {
  constructor ({ dir, registryDir, log }) {
    this.dir = dir
    this.registryDir = registryDir
    this.log = log
    this.jobs = {} // kind -> { state, received, total, error }
    fs.mkdirSync(dir, { recursive: true })
  }

  file (kind) { return path.join(this.dir, CATALOGUE[kind].file) }

  /** Installed = right size and a sha256 marker from a verified install (or a verified adb push). */
  installed (kind) {
    const m = CATALOGUE[kind]
    try {
      if (fs.statSync(this.file(kind)).size !== m.bytes) return false
      return fs.readFileSync(this.file(kind) + '.sha256', 'utf8').trim() === m.sha256
    } catch { return false }
  }

  status () {
    const out = {}
    for (const kind of Object.keys(CATALOGUE)) {
      const m = CATALOGUE[kind]
      const job = this.jobs[kind]
      out[kind] = { id: m.id, name: m.name, sizeMB: Math.round(m.bytes / 1048576), installed: this.installed(kind) }
      if (job) Object.assign(out[kind], { install: job.state, progress: job.total ? +(job.received / job.total).toFixed(3) : 0, error: job.error || undefined })
    }
    return out
  }

  /** Starts installing the missing models of the given kinds; returns immediately (progress in status). */
  install (kinds) {
    for (const kind of kinds) {
      if (!CATALOGUE[kind]) throw new Error('unknown model kind: ' + kind)
      if (this.installed(kind)) continue
      const job = this.jobs[kind]
      if (job && (job.state === 'downloading' || job.state === 'verifying')) continue
      this.jobs[kind] = { state: 'downloading', received: 0, total: CATALOGUE[kind].bytes }
      this._install(kind).catch((e) => {
        this.jobs[kind] = { state: 'failed', received: 0, total: 0, error: String(e.message || e) }
        this.log('install ' + kind + ' failed: ' + (e.stack || e))
      })
    }
  }

  /** A file pushed by hand (adb, dev): verify it once and write the marker. */
  async adopt (kind) {
    const m = CATALOGUE[kind]
    const f = this.file(kind)
    if (!fs.existsSync(f)) return false
    const sum = await sha256File(f)
    if (sum !== m.sha256) { this.log(`adopt ${kind}: sha256 mismatch ${sum}`); return false }
    fs.writeFileSync(f + '.sha256', sum + '\n')
    return true
  }

  remove (kinds) {
    for (const kind of kinds) {
      for (const suffix of ['', '.sha256', '.part']) {
        try { fs.unlinkSync(this.file(kind) + suffix) } catch {}
      }
      delete this.jobs[kind]
    }
  }

  async _install (kind) {
    const m = CATALOGUE[kind]
    const final = this.file(kind)
    const part = final + '.part'
    const job = this.jobs[kind]
    const t0 = Date.now()
    if (fs.existsSync(final) && fs.statSync(final).size === m.bytes && await this.adopt(kind)) {
      job.state = 'done'
      return
    }
    if (m.url) await downloadHttps(m.url, part, m.bytes, (n) => { job.received = n })
    else await this._fromRegistry(m, part, job)
    job.state = 'verifying'
    const sum = await sha256File(part)
    if (sum !== m.sha256) {
      try { fs.unlinkSync(part) } catch {}
      throw new Error(`sha256 mismatch for ${m.file}: ${sum}`)
    }
    fs.renameSync(part, final)
    fs.writeFileSync(final + '.sha256', sum + '\n')
    job.state = 'done'
    job.received = job.total
    this.log(`installed ${kind} ${m.file} in ${((Date.now() - t0) / 1000).toFixed(1)} s`)
  }

  async _fromRegistry (m, part, job) {
    const { QVACRegistryClient } = require('@qvac/registry-client')
    fs.mkdirSync(this.registryDir, { recursive: true })
    const client = new QVACRegistryClient({ storage: this.registryDir })
    const timer = setInterval(() => {
      try { job.received = fs.statSync(part).size } catch {}
    }, 500)
    try {
      await client.ready()
      await client.downloadModel(m.registry, 's3', { outputFile: part, timeout: 900000 })
    } finally {
      clearInterval(timer)
      try { await client.close() } catch {}
    }
  }
}

/** GET with redirects and resume (Range) into `dest`; progress(bytesOnDisk). */
function downloadHttps (url, dest, expected, progress) {
  const https = require('bare-https')
  return new Promise((resolve, reject) => {
    const attempt = (u, redirects, retries) => {
      let settled = false
      let have = 0
      try { have = fs.statSync(dest).size } catch {}
      if (have >= expected) return resolve()
      const req = https.request(u, { method: 'GET', headers: have > 0 ? { range: 'bytes=' + have + '-' } : {} }, (res) => {
        const code = res.statusCode
        if (code >= 300 && code < 400 && res.headers.location) {
          res.resume()
          if (redirects <= 0) return reject(new Error('too many redirects'))
          return attempt(new URL(res.headers.location, u).href, redirects - 1, retries)
        }
        if (code !== 200 && code !== 206) {
          res.resume()
          return reject(new Error('HTTP ' + code + ' for ' + u))
        }
        const out = fs.createWriteStream(dest, { flags: code === 206 ? 'a' : 'w' })
        let n = code === 206 ? have : 0
        res.on('data', (chunk) => { n += chunk.length; progress(n) })
        res.on('error', fail)
        out.on('error', fail)
        out.on('close', () => {
          if (n >= expected) { settled = true; resolve() } else fail(new Error('connection closed at ' + n + ' of ' + expected + ' bytes'))
        })
        res.pipe(out)
      })
      function fail (e) {
        if (settled) return
        settled = true
        if (retries > 0) setTimeout(() => attempt(url, 5, retries - 1), 2000)
        else reject(e)
      }
      req.on('error', fail)
      req.end()
    }
    attempt(url, 5, 5)
  })
}

/** sha256 of a file, streamed through libsodium (sodium-native ships with the registry client). */
function sha256File (file) {
  const sodium = require('sodium-native')
  return new Promise((resolve, reject) => {
    const state = Buffer.alloc(sodium.crypto_hash_sha256_STATEBYTES)
    sodium.crypto_hash_sha256_init(state)
    const s = fs.createReadStream(file)
    s.on('data', (chunk) => sodium.crypto_hash_sha256_update(state, chunk))
    s.on('error', reject)
    s.on('end', () => {
      const out = Buffer.alloc(sodium.crypto_hash_sha256_BYTES)
      sodium.crypto_hash_sha256_final(state, out)
      resolve(out.toString('hex'))
    })
  })
}

module.exports = { Models, CATALOGUE, sha256File }
