// Reconnect decisions for the Muse client. Loaded before app.js in the browser,
// and require()'d by tests/muse/reconnect.test.js. docs/muse and web/muse copies
// of this file are kept identical.
(function (root, factory) {
  var api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  else root.MuseReconnect = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
  // hack.chat rejects a join with a "warn" and leaves the socket open. A taken
  // nick (often our own ghost session) or a rate limit is worth retrying on a
  // first join; anything else (an invalid nick, a bad channel) will not change
  // by waiting, so that first join stops.
  var RETRYABLE_JOIN_WARN = /taken|too fast|rate|wait/i;
  // A page reload can race its own not-yet-expired session, so a first join
  // gets a few retries before we decide the nick really belongs to someone else.
  var FIRST_JOIN_MAX_RETRIES = 3;

  // "retry" keeps the socket looping with backoff. "stop" forgets the password
  // and waits for the user to press Connect again.
  // firstJoinWarns counts rejected joins only. A socket close is not a warning
  // and must not be passed in here, or a few drops would spend the first-join budget.
  function onJoinWarn(text, hasJoinedOnce, firstJoinWarns) {
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
  function recordJoinWarn(text, hasJoinedOnce, firstJoinWarns) {
    var decision = onJoinWarn(text, hasJoinedOnce, firstJoinWarns);
    var next = decision === "retry" && !hasJoinedOnce ? firstJoinWarns + 1 : firstJoinWarns;
    return { decision: decision, firstJoinWarns: next };
  }

  // A closed socket retries whenever the user hasn't pressed Disconnect.
  // This includes a drop before the first onlineSet: that isn't bad input.
  function onSocketClose(wantConnected) {
    return wantConnected ? "retry" : "stop";
  }

  return {
    RETRYABLE_JOIN_WARN: RETRYABLE_JOIN_WARN,
    FIRST_JOIN_MAX_RETRIES: FIRST_JOIN_MAX_RETRIES,
    onJoinWarn: onJoinWarn,
    recordJoinWarn: recordJoinWarn,
    onSocketClose: onSocketClose
  };
});
