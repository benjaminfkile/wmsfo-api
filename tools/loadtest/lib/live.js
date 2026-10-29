// The live object (docs/contracts.md 1.2) as one visitor sees it, and the
// shape of the run. Pure: every clock reading is passed in.

export const DEFAULT_POLL_INTERVAL_MS = 5000;

const LIVE_STATUS = 3;

// Milliseconds from the object's publishedAt to `now`; null when publishedAt
// does not parse.
export function lagMs(now, publishedAt) {
  const at = Date.parse(publishedAt);
  return Number.isFinite(at) ? now - at : null;
}

// The seq values missed between two hub events: zero unless `seq` is more than
// one past `prevSeq`, and zero when either is null.
export function gapsBetween(prevSeq, seq) {
  if (typeof prevSeq !== "number" || typeof seq !== "number") return 0;
  return seq > prevSeq + 1 ? seq - prevSeq - 1 : 0;
}

function intervalOf(live, fallbackMs) {
  const ms = live && live.pollIntervalMs;
  return typeof ms === "number" && ms > 0 ? ms : fallbackMs;
}

// The site's poll cadence (contracts 1.9). Quiet is defined only while the
// event is live (eventStatusId 3): no hub location event yet, or none within
// twice the interval. `lastHubEventAt` is null while the visitor is not joined.
// Quiet polls at max(1000, interval / 2); otherwise at the interval.
export function pollCadence(live, lastHubEventAt, now, fallbackMs = DEFAULT_POLL_INTERVAL_MS) {
  const interval = intervalOf(live, fallbackMs);
  const isLive = !!live && live.eventStatusId === LIVE_STATUS;
  const quiet = isLive && (lastHubEventAt === null || lastHubEventAt === undefined || now - lastHubEventAt > 2 * interval);
  return quiet ? Math.max(1000, interval / 2) : interval;
}

// Whether virtual user `vu` polls the CDN: (vu modulo 100) below fraction * 100.
export function pollsFor(vu, fraction) {
  return vu % 100 < fraction * 100;
}

// The ramping-vus stages: ramp to `vus`, then hold. A zero ramp is one stage,
// held from the start (startVusFor gives the executor its startVUs).
export function stagesFor(ramp, hold, vus) {
  const holdStage = { duration: `${hold}s`, target: vus };
  return ramp > 0 ? [{ duration: `${ramp}s`, target: vus }, holdStage] : [holdStage];
}

export function startVusFor(ramp, vus) {
  return ramp > 0 ? 0 : vus;
}
