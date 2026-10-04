import { nextTick, onBeforeUnmount, onMounted, ref } from "vue";
import { applyBrowserDemoConfig, resolveVisitorRelay, saveBrowserDemoConfig } from "./demoSession.js";
import { onSocketClose } from "./reconnect.js";
import { isHello, joinFrame } from "./relayProtocol.js";
import { isNearBottom } from "./scroll.js";
import { onWatchFrame } from "./watchSession.js";
import { parseEnvelope, nickStyle, roleOf, spectatorNick } from "./watchFormat.js";

// Relay URL and room come from the visitor (form or query), not from the build.
const BACKOFF_BASE_MS = 1000;
const BACKOFF_MAX_MS = 30000;
const MAX_MESSAGES = 300;

function nowStamp() {
  const d = new Date();
  return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

function metricsOf(el) {
  if (!el) return null;
  return { scrollHeight: el.scrollHeight, scrollTop: el.scrollTop, clientHeight: el.clientHeight };
}

// Read-only live view of a relay room the visitor names. Joins as a random
// spectator, never sends chat, reconnects with backoff. No trip is stored.
export function useWatch() {
  const demo = applyBrowserDemoConfig();
  const statusText = ref(demo.room ? "connecting…" : "watch channel not configured");
  const live = ref(false);
  const channel = ref(demo.room);
  const relayUrl = ref(demo.relay);
  const messages = ref([]);
  const users = ref([]);
  const unseen = ref(0);
  const transcriptEl = ref(null);

  let nick = spectatorNick();
  const trips = new Map();
  let ws = null;
  let joined = false;
  let helloSeen = false;
  let wantConnected = false;
  let retryAttempt = 0;
  let retryTimer = null;
  let rowSeq = 0;
  let followTail = true;
  let scrollingProgrammatically = false;

  function setStatus(text, on) {
    statusText.value = text;
    live.value = !!on;
  }

  function syncUsers() {
    users.value = [...trips.keys()]
      .sort((a, b) => a.localeCompare(b))
      .map((n) => ({ nick: n, trip: trips.get(n) || "", role: roleOf(n) }));
  }

  function scrollToEnd() {
    const node = transcriptEl.value;
    if (!node) return;
    scrollingProgrammatically = true;
    node.scrollTop = node.scrollHeight;
    followTail = true;
    unseen.value = 0;
    queueMicrotask(() => {
      scrollingProgrammatically = false;
    });
  }

  function onTranscriptScroll() {
    if (scrollingProgrammatically) return;
    const near = isNearBottom(metricsOf(transcriptEl.value));
    followTail = near;
    if (near) unseen.value = 0;
  }

  function jumpToEnd() {
    followTail = true;
    nextTick(scrollToEnd);
  }

  function push(row) {
    const stick = followTail;
    if (row.nick) {
      row.role = roleOf(row.nick);
      row.accent = nickStyle(row.nick);
    }
    messages.value.push({ id: ++rowSeq, time: nowStamp(), ...row });
    if (messages.value.length > MAX_MESSAGES) {
      messages.value.splice(0, messages.value.length - MAX_MESSAGES);
    }
    if (stick) {
      nextTick(scrollToEnd);
    } else {
      unseen.value += 1;
    }
  }

  function pushSys(text) {
    push({ kind: "sys", text });
  }

  function applyUsers(list) {
    trips.clear();
    if (Array.isArray(list)) {
      for (const u of list) {
        if (u && u.nick) trips.set(u.nick, u.trip || "");
      }
    }
    syncUsers();
  }

  function pushChat(data) {
    const who = data.nick || "?";
    const text = data.text || "";
    const proto = parseEnvelope(text);
    if (proto) {
      push({ kind: "proto", nick: who, trip: data.trip || trips.get(who) || "", proto });
    } else {
      push({ kind: "chat", nick: who, trip: data.trip || trips.get(who) || "", text });
    }
  }

  function handleMessage(data) {
    const type = data && data.type;
    if (type === "hello" || type === "pong") return;
    if (type === "welcome") {
      applyUsers(data.users);
      const replay = Array.isArray(data.replay) ? data.replay : [];
      for (const line of replay) {
        if (line && line.type === "chat") pushChat(line);
      }
      pushSys(`${trips.size} in the room`);
      return;
    }
    if (type === "presence") {
      if (Array.isArray(data.users)) applyUsers(data.users);
      if (data.event === "join" && data.nick) pushSys(`${data.nick} joined`);
      else if (data.event === "leave" && data.nick) pushSys(`${data.nick} left`);
      else if (data.event === "nick") {
        pushSys(`${data.previousNick || "someone"} is now ${data.nick || "?"}`);
      }
      return;
    }
    if (type === "error") {
      pushSys(data.text || data.code || "error");
      return;
    }
    if (type === "chat") {
      if (data.nick) trips.set(data.nick, data.trip || trips.get(data.nick) || "");
      syncUsers();
      pushChat(data);
      return;
    }
    if (type === "bye") {
      pushSys(data.reason === "replaced" ? "this nick was replaced" : "left the room");
    }
  }

  function clearRetry() {
    if (retryTimer) {
      clearTimeout(retryTimer);
      retryTimer = null;
    }
  }

  function dropSocket() {
    if (!ws) return;
    const old = ws;
    ws = null;
    old.onopen = old.onmessage = old.onerror = old.onclose = null;
    try {
      old.close();
    } catch (_) {
      /* ignore */
    }
  }

  function scheduleReconnect() {
    clearRetry();
    const exp = BACKOFF_BASE_MS * 2 ** Math.min(retryAttempt, 10);
    const delay = Math.min(BACKOFF_MAX_MS, Math.round(exp * (0.8 + Math.random() * 0.4)));
    retryAttempt += 1;
    setStatus(`reconnecting in ${Math.ceil(delay / 1000)}s`, false);
    retryTimer = setTimeout(() => {
      retryTimer = null;
      openSocket();
    }, delay);
  }

  function sendJoin(sock) {
    // Spectator join is room and nick only. No trip is attached.
    sock.send(JSON.stringify(joinFrame({ room: channel.value, nick })));
  }

  function openSocket() {
    clearRetry();
    dropSocket();
    joined = false;
    helloSeen = false;
    const room = String(channel.value || "").trim();
    const url = relayUrl.value;
    if (!room) {
      setStatus("watch channel not configured", false);
      pushSys("watch channel not configured");
      return;
    }
    if (!url) {
      setStatus("relay URL not configured", false);
      pushSys("relay URL not configured");
      return;
    }
    setStatus("connecting…", false);
    const sock = new WebSocket(url);
    ws = sock;
    sock.onopen = () => {
      if (sock !== ws) return;
      helloSeen = false;
      joined = false;
      setStatus("connecting…", false);
    };
    sock.onmessage = (ev) => {
      if (sock !== ws) return;
      let data;
      try {
        data = JSON.parse(ev.data);
      } catch {
        return;
      }
      if (!helloSeen) {
        if (!isHello(data)) {
          dropSocket();
          scheduleReconnect();
          return;
        }
        helloSeen = true;
        setStatus("joining…", false);
        sendJoin(sock);
        return;
      }
      const decision = onWatchFrame(data, joined);
      if (decision.action === "joined") {
        joined = true;
        retryAttempt = 0;
        setStatus("live", true);
      }
      handleMessage(data);
      if (decision.action === "giveup") {
        dropSocket();
        setStatus("couldn't join — invalid nickname", false);
        pushSys("the generated spectator nickname was rejected; not retrying");
      } else if (decision.action === "retry") {
        if (decision.rotateNick) nick = spectatorNick();
        dropSocket();
        scheduleReconnect();
      }
    };
    sock.onerror = () => {
      if (sock !== ws) return;
      setStatus("error", false);
    };
    sock.onclose = () => {
      if (sock !== ws) return;
      ws = null;
      joined = false;
      helloSeen = false;
      if (onSocketClose(wantConnected) === "stop") {
        setStatus("disconnected", false);
        return;
      }
      pushSys("connection lost — reconnecting");
      scheduleReconnect();
    };
  }

  function reconnectNowIfNeeded() {
    if (!wantConnected) return;
    if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;
    openSocket();
  }

  function onVisibility() {
    if (document.visibilityState === "visible") reconnectNowIfNeeded();
  }

  function connectDemo(relayRaw, roomRaw) {
    const relay = resolveVisitorRelay(relayRaw);
    const room = String(roomRaw == null ? "" : roomRaw).trim();
    const relayError = relay ? "" : "Enter a ws:// or wss:// relay URL with no user, password, query, or fragment.";
    const roomError = room ? "" : "Enter a room name.";
    if (relayError || roomError) return { ok: false, relayError, roomError };
    channel.value = room;
    relayUrl.value = relay;
    saveBrowserDemoConfig({ relay, room });
    wantConnected = true;
    retryAttempt = 0;
    nick = spectatorNick();
    messages.value = [];
    users.value = [];
    trips.clear();
    openSocket();
    return { ok: true, relayError: "", roomError: "" };
  }

  onMounted(() => {
    document.addEventListener("visibilitychange", onVisibility);
    window.addEventListener("online", reconnectNowIfNeeded);
    if (channel.value && relayUrl.value) {
      wantConnected = true;
      openSocket();
    } else {
      wantConnected = false;
    }
  });

  onBeforeUnmount(() => {
    wantConnected = false;
    document.removeEventListener("visibilitychange", onVisibility);
    window.removeEventListener("online", reconnectNowIfNeeded);
    clearRetry();
    dropSocket();
  });

  return {
    statusText,
    live,
    channel,
    relayUrl,
    connectDemo,
    messages,
    users,
    unseen,
    transcriptEl,
    onTranscriptScroll,
    jumpToEnd,
  };
}
