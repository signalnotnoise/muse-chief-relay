// voizle-text-relay v1 frames. JSON text. The server speaks first (hello).
// The client then joins with room, nick, and an optional public trip.
// This relay does not hash a secret and rejects nick#password.

export const PROTOCOL_NAME = "voizle-text-relay";
export const PROTOCOL_VERSION = 1;

// A public trip is the short code people write as !XXXX. The user types the
// code without "!". This client does not hash a password. A raw password,
// nick#password, or any other string is refused and must not be sent.
const PUBLIC_TRIP_BODY = /^[A-Za-z0-9+/]{6}$/;

export function publicTrip(raw) {
  let trip = String(raw == null ? "" : raw).trim();
  if (!trip) return "";
  if (trip.startsWith("!")) trip = trip.slice(1);
  if (!PUBLIC_TRIP_BODY.test(trip)) return "";
  return "!" + trip;
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
