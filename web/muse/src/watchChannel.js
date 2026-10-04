// Trim a room name the visitor typed. An empty value means the page should
// show "watch channel not configured" and not join. The public build does
// not inline a room. Never commit a real room name.
export function resolveWatchChannel(envValue) {
  return String(envValue == null ? "" : envValue).trim();
}
