// Reconnect decisions for the Muse client. Imported by useChat.js and by
// tests/muse/reconnect.test.js (dynamic import). One copy: the Pages build
// inlines it; there is no second hand-edited file under docs/muse/.

// voizle-text-relay rejects a join with type "error" and leaves the socket
// open. A taken nick or a rate limit is worth retrying on a first join;
// anything else (an invalid nick, a bad room) will not change by waiting, so
// that first join stops. The text pattern still covers the older warn wording.
export const RETRYABLE_JOIN_WARN = /taken|too fast|rate|wait/i;
export const RETRYABLE_JOIN_CODE = new Set(["nick_taken", "rate_limited"]);
// A page reload can race its own not-yet-expired session, so a first join
// gets a few retries before we decide the nick really belongs to someone else.
export const FIRST_JOIN_MAX_RETRIES = 3;

// "retry" keeps the socket looping with backoff. "stop" forgets the trip
// and waits for the user to press Connect again.
// firstJoinWarns counts rejected joins only. A socket close is not a warning
// and must not be passed in here, or a few drops would spend the first-join budget.
export function onJoinWarn(text, hasJoinedOnce, firstJoinWarns, code) {
  // A session that already reached welcome must keep trying. The drop is
  // what we're recovering from. Bad input was accepted once already.
  if (hasJoinedOnce) return "retry";
  const retryable = RETRYABLE_JOIN_WARN.test(text || "") || RETRYABLE_JOIN_CODE.has(code || "");
  if (retryable && firstJoinWarns < FIRST_JOIN_MAX_RETRIES) return "retry";
  return "stop";
}

// Apply one join error and return the next warn count. Socket-close retries
// do not call this, so they leave the count alone.
export function recordJoinWarn(text, hasJoinedOnce, firstJoinWarns, code) {
  const decision = onJoinWarn(text, hasJoinedOnce, firstJoinWarns, code);
  const next = decision === "retry" && !hasJoinedOnce ? firstJoinWarns + 1 : firstJoinWarns;
  return { decision, firstJoinWarns: next };
}

// A closed socket retries whenever the user hasn't pressed Disconnect.
// This includes a drop before the first welcome: that isn't bad input.
export function onSocketClose(wantConnected) {
  return wantConnected ? "retry" : "stop";
}
