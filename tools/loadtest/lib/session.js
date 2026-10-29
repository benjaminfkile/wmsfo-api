// One anonymous tracker visitor (docs/contracts.md 2.3 and 1.9), with no I/O
// of its own: the caller hands it a socket ({ send(text), close() }), a GET
// function, a clock, and a metrics sink ({ add(name, value) }), and drives it
// with open, message, tick, and closed. scenario.js wires k6's socket and http
// to it; the specs wire fakes.

import { HANDSHAKE, PING, envelopeOf, handshakeError, invocation, isClose, isCompletion, splitFrames } from "./signalr.js";
import { lagMs, pollCadence, seqRegressed } from "./live.js";

export const KEEPALIVE_MS = 15000;
export const SERVER_TIMEOUT_MS = 30000;
export const SERVICE_REMOVED_RETRY_MS = 5000;

// The site's start loop: 1 s, 2 s, 3 s, then every 5 s. `attempt` counts from 1.
export function reconnectDelay(attempt) {
  return [1000, 2000, 3000][attempt - 1] ?? 5000;
}

// A denied join waits 10 s, then the 1, 2, 3, 5 s backoff. `attempt` counts
// the consecutive failed joins from 1.
export function joinRetryDelay(attempt) {
  return attempt <= 1 ? 10000 : ([1000, 2000, 3000][attempt - 2] ?? 5000);
}

// What outlives a connection: the newest live object, the last seq a hub event
// carried, and the poll loop.
export function createVisitor({ params, metrics, now, polls }) {
  const visitor = {
    params,
    metrics,
    now,
    polls: !!polls,
    live: null,
    prevSeq: null,
    prevEventId: null,
    lastHubEventAt: null,
    joined: false,
    lastPollDoneAt: null,
    reconnects: 0,

    // Keeps the newer of the held object and `live` by publishedAt (ordinal,
    // the canonical format makes that time order).
    remember(live) {
      if (!live || typeof live !== "object") return;
      if (!visitor.live || String(live.publishedAt) > String(visitor.live.publishedAt)) visitor.live = live;
    },

    cadence(at) {
      return pollCadence(visitor.live, visitor.joined ? visitor.lastHubEventAt : null, at, params.pollIntervalMs);
    },

    pollDue(at) {
      if (!visitor.polls) return false;
      return visitor.lastPollDoneAt === null || at - visitor.lastPollDoneAt >= visitor.cadence(at);
    },

    // One poll with `get(url)` answering { status, durationMs, body }, status 0
    // for a network failure. The next one is due a cadence after this one ends.
    poll(get) {
      let res;
      try {
        res = get(`${params.cdnBaseUrl}/live/location.json`);
      } catch {
        res = { status: 0, durationMs: null, body: null };
      }
      metrics.add("polls", 1);
      if (typeof res.durationMs === "number") metrics.add("poll_ms", res.durationMs);
      if (!(res.status >= 200 && res.status < 300)) {
        metrics.add("poll_errors", 1);
      } else {
        try {
          visitor.remember(typeof res.body === "string" ? JSON.parse(res.body) : res.body);
        } catch {
          // an unreadable body keeps the held object, as the site does
        }
      }
      visitor.lastPollDoneAt = now();
    },

    onLocation(data) {
      const at = now();
      metrics.add("events", 1);
      const lag = lagMs(at, data.publishedAt);
      if (lag !== null) {
        metrics.add("event_lag_ms", lag);
        metrics.add("events_on_time", lag <= params.allowedLagMs);
      }
      if (data.eventId !== visitor.prevEventId) visitor.prevSeq = null;
      if (seqRegressed(visitor.prevSeq, data.seq)) metrics.add("seq_regressions", 1);
      if (typeof data.seq === "number") visitor.prevSeq = data.seq;
      if (visitor.lastHubEventAt !== null) metrics.add("event_gap_ms", at - visitor.lastHubEventAt);
      visitor.prevEventId = data.eventId;
      visitor.lastHubEventAt = at;
      visitor.remember(data);
    },
  };
  return visitor;
}

// One websocket's life. `socket.close()` is expected to end in `closed()`.
export function createConnection(visitor, socket) {
  const { params, metrics, now } = visitor;
  let openedAt = null;
  let handshaken = false;
  let lastReceivedAt = null;
  let lastPingAt = null;
  let nextId = 0;
  let pendingJoin = null;
  let joinAt = null;
  let failedJoins = 0;
  let serviceRemoved = false;
  let closing = false;
  let errored = false;
  let done = false;

  function sendJoin() {
    nextId += 1;
    pendingJoin = String(nextId);
    joinAt = null;
    socket.send(invocation("JoinChannel", [params.channel], pendingJoin));
  }

  function rejoinLater(ms) {
    visitor.joined = false;
    pendingJoin = null;
    joinAt = now() + ms;
  }

  function fail() {
    if (!errored) metrics.add("ws_errors", 1);
    errored = true;
    if (!closing && openedAt !== null) {
      closing = true;
      socket.close();
    }
  }

  function onEnvelope(env) {
    if (!env || env.channel !== params.channel) return;
    const data = env.data && typeof env.data === "object" ? env.data : {};
    if (env.event === "joined") {
      if (!visitor.joined) {
        visitor.joined = true;
        failedJoins = 0;
        serviceRemoved = false;
        joinAt = null;
        metrics.add("joins_ok", 1);
        metrics.add("join_ms", now() - openedAt);
      }
    } else if (env.event === "location") {
      visitor.onLocation(data);
    } else if (env.event === "channelEvicted") {
      metrics.add("evictions", 1);
      if (data.reason === "service_removed") {
        serviceRemoved = true;
        rejoinLater(SERVICE_REMOVED_RETRY_MS);
      } else {
        // auth_expired, and any reason this version does not know: join again at once
        visitor.joined = false;
        sendJoin();
      }
    }
  }

  function onMessage(message) {
    if (isCompletion(message)) {
      if (message.invocationId !== pendingJoin) return;
      pendingJoin = null;
      if (message.error !== undefined && message.error !== null) {
        metrics.add("joins_denied", 1);
        failedJoins += 1;
        rejoinLater(serviceRemoved ? SERVICE_REMOVED_RETRY_MS : joinRetryDelay(failedJoins));
      }
    } else if (isClose(message)) {
      fail();
    } else {
      const env = envelopeOf(message);
      if (env) onEnvelope(env);
    }
  }

  return {
    get joined() {
      return visitor.joined;
    },

    open() {
      openedAt = now();
      lastReceivedAt = openedAt;
      lastPingAt = openedAt;
      metrics.add("ws_opened", 1);
      socket.send(HANDSHAKE);
    },

    message(text) {
      lastReceivedAt = now();
      for (const message of splitFrames(text)) {
        if (!handshaken) {
          const error = handshakeError(message);
          if (error !== null) return fail();
          handshaken = true;
          visitor.reconnects = 0;
          sendJoin();
          continue;
        }
        onMessage(message);
      }
    },

    // Called often (scenario.js: every 250 ms): the keepalive, the server
    // timeout, and a join that is waiting out its delay.
    tick() {
      if (openedAt === null || closing) return;
      const at = now();
      if (at - lastReceivedAt > SERVER_TIMEOUT_MS) return fail();
      if (handshaken && at - lastPingAt >= KEEPALIVE_MS) {
        lastPingAt = at;
        socket.send(PING);
      }
      if (handshaken && joinAt !== null && at >= joinAt && pendingJoin === null) sendJoin();
    },

    // A close the visitor asked for at the session end.
    end() {
      if (closing) return;
      closing = true;
      socket.close();
    },

    // An error the socket reported.
    error() {
      fail();
    },

    // The socket is closed, by either side. A close the visitor did not ask
    // for counts as an error.
    closed() {
      if (done) return;
      done = true;
      if (!closing && !errored) {
        errored = true;
        metrics.add("ws_errors", 1);
      }
      if (openedAt !== null) metrics.add("ws_closed", 1);
      visitor.joined = false;
    },

    get failed() {
      return errored;
    },
  };
}
