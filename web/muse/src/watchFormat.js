// Pure formatting helpers for the watch-live view.
// No DOM, no network, no logging: safe to unit-test under node.

// Known room members and their roles.
const ROLES = {
  Alex: "human",
  Fuse: "agent",
  chief: "agent",
  Design: "agent",
  SocialMgr: "agent",
};

export function roleOf(nick) {
  if (Object.hasOwn(ROLES, nick)) return ROLES[nick];
  if (/^spectator[-_]/i.test(nick || "")) return "spectator";
  return "guest";
}

const PALETTE = {
  Alex: { fg: "#f0b45a", bg: "rgba(240,180,90,0.12)" },
  Fuse: { fg: "#7ee0b0", bg: "rgba(126,224,176,0.12)" },
  chief: { fg: "#6ea8fe", bg: "rgba(110,168,254,0.12)" },
  Design: { fg: "#b8a4ff", bg: "rgba(184,164,255,0.12)" },
  SocialMgr: { fg: "#f49ac1", bg: "rgba(244,154,193,0.12)" },
};
const FALLBACK = [
  { fg: "#8b97a8", bg: "rgba(139,151,168,0.12)" },
  { fg: "#7dd3d8", bg: "rgba(125,211,216,0.12)" },
  { fg: "#e0a47e", bg: "rgba(224,164,126,0.12)" },
];

// Stable per-nick accent colors for chips and avatars.
export function nickStyle(nick) {
  if (Object.hasOwn(PALETTE, nick)) return PALETTE[nick];
  let h = 0;
  const s = String(nick || "?");
  for (const c of s) h = (h * 31 + c.codePointAt(0)) >>> 0;
  return FALLBACK[h % FALLBACK.length];
}

// Parse a relay protocol envelope ({"type":"task"|"opinion"|"result", ...}).
// Returns the object, or null when the text is not an envelope.
export function parseEnvelope(text) {
  const t = String(text || "").trim();
  if (!t.startsWith("{") || !t.endsWith("}")) return null;
  try {
    const o = JSON.parse(t);
    if (o && typeof o === "object" && typeof o.type === "string") return o;
  } catch (_) {
    /* not an envelope */
  }
  return null;
}

// The relay keeps "!" when the trip already has it. Display adds one only
// when the id does not, so a public trip is not shown with a doubled prefix.
export function formatTrip(trip) {
  const t = String(trip || "").trim();
  if (!t) return "";
  return t.startsWith("!") ? t : "!" + t;
}

// Nicks are letters, digits, ".", "_", and "-". The generated spectator nick
// uses an underscore so it stays inside that alphabet.
export function spectatorNick() {
  return "spectator_" + Math.random().toString(36).slice(2, 6);
}
