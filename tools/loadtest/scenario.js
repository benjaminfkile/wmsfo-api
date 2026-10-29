// Load scenario v1: one anonymous tracker visitor per virtual user, held for
// the whole run (README.md). The gateway's load feature runs it on generator
// instances with the run in GATEWAY_LOAD_* variables; it carries no URL and no
// credential of its own. The behaviour lives in lib/session.js; this file
// wires k6's socket, http, clock, and metrics to it.

import http from "k6/http";
import ws from "k6/ws";
import { fail, sleep } from "k6";
import { Counter, Rate, Trend } from "k6/metrics";
import { readRun } from "./lib/params.js";
import { pollsFor, stagesFor, startVusFor } from "./lib/live.js";
import { createConnection, createVisitor, reconnectDelay } from "./lib/session.js";

const USER_AGENT = "wmsfo-loadtest/1";
const TICK_MS = 250;

const { run, params, problems } = readRun(__ENV);
const ramp = run.rampSeconds || 0;
const hold = Number.isNaN(run.holdSeconds) ? 300 : run.holdSeconds;
const vus = run.vus || 1;

export const options = {
  scenarios: {
    visitor: {
      executor: "ramping-vus",
      startVUs: startVusFor(ramp, vus),
      stages: stagesFor(ramp, hold, vus),
      gracefulRampDown: "10s",
      gracefulStop: "10s",
    },
  },
  summaryTrendStats: ["avg", "min", "med", "max", "p(95)", "p(99)"],
  userAgent: USER_AGENT,
};

const metrics = {
  ws_opened: new Counter("ws_opened"),
  ws_closed: new Counter("ws_closed"),
  ws_errors: new Counter("ws_errors"),
  joins_ok: new Counter("joins_ok"),
  joins_denied: new Counter("joins_denied"),
  join_ms: new Trend("join_ms", true),
  events: new Counter("events"),
  event_lag_ms: new Trend("event_lag_ms", true),
  seq_gaps: new Counter("seq_gaps"),
  evictions: new Counter("evictions"),
  polls: new Counter("polls"),
  poll_errors: new Counter("poll_errors"),
  poll_ms: new Trend("poll_ms", true),
  events_on_time: new Rate("events_on_time"),
};
const sink = { add: (name, value) => metrics[name].add(value) };

export function setup() {
  if (problems.length) fail(`wmsfo-loadtest: ${problems.join("; ")}`);
  console.log(
    `wmsfo-loadtest: run ${run.runId ?? "-"} scenario ${run.scenario ?? "-"} generator ${run.index + 1} of ${run.count}, ` +
      `${vus} users, ramp ${ramp} s, hold ${hold} s, target ${run.target ?? "-"}`,
  );
  return { endAt: Date.now() + (ramp + hold) * 1000 };
}

function get(url) {
  const res = http.get(url, { tags: { name: "live/location.json" }, timeout: "10s" });
  return { status: res.status, durationMs: res.timings.duration, body: res.body };
}

// Waits `ms` outside a socket, polling when a poll falls due.
function wait(visitor, ms, endAt) {
  const until = Math.min(Date.now() + ms, endAt);
  while (Date.now() < until) {
    if (visitor.pollDue(Date.now())) visitor.poll(get);
    sleep(Math.max(0, Math.min(TICK_MS, until - Date.now())) / 1000);
  }
}

export default function (data) {
  const endAt = data.endAt;
  const visitor = createVisitor({ params, metrics: sink, now: Date.now, polls: pollsFor(__VU, params.pollFraction) });

  while (Date.now() < endAt) {
    let connection = null;
    try {
      ws.connect(params.hubUrl, { headers: { "User-Agent": USER_AGENT } }, (socket) => {
        connection = createConnection(visitor, {
          send: (text) => socket.send(text),
          close: () => socket.close(),
        });
        socket.on("open", () => connection.open());
        socket.on("message", (text) => connection.message(text));
        socket.on("error", () => connection.error());
        socket.on("close", () => connection.closed());
        socket.setInterval(() => {
          if (Date.now() >= endAt) return connection.end();
          connection.tick();
          if (visitor.pollDue(Date.now())) visitor.poll(get);
        }, TICK_MS);
        socket.setTimeout(() => connection.end(), Math.max(0, endAt - Date.now()));
      });
    } catch {
      if (connection) connection.error();
    }
    // A dial that failed before the socket existed is an error too.
    if (connection) connection.closed();
    else metrics.ws_errors.add(1);
    if (Date.now() >= endAt) break;
    visitor.reconnects += 1;
    wait(visitor, reconnectDelay(visitor.reconnects), endAt);
  }
}
