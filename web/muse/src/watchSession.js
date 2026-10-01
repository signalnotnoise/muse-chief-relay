// Spectator join state for voizle-text-relay v1.
// The server sends hello, then the client joins. "live" is only true after
// welcome. An error before that drops the socket and retries. invalid_nick
// stops: rotating cannot fix a nick the client generates wrong. nick_taken
// rotates so the next try is not the same collision. The relay replaces a
// duplicate nick on join; nick_taken is still handled if a rename is refused.

export function onWatchFrame(frame, joined) {
  const type = frame && (frame.type || frame.cmd);
  const already = !!joined;
  if (type === "welcome") {
    return { joined: true, live: true, action: "joined", rotateNick: false };
  }
  if (type === "error" && !already) {
    const code = String((frame && frame.code) || "");
    if (code === "invalid_nick") {
      return { joined: false, live: false, action: "giveup", rotateNick: false };
    }
    return {
      joined: false,
      live: false,
      action: "retry",
      rotateNick: code === "nick_taken",
    };
  }
  return { joined: already, live: already, action: "stay", rotateNick: false };
}
