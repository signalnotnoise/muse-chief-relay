// voizle-text-relay v1 frames. JSON text. The server speaks first (hello).
// The client then joins with room, nick, and an optional public trip.
// This relay does not hash a secret and rejects nick#password.

export const PROTOCOL_NAME = "voizle-text-relay";
export const PROTOCOL_VERSION = 1;

// Muse UI only: short public trip ids. The wire protocol allows up to 64; this
// client is stricter so a password pasted into the trip field is not sent
// (voizle does not hash). Real public trips in this room are ~6–8 characters.
export const TRIP_MAX = 12;

const TRIP_BODY_RE = /^[A-Za-z0-9._~+/=-]+$/;

// True when the value looks like a password, not a short opaque public id.
function looksLikePassword(body) {
  // Mixed case + digit at lengths that short trip ids do not use.
  if (
    body.length >= 10 &&
    /[a-z]/.test(body) &&
    /[A-Z]/.test(body) &&
    /\d/.test(body)
  ) {
    return true;
  }
  return false;
}

// A public trip id, or "" when the value is empty or not safe to send.
// Dropped (never echoed): empty, "#", over TRIP_MAX, "!" anywhere but an
// optional leading bang, charset misses, and password-shaped bodies.
export function publicTrip(raw) {
  const trip = String(raw == null ? "" : raw).trim();
  if (!trip || trip.includes("#") || trip.length > TRIP_MAX) return "";
  const body = trip.startsWith("!") ? trip.slice(1) : trip;
  if (!body || body.includes("!") || !TRIP_BODY_RE.test(body)) return "";
  if (looksLikePassword(body)) return "";
  return trip;
}

export function isHello(frame) {
  return !!(
    frame &&
    frame.type === "hello" &&
    frame.v === PROTOCOL_VERSION &&
    frame.protocol === PROTOCOL_NAME
  );
}

export function joinFrame({ room, nick, trip }) {
  const frame = {
    v: PROTOCOL_VERSION,
    type: "join",
    room: String(room || ""),
    nick: String(nick || ""),
  };
  const t = publicTrip(trip);
  if (t) frame.trip = t;
  return frame;
}

export function chatFrame(text) {
  return { v: PROTOCOL_VERSION, type: "chat", text: String(text || "") };
}
