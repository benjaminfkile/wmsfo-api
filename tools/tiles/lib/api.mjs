// The admin API client: fetch against WMSFO_API_BASE_URL with the wak_ key in
// WMSFO_API_KEY as a Bearer token. An error answer becomes an ApiError that
// carries the body's code, message, and details (contracts 2); a 429 is waited
// for details.retryAfterSeconds (or the Retry-After header) and retried once.

export class ApiError extends Error {
  constructor(status, body) {
    const code = body?.code ?? `http_${status}`;
    super(body?.message ?? `HTTP ${status}`);
    this.status = status;
    this.code = code;
    this.details = body?.details ?? null;
  }
}

// One line: the code, the message, and the details as JSON when present.
export function formatApiError(error) {
  const details = error.details ? ` ${JSON.stringify(error.details)}` : "";
  return `${error.code}: ${error.message}${details}`;
}

// The base URL and key from the environment, or an Error naming what is missing.
export function apiSettings(env) {
  const missing = ["WMSFO_API_BASE_URL", "WMSFO_API_KEY"].filter((name) => !env[name]);
  if (missing.length) {
    throw new Error(`${missing.join(" and ")} must be set (the API base URL and a wak_ key with the maps capability, and events for build --event)`);
  }
  return { baseUrl: env.WMSFO_API_BASE_URL.replace(/\/+$/, ""), key: env.WMSFO_API_KEY };
}

const defaultSleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

export function createApi({ baseUrl, key, sleep = defaultSleep }) {
  async function send(method, path, body) {
    const headers = { Authorization: `Bearer ${key}`, Accept: "application/json" };
    const init = { method, headers };
    if (body !== undefined) {
      headers["Content-Type"] = "application/json";
      init.body = JSON.stringify(body);
    }
    return globalThis.fetch(`${baseUrl}${path}`, init);
  }

  async function readBody(response) {
    const text = await response.text();
    if (!text) return null;
    try {
      return JSON.parse(text);
    } catch {
      return { code: `http_${response.status}`, message: text.slice(0, 200) };
    }
  }

  // The parsed body and status of a 2xx answer; any other answer throws ApiError.
  async function request(method, path, body) {
    let response = await send(method, path, body);
    if (response.status === 429) {
      const error = await readBody(response);
      const seconds = Number(error?.details?.retryAfterSeconds ?? response.headers.get("retry-after") ?? 1);
      await sleep(Math.max(0, seconds) * 1000);
      response = await send(method, path, body);
    }
    const parsed = await readBody(response);
    if (!response.ok) throw new ApiError(response.status, parsed);
    return { status: response.status, body: parsed };
  }

  return { request };
}
