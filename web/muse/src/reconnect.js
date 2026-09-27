// Reconnect decisions for the Muse client. Imported by useChat.js and by
// tests/muse/reconnect.test.js (dynamic import). One copy: the Pages build
// inlines it; there is no second hand-edited file under docs/muse/.

// hack.chat rejects a join with a "warn" and leaves the socket open. A taken
// nick (often our own ghost session) or a rate limit is worth retrying on a
// first join; anything else (an invalid nick, a bad channel) will not change
// by waiting, so that first join stops.
export const RETRYABLE_JOIN_WARN = /taken|too fast|rate|wait/i;
// A page reload can race its own not-yet-expired session, so a first join
// gets a few retries before we decide the nick really belongs to someone else.
export const FIRST_JOIN_MAX_RETRIES = 3;

// "retry" keeps the socket looping with backoff. "stop" forgets the password
// and waits for the user to press Connect again.
// firstJoinWarns counts rejected joins only. A socket close is not a warning
// and must not be passed in here, or a few drops would spend the first-join budget.
export function onJoinWarn(text, hasJoinedOnce, firstJoinWarns) {
  // A session that already reached onlineSet must keep trying. The drop is
  // what we're recovering from; the warn is usually "nick taken" (still us)
  // or some other transient rejection. Bad input was accepted once already.
  if (hasJoinedOnce) return "retry";
  if (RETRYABLE_JOIN_WARN.test(text || "") && firstJoinWarns < FIRST_JOIN_MAX_RETRIES)
    return "retry";
  return "stop";
}

// Apply one join warning and return the next warn count. Socket-close retries
// do not call this, so they leave the count alone.
export function recordJoinWarn(text, hasJoinedOnce, firstJoinWarns) {
  const decision = onJoinWarn(text, hasJoinedOnce, firstJoinWarns);
  const next = decision === "retry" && !hasJoinedOnce ? firstJoinWarns + 1 : firstJoinWarns;
  return { decision, firstJoinWarns: next };
}

// A closed socket retries whenever the user hasn't pressed Disconnect.
// This includes a drop before the first onlineSet: that isn't bad input.
export function onSocketClose(wantConnected) {
  return wantConnected ? "retry" : "stop";
}
