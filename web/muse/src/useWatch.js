import { nextTick, onBeforeUnmount, onMounted, ref } from "vue";
import { onSocketClose } from "./reconnect.js";
import { isHello, joinFrame } from "./relayProtocol.js";
import { resolveRelayUrl } from "./relayUrl.js";
import { isNearBottom } from "./scroll.js";
import { resolveWatchChannel } from "./watchChannel.js";
import { onWatchFrame } from "./watchSession.js";
import { parseEnvelope, nickStyle, roleOf, spectatorNick } from "./watchFormat.js";

// Channel comes from VITE_WATCH_CHANNEL at dev/build time. The Pages
// workflow passes the repository secret. Unset -> do not join.
function resolveChannel() {
  const env = import.meta.env && import.meta.env.VITE_WATCH_CHANNEL;
  return resolveWatchChannel(env);
}
function resolveRelay() {
  const env = import.meta.env && import.meta.env.VITE_RELAY_URL;
  return resolveRelayUrl(env);
}
const CHANNEL = resolveChannel();
const RELAY_URL = resolveRelay();
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

// Read-only live view of the relay room. Joins as a random spectator,
// never sends chat, reconnects with backoff. No trip, no storage.
export function useWatch() {
  const statusText = ref("connecting…");
  const live = ref(false);
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
    sock.send(JSON.stringify(joinFrame({ room: CHANNEL, nick })));
  }

  function openSocket() {
    clearRetry();
    dropSocket();
    joined = false;
    helloSeen = false;
    if (!CHANNEL) {
      setStatus("watch channel not configured", false);
      pushSys("watch channel not configured");
      return;
    }
    if (!RELAY_URL) {
      setStatus("relay URL not configured", false);
      pushSys("relay URL not configured");
      return;
    }
    setStatus("connecting…", false);
    const sock = new WebSocket(RELAY_URL);
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

  onMounted(() => {
    wantConnected = true;
    document.addEventListener("visibilitychange", onVisibility);
    window.addEventListener("online", reconnectNowIfNeeded);
    openSocket();
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
    channel: CHANNEL,
    relayUrl: RELAY_URL,
    messages,
    users,
    unseen,
    transcriptEl,
    onTranscriptScroll,
    jumpToEnd,
  };
}
