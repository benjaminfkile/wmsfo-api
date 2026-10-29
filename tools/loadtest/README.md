# wmsfo-loadtest

Load scenario v1 (docs/platform.md 1.9): a k6 script that behaves like one anonymous browser on the tracker page, once per virtual user, and reports what it sees as k6 metrics. The gateway's load feature runs it on generator instances with k6 and hands it a run through `GATEWAY_LOAD_*` environment variables; it never runs in the API image. There is no signed-in flow in v1.

The script never carries a URL or a credential: the hub, the channel, and the CDN arrive in `GATEWAY_LOAD_PARAMS` on every run, and every example here uses `<placeholder>` names.

## What one virtual user does

It mirrors the site's hub client and data loop (docs/contracts.md 2.2, 2.3, 1.9), nothing more:

1. Opens a websocket to `hubUrl` with no Origin header and `User-Agent: wmsfo-loadtest/1`, negotiation skipped, and sends the SignalR JSON hub handshake `{"protocol":"json","version":1}` plus U+001E. The answer `{}` opens the session; `{"error":"..."}` fails the connection.
2. Invokes `JoinChannel(channel)` and waits for its completion. A completion with an `error` is a denied join (`joins_denied`), retried after 10 s, then 1, 2, 3, 5 s. The caller-only `joined` ack on the channel is the join (`joins_ok`, and `join_ms` from socket open to the ack).
3. Sends a `{"type":6}` ping every 15 s, and drops a connection the server has left silent for 30 s.
4. On every `location` event on the channel: records `event_lag_ms` (now minus `publishedAt`) and `events_on_time`, counts `events`, records `event_gap_ms` (the time since the previous `location` event, reconnects included), counts `seq_regressions` when `seq` repeats or steps back, and keeps `pollIntervalMs` and `eventStatusId`. On `channelEvicted` it counts `evictions`; `auth_expired` re-joins at once, `service_removed` retries the join every 5 s. Other channels and events are ignored.
5. If it is a polling user (`__VU` modulo 100 below `pollFraction` times 100), GETs `<cdnBaseUrl>/live/location.json` every `pollIntervalMs` (the newest live object's, else the param), at max(1000, interval / 2) while the event is live (status 3) and no hub `location` event arrived within twice the interval or the user is not joined. The next poll is due a cadence after the previous one ends, so polls never overlap. Polling goes on while the socket is down.
6. Holds the session for ramp plus hold seconds from the start of the run, then closes. An unexpected close or error counts `ws_errors` and `ws_closed`, and the user reconnects after 1, 2, 3, then every 5 s and joins again; it never gives up before the session end.

It uses `k6/ws`: `ws.connect` holds its socket, timers, and handlers for as long as the socket is open, so one connection spans the whole session, and the reconnect loop runs around it inside the one iteration.

## Variables

Every variable may be absent when run by hand.

| Variable | Default | Meaning |
|---|---|---|
| `GATEWAY_LOAD_RUN_ID` | none | The gateway's run id; printed at setup |
| `GATEWAY_LOAD_SCENARIO` | none | The scenario name; printed at setup |
| `GATEWAY_LOAD_INDEX`, `GATEWAY_LOAD_COUNT` | `0`, `1` | This generator's zero-based index and the generator count; informational |
| `GATEWAY_LOAD_VUS` | `1` | Virtual users this generator owns |
| `GATEWAY_LOAD_RAMP_SECONDS` | `0` | Seconds to ramp from 0 to `GATEWAY_LOAD_VUS`; with 0 every user starts at once |
| `GATEWAY_LOAD_HOLD_SECONDS` | `300` | Seconds to hold every user after the ramp |
| `GATEWAY_LOAD_TARGET` | none | The target's name; informational |
| `GATEWAY_LOAD_PARAMS` | `{}` | A JSON object of strings, below |

## Params

| Param | Default | Meaning |
|---|---|---|
| `hubUrl` | required | The gateway's SignalR hub, `wss://<gateway-domain>/hub` |
| `channel` | required | The location channel, `<service>:location`, e.g. `wmsfo-api-dev:location` |
| `cdnBaseUrl` | required when `pollFraction` is above 0 | `https://<cdn-domain>`, no trailing path |
| `pollFraction` | `"1"` | The share of users that poll the CDN, 0 to 1; `"1"` is everyone, `"0"` nobody |
| `pollIntervalMs` | `"5000"` | The poll cadence until a live object names one |
| `allowedLagMs` | `"2000"` | The lag at or under which an event counts as on time in `events_on_time` |

A missing required param, or a value that does not parse, fails `setup` with the list of problems before any user starts.

## Metrics

Beside k6's own (`vus`, `ws_*`, `http_*`), which the script leaves as they are:

| Metric | Kind | Meaning |
|---|---|---|
| `ws_opened` | Counter | Sockets opened |
| `ws_closed` | Counter | Opened sockets closed, by either side, the session end included |
| `ws_errors` | Counter | Connections that failed: a refused dial or handshake, a socket error, a close the user did not ask for, a server close message, or 30 s of server silence |
| `joins_ok` | Counter | `joined` acks on the channel |
| `joins_denied` | Counter | `JoinChannel` completions carrying an error |
| `join_ms` | Trend | Socket open to the `joined` ack |
| `events` | Counter | Hub `location` events on the channel |
| `event_lag_ms` | Trend | Arrival time minus the event's `publishedAt`, by the generator's clock against the API's: a generator keeps time with chrony, so the figure is the delivery lag; a laptop whose clock is off shows the offset instead, negative when the laptop runs behind |
| `event_gap_ms` | Trend | Time between consecutive `location` events, reconnects included; a stall shows as a high max |
| `seq_regressions` | Counter | `location` events whose `seq` repeated or stepped back within one event (a skipped value is not a fault: every stored fix of the event takes a seq, spare beacons included) |
| `evictions` | Counter | `channelEvicted` envelopes on the channel |
| `polls` | Counter | CDN polls |
| `poll_errors` | Counter | Polls answered with a non-2xx status or failed on the network |
| `poll_ms` | Trend | Poll duration |
| `events_on_time` | Rate | Share of events with `event_lag_ms` at or under `allowedLagMs` |

## One user by hand

With k6 installed, against dev, from the repository root:

```sh
k6 run -e GATEWAY_LOAD_VUS=1 -e GATEWAY_LOAD_HOLD_SECONDS=300 -e GATEWAY_LOAD_PARAMS='{"hubUrl":"wss://<gateway-domain>/hub","channel":"wmsfo-api-dev:location","cdnBaseUrl":"https://<cdn-domain>"}' tools/loadtest/scenario.js
```

A good five minutes while the event is live and the beacon sends once a second: `events` near 300 (one per second), `event_gap_ms` max near 1000, `seq_regressions` 0, `event_lag_ms` p(99) well under `allowedLagMs` and `events_on_time` near 100%, `joins_ok` at least 1 with `joins_denied` and `ws_errors` 0, and `poll_errors` 0. A user stays on the channel across the gateway's roughly 15 minute allow, so a longer hold shows `evictions` with a matching `joins_ok` each.

## Layout

| File | What it is |
|---|---|
| `scenario.js` | The k6 entry: `options` (one `ramping-vus` scenario), `setup` that validates the run, and the user function wiring `k6/ws` and `k6/http` to `lib/session.js` |
| `lib/signalr.js` | The JSON hub protocol: handshake and ping text, `splitFrames`, `invocation`, message kind helpers |
| `lib/live.js` | `lagMs`, `seqRegressed`, `pollCadence` (the quiet rule), `pollsFor`, `stagesFor` |
| `lib/params.js` | `readRun`: the run variables and the params, defaulted, with the problems listed |
| `lib/session.js` | The visitor and its connections: every message, tick, poll, and close, with the socket, the GET, the clock, and the metrics handed in |

The `lib/*.js` files are plain ES modules with no node built-in and no top-level await, so k6 and Node 22 load the same files.

## Tests

```sh
node --test tools/loadtest   # from the repository root, or npm test here
```

The tests are offline and need no k6: `lib/session.spec.mjs` drives a fake socket through open, handshake, join, ack, location events, a gap, an eviction, and a re-join with the functions `scenario.js` uses.
