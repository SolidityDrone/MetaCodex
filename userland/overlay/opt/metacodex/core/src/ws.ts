// Minimal WebSocket server (RFC 6455) for change notifications: text frames out, ping/pong/close in.
// No dependency: Node has a WebSocket client built in, not a server.
import { createHash } from "node:crypto";
import type { IncomingMessage } from "node:http";
import type { Duplex } from "node:stream";

const GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

export class WsHub {
  private clients = new Set<Duplex>();

  get size(): number {
    return this.clients.size;
  }

  /// Completes the handshake (the caller has already checked the path and the token).
  accept(req: IncomingMessage, socket: Duplex): void {
    const key = req.headers["sec-websocket-key"];
    if (typeof key !== "string" || req.headers.upgrade?.toLowerCase() !== "websocket") {
      socket.end("HTTP/1.1 400 Bad Request\r\n\r\n");
      return;
    }
    const accept = createHash("sha1").update(key + GUID).digest("base64");
    socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
    this.clients.add(socket);
    let buf = Buffer.alloc(0);
    socket.on("data", (chunk: Buffer) => {
      buf = Buffer.concat([buf, chunk]);
      for (;;) {
        const f = parseFrame(buf);
        if (!f) break;
        buf = buf.subarray(f.length);
        if (f.opcode === 0x8) { socket.end(frame(0x8, Buffer.alloc(0))); this.clients.delete(socket); return; }
        if (f.opcode === 0x9) socket.write(frame(0xA, f.payload)); // ping -> pong
      }
      if (buf.length > 1 << 20) socket.destroy(); // nobody needs to send us a megabyte
    });
    const drop = () => this.clients.delete(socket);
    socket.on("close", drop);
    socket.on("error", drop);
  }

  broadcast(obj: unknown): void {
    const f = frame(0x1, Buffer.from(JSON.stringify(obj)));
    for (const c of this.clients) {
      if (c.writable) c.write(f);
    }
  }

  closeAll(): void {
    for (const c of this.clients) c.destroy();
    this.clients.clear();
  }
}

function frame(opcode: number, payload: Buffer): Buffer {
  const n = payload.length;
  const head = n < 126 ? Buffer.alloc(2) : n < 65536 ? Buffer.alloc(4) : Buffer.alloc(10);
  head[0] = 0x80 | opcode;
  if (n < 126) head[1] = n;
  else if (n < 65536) { head[1] = 126; head.writeUInt16BE(n, 2); }
  else { head[1] = 127; head.writeBigUInt64BE(BigInt(n), 2); }
  return Buffer.concat([head, payload]);
}

function parseFrame(b: Buffer): { opcode: number; payload: Buffer; length: number } | null {
  if (b.length < 2) return null;
  const opcode = b[0] & 0x0f;
  const masked = (b[1] & 0x80) !== 0;
  let len = b[1] & 0x7f;
  let off = 2;
  if (len === 126) { if (b.length < 4) return null; len = b.readUInt16BE(2); off = 4; }
  else if (len === 127) { if (b.length < 10) return null; len = Number(b.readBigUInt64BE(2)); off = 10; }
  const maskOff = off;
  if (masked) off += 4;
  if (b.length < off + len) return null;
  const payload = Buffer.from(b.subarray(off, off + len));
  if (masked) for (let i = 0; i < len; i++) payload[i] ^= b[maskOff + (i & 3)];
  return { opcode, payload, length: off + len };
}
