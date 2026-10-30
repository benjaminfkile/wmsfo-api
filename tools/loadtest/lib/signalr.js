// The SignalR JSON hub protocol as the site speaks it (docs/contracts.md 2.2):
// WebSockets only, negotiation skipped, every message one JSON text ended by
// the record separator. Pure: builds and parses text, does no I/O.

export const RS = "\u001e";

export const MessageType = { Invocation: 1, Completion: 3, Ping: 6, Close: 7 };

export const HANDSHAKE = JSON.stringify({ protocol: "json", version: 1 }) + RS;

export const PING = JSON.stringify({ type: MessageType.Ping }) + RS;

// The messages of one frame, in order. A frame may carry several, each ended by
// the separator; the empty piece after the last one is ignored, and so is a
// piece that is not JSON.
export function splitFrames(text) {
  const out = [];
  for (const piece of String(text).split(RS)) {
    if (piece === "") continue;
    try {
      out.push(JSON.parse(piece));
    } catch {
      // not a message
    }
  }
  return out;
}

export function invocation(target, args, id) {
  return JSON.stringify({ type: MessageType.Invocation, invocationId: String(id), target, arguments: args }) + RS;
}

// The handshake answer is `{}`, or `{ "error": "..." }` when the server refuses.
export function handshakeError(message) {
  if (!message || typeof message !== "object") return "handshake answer is not an object";
  return typeof message.error === "string" ? message.error : null;
}

export function isCompletion(message, id) {
  return !!message && message.type === MessageType.Completion && (id === undefined || message.invocationId === String(id));
}

export function isPing(message) {
  return !!message && message.type === MessageType.Ping;
}

export function isClose(message) {
  return !!message && message.type === MessageType.Close;
}

// A server invocation of the one client method, `ChannelEvent(envelope)`.
export function isChannelEvent(message) {
  return (
    !!message &&
    message.type === MessageType.Invocation &&
    message.target === "ChannelEvent" &&
    Array.isArray(message.arguments) &&
    !!message.arguments[0] &&
    typeof message.arguments[0] === "object"
  );
}

// The `{ channel, event, data }` envelope of a ChannelEvent invocation.
export function envelopeOf(message) {
  return isChannelEvent(message) ? message.arguments[0] : null;
}
