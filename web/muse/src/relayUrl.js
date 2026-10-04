// WebSocket URL helper for the Muse client.
// An empty value uses the local relay default for callers that still want a
// dev socket. The public demo does not use that default: a blank visitor
// value stays unset. An invalid value means "not configured": the page must
// not open a socket. Never put credentials in the URL. The public build does
// not inline a private host.

export const LOCAL_RELAY_URL = "ws://127.0.0.1:8787/relay";

export function resolveRelayUrl(envValue) {
  const raw = String(envValue == null ? "" : envValue).trim();
  const candidate = raw || LOCAL_RELAY_URL;
  let url;
  try {
    url = new URL(candidate);
  } catch {
    return "";
  }
  if (url.protocol !== "ws:" && url.protocol !== "wss:") return "";
  if (url.username || url.password || url.search || url.hash) return "";
  return url.href;
}
