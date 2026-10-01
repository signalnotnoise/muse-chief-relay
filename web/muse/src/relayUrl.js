// WebSocket URL for the Muse client.
// VITE_RELAY_URL is inlined by Vite. An empty value uses the local relay
// default so a dev build does not dial a public chat network. GitHub Pages
// sets the repository secret (wss). An invalid value means "not configured":
// the page must not open a socket. Never put credentials in the URL.

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
