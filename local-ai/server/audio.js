'use strict'
// Audio helpers for the AI service: WAV in/out, resampling to 16 kHz for speech-to-text, multipart bodies.

/** Parses a PCM WAV (8/16/24/32-bit int or 32-bit float, any channels) into mono Int16 at its own rate. */
function parseWav (buf) {
  if (buf.length < 44 || buf.toString('ascii', 0, 4) !== 'RIFF' || buf.toString('ascii', 8, 12) !== 'WAVE') {
    throw new Error('not a WAV file (RIFF/WAVE header missing)')
  }
  let off = 12; let fmt = null; let data = null
  while (off + 8 <= buf.length) {
    const id = buf.toString('ascii', off, off + 4)
    const size = buf.readUInt32LE(off + 4)
    const body = off + 8
    if (id === 'fmt ') {
      fmt = { format: buf.readUInt16LE(body), channels: buf.readUInt16LE(body + 2), rate: buf.readUInt32LE(body + 4), bits: buf.readUInt16LE(body + 14) }
      if (fmt.format === 0xFFFE && size >= 26) fmt.format = buf.readUInt16LE(body + 24) // WAVE_FORMAT_EXTENSIBLE
    } else if (id === 'data') {
      data = buf.subarray(body, Math.min(buf.length, body + size))
      break
    }
    off = body + size + (size & 1)
  }
  if (!fmt || !data) throw new Error('WAV without fmt or data chunk')
  const bytes = fmt.bits / 8
  const frames = Math.floor(data.length / (bytes * fmt.channels))
  const out = new Int16Array(frames)
  for (let i = 0; i < frames; i++) {
    let sum = 0
    for (let c = 0; c < fmt.channels; c++) {
      const o = (i * fmt.channels + c) * bytes
      let v
      if (fmt.format === 3 && fmt.bits === 32) v = data.readFloatLE(o)
      else if (fmt.bits === 16) v = data.readInt16LE(o) / 32768
      else if (fmt.bits === 8) v = (data[o] - 128) / 128
      else if (fmt.bits === 24) v = ((data[o] | (data[o + 1] << 8) | (data[o + 2] << 16)) << 8 >> 8) / 8388608
      else if (fmt.bits === 32) v = data.readInt32LE(o) / 2147483648
      else throw new Error('unsupported WAV sample format: ' + fmt.format + '/' + fmt.bits)
      sum += v
    }
    const s = sum / fmt.channels
    out[i] = Math.max(-32768, Math.min(32767, Math.round(s * 32767)))
  }
  return { pcm: out, rate: fmt.rate }
}

/** Linear resampler, mono Int16. Speech-to-text wants 16 kHz. */
function resample (pcm, from, to) {
  if (from === to) return pcm
  const n = Math.floor(pcm.length * to / from)
  const out = new Int16Array(n)
  const r = from / to
  for (let i = 0; i < n; i++) {
    const x = i * r; const i0 = Math.floor(x); const i1 = Math.min(i0 + 1, pcm.length - 1); const f = x - i0
    out[i] = Math.round(pcm[i0] * (1 - f) + pcm[i1] * f)
  }
  return out
}

/** Mono 16-bit PCM WAV. */
function toWav (pcm, rate) {
  const b = Buffer.alloc(44 + pcm.length * 2)
  b.write('RIFF', 0, 'ascii'); b.writeUInt32LE(36 + pcm.length * 2, 4); b.write('WAVE', 8, 'ascii'); b.write('fmt ', 12, 'ascii')
  b.writeUInt32LE(16, 16); b.writeUInt16LE(1, 20); b.writeUInt16LE(1, 22); b.writeUInt32LE(rate, 24)
  b.writeUInt32LE(rate * 2, 28); b.writeUInt16LE(2, 32); b.writeUInt16LE(16, 34); b.write('data', 36, 'ascii')
  b.writeUInt32LE(pcm.length * 2, 40)
  for (let i = 0; i < pcm.length; i++) b.writeInt16LE(pcm[i], 44 + i * 2)
  return b
}

/** Raw little-endian 16-bit mono PCM (Content-Type audio/pcm or audio/l16) -> Int16Array. */
function fromPcm16 (buf) {
  const out = new Int16Array(Math.floor(buf.length / 2))
  for (let i = 0; i < out.length; i++) out[i] = buf.readInt16LE(i * 2)
  return out
}

/** multipart/form-data -> { fields: {name: string}, files: {name: Buffer} } (enough for OpenAI-style uploads). */
function parseMultipart (buf, contentType) {
  const m = /boundary=(?:"([^"]+)"|([^;]+))/i.exec(contentType || '')
  if (!m) throw new Error('multipart without boundary')
  const boundary = Buffer.from('--' + (m[1] || m[2]).trim())
  const fields = {}; const files = {}
  let pos = buf.indexOf(boundary)
  while (pos >= 0) {
    let start = pos + boundary.length
    if (buf[start] === 45 && buf[start + 1] === 45) break // closing "--"
    if (buf[start] === 13) start += 2
    const headEnd = buf.indexOf('\r\n\r\n', start)
    if (headEnd < 0) break
    const head = buf.toString('utf8', start, headEnd)
    const next = buf.indexOf(boundary, headEnd + 4)
    if (next < 0) break
    const body = buf.subarray(headEnd + 4, next - 2) // minus CRLF before the boundary
    const name = (/name="([^"]*)"/i.exec(head) || [])[1]
    if (name) {
      if (/filename="/i.test(head)) files[name] = body
      else fields[name] = body.toString('utf8')
    }
    pos = next
  }
  return { fields, files }
}

module.exports = { parseWav, resample, toWav, fromPcm16, parseMultipart }
