import assert from "node:assert/strict";
import { test } from "node:test";
import { HANDSHAKE, PING, RS } from "./signalr.js";
import { createConnection, createVisitor, joinRetryDelay, reconnectDelay } from "./session.js";

const CHANNEL = "wmsfo-api-dev:location";
const PARAMS = { hubUrl: "wss://<gateway-domain>/hub", channel: CHANNEL, cdnBaseUrl: "https://<cdn-domain>", pollFraction: 1, pollIntervalMs: 5000, allowedLagMs: 2000 };
const T0 = Date.parse("2026-12-22T01:00:00.000Z");

// The metrics as plain values: counters sum, trends and rates keep every sample.
function fakeMetrics() {
  const counters = {};
  const samples = {};
  return {
    counters,
    samples,
    add(name, value) {
      if (typeof value === "number" && !/_ms$/.test(name)) counters[name] = (counters[name] ?? 0) + value;
      else (samples[name] ??= []).push(value);
    },
  };
}

function harness(params = PARAMS) {
  const clock = { t: T0 };
  const metrics = fakeMetrics();
  const visitor = createVisitor({ params, metrics, now: () => clock.t, polls: true });
  const sockets = [];
  const connect = () => {
    const socket = { sent: [], closes: 0, send: (text) => socket.sent.push(text), close: () => (socket.closes += 1) };
    const connection = createConnection(visitor, socket);
    sockets.push({ socket, connection });
    return { socket, connection };
  };
  return { clock, metrics, visitor, connect };
}

const frame = (...messages) => messages.map((m) => JSON.stringify(m) + RS).join("");
const channelEvent = (event, data, channel = CHANNEL) => ({ type: 1, target: "ChannelEvent", arguments: [{ channel, event, data }] });
const join = (id) => JSON.stringify({ type: 1, invocationId: String(id), target: "JoinChannel", arguments: [CHANNEL] }) + RS;
const live = (seq, publishedAtMs, extra = {}) => ({
  schemaVersion: 1,
  eventId: 7,
  eventStatusId: 3,
  pollIntervalMs: 5000,
  seq,
  publishedAt: new Date(publishedAtMs).toISOString(),
  ...extra,
});

test("the backoffs", () => {
  assert.deepEqual([1, 2, 3, 4, 9].map(reconnectDelay), [1000, 2000, 3000, 5000, 5000]);
  assert.deepEqual([1, 2, 3, 4, 5, 9].map(joinRetryDelay), [10000, 1000, 2000, 3000, 5000, 5000]);
});

test("open, handshake, join, ack, location events, a gap, an eviction, a re-join", () => {
  const { clock, metrics, visitor, connect } = harness();
  const { socket, connection } = connect();

  connection.open();
  assert.deepEqual(socket.sent, [HANDSHAKE]);
  assert.equal(metrics.counters.ws_opened, 1);

  clock.t += 40;
  connection.message("{}" + RS);
  assert.equal(socket.sent[1], join(1));
  assert.equal(connection.joined, false);

  // the completion and the caller-only ack in one frame; the ack is the join
  clock.t += 60;
  connection.message(frame({ type: 3, invocationId: "1" }, channelEvent("joined", { channel: CHANNEL })));
  assert.equal(connection.joined, true);
  assert.equal(metrics.counters.joins_ok, 1);
  assert.deepEqual(metrics.samples.join_ms, [100]);

  // events on other channels and other events are ignored
  connection.message(frame(channelEvent("location", live(1, clock.t), "other:location"), channelEvent("cookie", {})));
  assert.equal(metrics.counters.events, undefined);

  clock.t += 1000;
  connection.message(frame(channelEvent("location", live(10, clock.t - 150))));
  clock.t += 1000;
  connection.message(frame(channelEvent("location", live(11, clock.t - 2500))));
  clock.t += 1000;
  connection.message(frame(channelEvent("location", live(14, clock.t - 20, { pollIntervalMs: 4000 }))));
  assert.equal(metrics.counters.events, 3);
  assert.deepEqual(metrics.samples.event_lag_ms, [150, 2500, 20]);
  assert.deepEqual(metrics.samples.events_on_time, [true, false, true]);
  assert.equal(metrics.counters.seq_gaps, 2);
  assert.equal(visitor.live.seq, 14);
  assert.equal(visitor.live.pollIntervalMs, 4000);
  assert.equal(visitor.cadence(clock.t), 4000);

  // auth_expired: join again at once
  const before = socket.sent.length;
  connection.message(frame(channelEvent("channelEvicted", { channel: CHANNEL, reason: "auth_expired" })));
  assert.equal(metrics.counters.evictions, 1);
  assert.equal(connection.joined, false);
  assert.equal(socket.sent[before], join(2));
  // not joined: quiet while live
  assert.equal(visitor.cadence(clock.t), 2000);

  clock.t += 30;
  connection.message(frame({ type: 3, invocationId: "2" }, channelEvent("joined", { channel: CHANNEL })));
  assert.equal(metrics.counters.joins_ok, 2);
  clock.t += 1000;
  connection.message(frame(channelEvent("location", live(15, clock.t - 10))));
  assert.equal(metrics.counters.seq_gaps, 2);
  assert.equal(metrics.counters.events, 4);

  // the session end closes cleanly
  connection.end();
  assert.equal(socket.closes, 1);
  connection.closed();
  assert.equal(metrics.counters.ws_closed, 1);
  assert.equal(metrics.counters.ws_errors, undefined);
});

test("keepalive every 15 s, and a server silent for 30 s is dropped", () => {
  const { clock, metrics, connect } = harness();
  const { socket, connection } = connect();
  connection.open();
  connection.message("{}" + RS);
  const sent = socket.sent.length;

  clock.t += 14999;
  connection.tick();
  assert.equal(socket.sent.length, sent);
  clock.t += 1;
  connection.tick();
  assert.equal(socket.sent.at(-1), PING);

  // server pings keep the connection alive
  clock.t += 14000;
  connection.message('{"type":6}' + RS);
  clock.t += 16000;
  connection.tick();
  assert.equal(socket.closes, 0);

  clock.t += 14001;
  connection.tick();
  assert.equal(socket.closes, 1);
  assert.equal(metrics.counters.ws_errors, 1);
  connection.closed();
  assert.equal(metrics.counters.ws_errors, 1);
  assert.equal(metrics.counters.ws_closed, 1);
});

test("a refused handshake fails the connection", () => {
  const { metrics, connect } = harness();
  const { socket, connection } = connect();
  connection.open();
  connection.message('{"error":"protocol not supported"}' + RS);
  assert.equal(socket.closes, 1);
  assert.equal(socket.sent.length, 1);
  assert.equal(metrics.counters.ws_errors, 1);
  assert.ok(connection.failed);
});

test("a denied join retries after 10 s, then 1, 2, 3, 5 s", () => {
  const { clock, metrics, connect } = harness();
  const { socket, connection } = connect();
  connection.open();
  connection.message("{}" + RS);

  const waits = [];
  for (let id = 1; id <= 5; id++) {
    assert.equal(socket.sent.at(-1), join(id));
    connection.message(frame({ type: 3, invocationId: String(id), error: "denied" }));
    const start = clock.t;
    while (socket.sent.at(-1) !== join(id + 1)) {
      clock.t += 250;
      connection.message('{"type":6}' + RS);
      connection.tick();
    }
    waits.push(clock.t - start);
  }
  assert.equal(metrics.counters.joins_denied, 5);
  assert.deepEqual(waits, [10000, 1000, 2000, 3000, 5000]);
});

test("service_removed retries the join every 5 s", () => {
  const { clock, metrics, connect } = harness();
  const { socket, connection } = connect();
  connection.open();
  connection.message("{}" + RS);
  connection.message(frame({ type: 3, invocationId: "1" }, channelEvent("joined", { channel: CHANNEL })));
  connection.message(frame(channelEvent("channelEvicted", { channel: CHANNEL, reason: "service_removed" })));
  assert.equal(metrics.counters.evictions, 1);
  assert.equal(socket.sent.at(-1), join(1));

  clock.t += 4999;
  connection.tick();
  assert.equal(socket.sent.at(-1), join(1));
  clock.t += 1;
  connection.tick();
  assert.equal(socket.sent.at(-1), join(2));

  connection.message(frame({ type: 3, invocationId: "2", error: "denied" }));
  clock.t += 5000;
  connection.tick();
  assert.equal(socket.sent.at(-1), join(3));
});

test("an unexpected close is an error; the next connection re-joins and counts the gap it missed", () => {
  const { clock, metrics, visitor, connect } = harness();
  const first = connect();
  first.connection.open();
  first.connection.message("{}" + RS);
  first.connection.message(frame(channelEvent("joined", { channel: CHANNEL }), channelEvent("location", live(3, clock.t))));
  first.connection.closed();
  assert.equal(metrics.counters.ws_errors, 1);
  assert.equal(metrics.counters.ws_closed, 1);
  assert.equal(visitor.joined, false);

  const second = connect();
  second.connection.open();
  second.connection.message("{}" + RS);
  assert.equal(second.socket.sent.at(-1), join(1));
  second.connection.message(frame(channelEvent("joined", { channel: CHANNEL }), channelEvent("location", live(6, clock.t))));
  assert.equal(metrics.counters.joins_ok, 2);
  assert.equal(metrics.counters.ws_opened, 2);
  assert.equal(metrics.counters.seq_gaps, 2);
});

test("a server close message fails the connection", () => {
  const { metrics, connect } = harness();
  const { socket, connection } = connect();
  connection.open();
  connection.message("{}" + RS);
  connection.message('{"type":7,"error":"server shutting down","allowReconnect":true}' + RS);
  assert.equal(socket.closes, 1);
  assert.equal(metrics.counters.ws_errors, 1);
});

test("polls follow the cadence, never overlap, and count errors", () => {
  const { clock, metrics, visitor } = harness();
  const urls = [];
  let answer = { status: 200, durationMs: 42, body: JSON.stringify(live(5, clock.t, { eventStatusId: 2, pollIntervalMs: 8000 })) };
  const get = (url) => {
    urls.push(url);
    clock.t += 300;
    return answer;
  };

  assert.ok(visitor.pollDue(clock.t));
  visitor.poll(get);
  assert.deepEqual(urls, ["https://<cdn-domain>/live/location.json"]);
  assert.equal(metrics.counters.polls, 1);
  assert.deepEqual(metrics.samples.poll_ms, [42]);
  assert.equal(visitor.live.pollIntervalMs, 8000);

  // the interval runs from the end of the last fetch
  clock.t += 7999;
  assert.ok(!visitor.pollDue(clock.t));
  clock.t += 1;
  assert.ok(visitor.pollDue(clock.t));

  answer = { status: 503, durationMs: 12, body: "" };
  visitor.poll(get);
  assert.equal(metrics.counters.poll_errors, 1);
  visitor.poll(() => {
    throw new Error("network");
  });
  assert.equal(metrics.counters.poll_errors, 2);
  assert.equal(metrics.counters.polls, 3);
  assert.equal(visitor.live.seq, 5);

  // live and nothing from the hub: quiet, half the interval
  answer = { status: 200, durationMs: 9, body: JSON.stringify(live(6, clock.t + 300, { eventStatusId: 3, pollIntervalMs: 8000 })) };
  visitor.poll(get);
  assert.equal(visitor.cadence(clock.t), 4000);
  clock.t += 4000;
  assert.ok(visitor.pollDue(clock.t));
});

test("a user outside the poll fraction never polls", () => {
  const visitor = createVisitor({ params: PARAMS, metrics: fakeMetrics(), now: () => T0, polls: false });
  assert.ok(!visitor.pollDue(T0));
});
