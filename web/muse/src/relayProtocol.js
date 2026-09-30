// voizle-text-relay v1 frames. JSON text. The server speaks first (hello).
// The client then joins with room, nick, and an optional public trip.
// This relay does not hash a secret and rejects nick#password.

export const PROTOCOL_NAME = "voizle-text-relay";
export const PROTOCOL_VERSION = 1;

const TRIP_RE = /^[!A-Za-z0-9._~+/=-]{1,64}$/;

// A public trip id, or "" when the value is empty or not safe to send.
// Anything containing "#" is a secret-shaped password and is dropped.
export function publicTrip(raw) {
  const trip = String(raw == null ? "" : raw).trim();
  if (!trip || trip.includes("#") || !TRIP_RE.test(trip)) return "";
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
