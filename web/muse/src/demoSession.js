// Public demo connection. The visitor types a relay URL and a room, or
// passes them as ?relay= and ?room= (channel is accepted as a room alias).
// Values stay in memory and, after connect, in localStorage on this device.
// Nothing private is read from the build. Passwords and trips are never stored.
//
// VITE_PUBLIC_DEMO_RELAY is an optional, clearly public prefill. It is unset
// unless a maintainer sets it. Leave it empty. Do not point it at a private host.

import { resolveRelayUrl } from "./relayUrl.js";

export const DEMO_RELAY_KEY = "muse.demo.relayUrl";
export const DEMO_ROOM_KEY = "muse.demo.room";
export const PUBLIC_DEMO_RELAY_UNSET = "unset";

export function publicDemoRelay(envValue) {
  return String(envValue == null ? "" : envValue).trim();
}

export function describePublicDemoRelay(envValue) {
  return publicDemoRelay(envValue) || PUBLIC_DEMO_RELAY_UNSET;
}

// A blank value is not configured. It does not fall back to a baked host.
export function resolveVisitorRelay(raw) {
  const trimmed = String(raw == null ? "" : raw).trim();
  if (!trimmed) return "";
  return resolveRelayUrl(trimmed);
}

export function readDemoQuery(search) {
  const params = new URLSearchParams(String(search || ""));
  return {
    relay: (params.get("relay") || "").trim(),
    room: (params.get("room") || params.get("channel") || "").trim(),
  };
}

export function readDemoStorage(storage) {
  if (!storage) return { relay: "", room: "" };
  try {
    return {
      relay: String(storage.getItem(DEMO_RELAY_KEY) || "").trim(),
      room: String(storage.getItem(DEMO_ROOM_KEY) || "").trim(),
    };
  } catch {
    return { relay: "", room: "" };
  }
}

export function loadDemoConfig({ search, storage, publicRelay } = {}) {
  const query = readDemoQuery(search);
  const stored = readDemoStorage(storage);
  const relayRaw = query.relay || stored.relay || publicDemoRelay(publicRelay);
  const room = query.room || stored.room || "";
  const relay = resolveVisitorRelay(relayRaw);
  return {
    relay,
    room,
    relayInvalid: relayRaw !== "" && relay === "",
    fromQuery: query.relay !== "" || query.room !== "",
  };
}

export function saveBrowserDemoConfig(cfg) {
  if (typeof window === "undefined") return;
  saveDemoConfig(window.localStorage, cfg);
}

export function saveDemoConfig(storage, { relay, room } = {}) {
  if (!storage) return;
  try {
    const nextRelay = resolveVisitorRelay(relay);
    const nextRoom = String(room == null ? "" : room).trim();
    if (nextRelay) storage.setItem(DEMO_RELAY_KEY, nextRelay);
    else storage.removeItem(DEMO_RELAY_KEY);
    if (nextRoom) storage.setItem(DEMO_ROOM_KEY, nextRoom);
    else storage.removeItem(DEMO_ROOM_KEY);
  } catch {
    /* private mode and disabled storage stay in memory only */
  }
}

function readBuildPublicRelay() {
  try {
    return publicDemoRelay(import.meta.env.VITE_PUBLIC_DEMO_RELAY);
  } catch {
    return "";
  }
}

// Drop relay and room from the address bar after they have been copied into
// memory (and localStorage when the browser allows it). The hash route stays.
function rememberQuery(cfg) {
  if (typeof window === "undefined" || !cfg.fromQuery) return cfg;
  saveDemoConfig(window.localStorage, cfg);
  try {
    const params = new URLSearchParams(window.location.search);
    params.delete("relay");
    params.delete("room");
    params.delete("channel");
    const qs = params.toString();
    const next = window.location.pathname + (qs ? "?" + qs : "") + window.location.hash;
    window.history.replaceState(null, "", next);
  } catch {
    /* keep the query in memory either way */
  }
  return cfg;
}

export function applyBrowserDemoConfig() {
  const pub = readBuildPublicRelay();
  if (typeof window === "undefined") {
    const relay = resolveVisitorRelay(pub);
    return { relay, room: "", relayInvalid: pub !== "" && relay === "", fromQuery: false };
  }
  return rememberQuery(loadDemoConfig({
    search: window.location.search,
    storage: window.localStorage,
    publicRelay: pub,
  }));
}
