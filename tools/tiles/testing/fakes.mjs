// Offline doubles: a scripted fetch on globalThis, an injected spawn, and a
// line collector.
import { EventEmitter } from "node:events";

// Replaces globalThis.fetch with handler(url, init) for the duration of fn;
// every call is recorded as { method, url, body }.
export async function withFetch(handler, fn) {
  const original = globalThis.fetch;
  const calls = [];
  globalThis.fetch = async (url, init = {}) => {
    const method = init.method ?? "GET";
    let body = init.body;
    if (typeof body === "string") {
      try {
        body = JSON.parse(body);
      } catch {}
    }
    const call = { method, url: String(url), body, headers: init.headers ?? {} };
    calls.push(call);
    return handler(call);
  };
  try {
    return await fn(calls);
  } finally {
    globalThis.fetch = original;
  }
}

export const json = (status, body, headers = {}) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ...headers } });

// A spawn whose children exit with codeOf(args) (0 by default); calls lists
// each [command, args, options].
export function fakeSpawn(codeOf = () => 0) {
  const calls = [];
  const spawn = (command, args, options) => {
    calls.push([command, args, options]);
    const child = new EventEmitter();
    setImmediate(() => child.emit("close", codeOf(args)));
    return child;
  };
  return { spawn, calls };
}

export function lines() {
  const list = [];
  const push = (line) => list.push(line);
  return { list, push };
}

export const noSleep = async () => {};
