// Whether the transcript should follow new messages. The composer stays pinned;
// only the transcript moves, and only when the reader is already near the bottom.

export const NEAR_BOTTOM_PX = 80;

// metrics is { scrollHeight, scrollTop, clientHeight }, or null when the
// transcript is not mounted yet (treat that as "follow", same as an empty box).
export function isNearBottom(metrics, threshold = NEAR_BOTTOM_PX) {
  if (!metrics) return true;
  const distance = metrics.scrollHeight - metrics.scrollTop - metrics.clientHeight;
  return distance <= threshold;
}
