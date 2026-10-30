import assert from "node:assert/strict";
import { test } from "node:test";
import {
  HANDSHAKE,
  PING,
  RS,
  envelopeOf,
  handshakeError,
  invocation,
  isChannelEvent,
  isClose,
  isCompletion,
  isPing,
  splitFrames,
} from "./signalr.js";

test("the handshake and the ping end with the record separator", () => {
  assert.equal(RS, "\u001e");
  assert.equal(HANDSHAKE, '{"protocol":"json","version":1}\u001e');
  assert.equal(PING, '{"type":6}\u001e');
});

test("a frame splits into its messages and the trailing empty piece is ignored", () => {
  assert.deepEqual(splitFrames("{}\u001e"), [{}]);
  assert.deepEqual(splitFrames('{"type":6}\u001e{"type":3,"invocationId":"1"}\u001e'), [{ type: 6 }, { type: 3, invocationId: "1" }]);
  assert.deepEqual(splitFrames(""), []);
  assert.deepEqual(splitFrames("not json\u001e{}\u001e"), [{}]);
});

test("JoinChannel is an invocation with a string id", () => {
  assert.equal(
    invocation("JoinChannel", ["svc:location"], 1),
    '{"type":1,"invocationId":"1","target":"JoinChannel","arguments":["svc:location"]}\u001e',
  );
});

test("the handshake answer is {} or an error", () => {
  assert.equal(handshakeError({}), null);
  assert.equal(handshakeError({ error: "nope" }), "nope");
  assert.match(handshakeError(null), /not an object/);
});

test("message kinds", () => {
  assert.ok(isCompletion({ type: 3, invocationId: "2" }));
  assert.ok(isCompletion({ type: 3, invocationId: "2" }, 2));
  assert.ok(!isCompletion({ type: 3, invocationId: "2" }, 3));
  assert.ok(isPing({ type: 6 }));
  assert.ok(!isPing({ type: 1 }));
  assert.ok(isClose({ type: 7 }));
  const event = { type: 1, target: "ChannelEvent", arguments: [{ channel: "svc:location", event: "joined", data: {} }] };
  assert.ok(isChannelEvent(event));
  assert.deepEqual(envelopeOf(event), { channel: "svc:location", event: "joined", data: {} });
  assert.ok(!isChannelEvent({ type: 1, target: "Other", arguments: [{}] }));
  assert.ok(!isChannelEvent({ type: 1, target: "ChannelEvent", arguments: [] }));
  assert.equal(envelopeOf({ type: 6 }), null);
});
