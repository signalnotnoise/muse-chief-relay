import { nextTick, onBeforeUnmount, onMounted, ref } from "vue";
import { onSocketClose, recordJoinWarn } from "./reconnect.js";
import { chatFrame, isHello, joinFrame, publicTrip } from "./relayProtocol.js";
import { resolveRelayUrl } from "./relayUrl.js";
import { createRoomBoard } from "./roomBoard.js";
import { isNearBottom } from "./scroll.js";
import { formatTrip } from "./watchFormat.js";

function resolveRelay() {
  const env = import.meta.env && import.meta.env.VITE_RELAY_URL;
  return resolveRelayUrl(env);
}
const RELAY_URL = resolveRelay();
const DEFAULT_NICK = "Muse";
const BACKOFF_BASE_MS = 1000;
const BACKOFF_MAX_MS = 30000;

// Fields that must never be rendered. A session token can restore a nick
// without the secret that produced a trip, so it is treated like a password.
const SECRET_KEYS = new Set(["token", "pass", "password"]);

function redact(key, value) {
  return SECRET_KEYS.has(key) ? "<redacted>" : value;
}

// A legacy "name#password" nick is split so the secret is never displayed
// and never sent. This relay does not hash a password into a trip.
function splitNick(raw) {
  const s = String(raw || "");
  const i = s.indexOf("#");
  if (i < 0) return { name: s.trim(), secret: "" };
  return { name: s.slice(0, i).trim(), secret: s.slice(i + 1) };
}

function nowStamp() {
  const d = new Date();
  return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

function shortId() {
  return "t" + Math.random().toString(36).slice(2, 8);
}

function tryParseProtocol(text) {
  const t = (text || "").trim();
  if (!t.startsWith("{") || !t.endsWith("}")) return null;
  try {
    const obj = JSON.parse(t);
    if (obj && typeof obj === "object" && typeof obj.type === "string") return obj;
  } catch (_) { /* ignore */ }
  return null;
}

function metricsOf(el) {
  if (!el) return null;
  return {
    scrollHeight: el.scrollHeight,
    scrollTop: el.scrollTop,
    clientHeight: el.clientHeight,
  };
}

export function useChat() {
  const statusText = ref("disconnected");
  const statusKind = ref("off");
  const inChat = ref(false);
  const channel = ref("");
  const channelError = ref(false);
  const nick = ref(DEFAULT_NICK);
  const message = ref("");
  const messages = ref([]);
  const users = ref([]);
  const metaChannel = ref("—");
  const metaNick = ref("—");
  const metaTrip = ref("—");

  const taskTitle = ref("");
  const taskBody = ref("");
  const taskRepo = ref("");
  const opinionTopic = ref("");
  const opinionText = ref("");
  const resultId = ref("");
  const resultStatus = ref("done");
  const resultSummary = ref("");

  const channelEl = ref(null);
  const nickEl = ref(null);
  const tripEl = ref(null);
  const transcriptEl = ref(null);
  const messageEl = ref(null);

  let room;
  const boardView = ref(null);
  function syncBoard() {
    boardView.value = room.getState();
  }
  room = createRoomBoard({ onUpdate: syncBoard });
  boardView.value = room.getState();

  function startBoard(channel) {
    Promise.resolve(room.startBoard(channel)).then(syncBoard, syncBoard);
  }

  function showBoardPlaceholder() {
    room.showBoardPlaceholder();
    syncBoard();
  }

  function reloadBoard() {
    Promise.resolve(room.reloadBoard()).then(syncBoard, syncBoard);
  }

  let ws = null;
  let myChannel = "";
  // The public trip code (!XXXX), only after publicTrip() accepts it. A
  // password is never stored here and never sent. An automatic rejoin sends
  // the same public trip. The field is cleared as soon as Connect is
  // pressed. Disconnect, a first join that is rejected for good, or closing
  // the tab forgets it. A drop after a successful join keeps it.
  let myPublicTrip = "";
  let tripOmitted = false;
  let myTrip = "";
  let online = new Set();

  let wantConnected = false;
  let retryAttempt = 0;
  let firstJoinWarns = 0;
  let retryTimer = null;
  let hasJoinedOnce = false;
  let awaitingJoin = false;
  let rowSeq = 0;
  // True when the reader is already at the tail. New rows scroll into view
  // only then, so reading history is not yanked back down. Sending sets this
  // back to true: the person just used the composer.
  let followTail = true;
  let scrollingProgrammatically = false;

  function setStatus(text, kind) {
    statusText.value = text;
    statusKind.value = kind || "off";
  }

  function setTrip(trip) {
    myTrip = trip || "";
    metaTrip.value = myTrip ? formatTrip(myTrip) : "none";
  }

  function forgetTrip() {
    myPublicTrip = "";
    tripOmitted = false;
    if (tripEl.value) tripEl.value.value = "";
  }

  function syncUsers() {
    users.value = [...online].sort((a, b) => a.localeCompare(b));
  }

  function scrollToEnd() {
    const node = transcriptEl.value;
    if (!node) return;
    scrollingProgrammatically = true;
    node.scrollTop = node.scrollHeight;
    followTail = true;
    queueMicrotask(() => {
      scrollingProgrammatically = false;
    });
  }

  function onTranscriptScroll() {
    if (scrollingProgrammatically) return;
    followTail = isNearBottom(metricsOf(transcriptEl.value));
  }

  function appendRow(fields) {
    const stick = followTail;
    const who = fields.nick || "";
    messages.value.push({
      id: ++rowSeq,
      nick: who,
      text: fields.text,
      kind: fields.kind || "",
      tag: fields.tag || "",
      time: nowStamp(),
      me: !!who && who === nick.value,
    });
    if (stick) nextTick(scrollToEnd);
  }

  function sendRaw(obj) {
    if (!ws || ws.readyState !== WebSocket.OPEN) return false;
    ws.send(JSON.stringify(obj));
    return true;
  }

  function sendChatText(text) {
    const t = (text || "").trim();
    if (!t) return true;
    if (sendRaw(chatFrame(t))) return true;
    appendRow({ text: "not connected, message not sent (it's still in the box)", kind: "sys" });
    return false;
  }

  function applyUsers(list) {
    online = new Set();
    if (Array.isArray(list)) {
      for (const u of list) {
        if (u && u.nick) online.add(u.nick);
      }
    }
    syncUsers();
  }

  function appendChat(data) {
    const who = data.nick || "?";
    const text = data.text || "";
    const proto = tryParseProtocol(text);
    if (proto) {
      appendRow({
        nick: who,
        text: JSON.stringify(proto, null, 2),
        kind: "proto",
        tag: proto.type || "protocol",
      });
    } else {
      appendRow({ nick: who, text });
    }
  }

  function handleMessage(data) {
    const type = data && data.type;
    if (type === "hello" || type === "pong") return;
    if (type === "welcome") {
      applyUsers(data.users);
      const replay = Array.isArray(data.replay) ? data.replay : [];
      for (const line of replay) {
        if (line && line.type === "chat") appendChat(line);
      }
      appendRow({ text: `online: ${[...online].join(", ") || "(nobody)"}`, kind: "sys" });
      return;
    }
    if (type === "presence") {
      if (Array.isArray(data.users)) applyUsers(data.users);
      if (data.event === "join" && data.nick) appendRow({ text: `${data.nick} joined`, kind: "sys" });
      else if (data.event === "leave" && data.nick) appendRow({ text: `${data.nick} left`, kind: "sys" });
      else if (data.event === "nick") {
        appendRow({ text: `${data.previousNick || "someone"} is now ${data.nick || "?"}`, kind: "sys" });
      }
      return;
    }
    if (type === "error") {
      appendRow({ text: data.text || data.code || "error", kind: "sys" });
      return;
    }
    if (type === "chat") {
      appendChat(data);
      return;
    }
    if (type === "bye") {
      appendRow({
        text: data.reason === "replaced" ? "this connection was replaced" : "left the room",
        kind: "sys",
      });
      return;
    }
    appendRow({ text: JSON.stringify(data, redact), kind: "sys" });
  }

  function clearRetry() {
    if (retryTimer) {
      clearTimeout(retryTimer);
      retryTimer = null;
    }
  }

  function socketIsLive() {
    return ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING);
  }

  function dropSocket() {
    if (!ws) return;
    const old = ws;
    ws = null;
    old.onopen = old.onmessage = old.onerror = old.onclose = null;
    try { old.close(); } catch (_) { /* ignore */ }
  }

  function scheduleReconnect() {
    clearRetry();
    const exp = BACKOFF_BASE_MS * 2 ** Math.min(retryAttempt, 10);
    const delay = Math.min(BACKOFF_MAX_MS, Math.round(exp * (0.8 + Math.random() * 0.4)));
    retryAttempt += 1;
    setStatus(`reconnecting in ${Math.ceil(delay / 1000)}s`, "err");
    retryTimer = setTimeout(() => {
      retryTimer = null;
      openSocket();
    }, delay);
  }

  function openSocket() {
    clearRetry();
    dropSocket();
    if (!RELAY_URL) {
      wantConnected = false;
      setStatus("relay URL not configured", "err");
      appendRow({ text: "relay URL not configured", kind: "sys" });
      return;
    }
    setStatus(hasJoinedOnce ? "reconnecting…" : "connecting…", "off");

    const sock = new WebSocket(RELAY_URL);
    ws = sock;
    let helloSeen = false;

    sock.onopen = () => {
      if (sock !== ws) return;
      helloSeen = false;
      setStatus("connecting…", "off");
    };

    sock.onmessage = (ev) => {
      if (sock !== ws) return;
      let data;
      try { data = JSON.parse(ev.data); }
      catch { appendRow({ text: String(ev.data), kind: "sys" }); return; }
      if (!helloSeen) {
        if (!isHello(data)) {
          dropSocket();
          scheduleReconnect();
          return;
        }
        helloSeen = true;
        setStatus("connected", "on");
        const first = !hasJoinedOnce;
        inChat.value = true;
        if (first) messages.value = [];
        online = new Set();
        syncUsers();
        awaitingJoin = true;
        sendRaw(joinFrame({ room: myChannel, nick: nick.value, trip: myPublicTrip }));
        appendRow({
          text: `${hasJoinedOnce ? "rejoining" : "joining"} #${myChannel} as ${nick.value}` +
            (myPublicTrip ? " (with a public trip)" : ""),
          kind: "sys",
        });
        if (tripOmitted && !hasJoinedOnce) {
          appendRow({
            text: "trip not sent — enter the public trip code only (like Ab12Cd), not a password",
            kind: "sys",
          });
        }
        if (first) nextTick(() => messageEl.value && messageEl.value.focus());
        return;
      }
      let joinedNow = false;
      if (data && data.type === "welcome") {
        awaitingJoin = false;
        retryAttempt = 0;
        firstJoinWarns = 0;
        joinedNow = true;
      }
      handleMessage(data);
      if (joinedNow) {
        const rejoin = hasJoinedOnce;
        hasJoinedOnce = true;
        setTrip(typeof data.trip === "string" ? data.trip : "");
        const who = myTrip ? `${nick.value} ${formatTrip(myTrip)}` : `${nick.value} (no trip)`;
        appendRow({ text: `${rejoin ? "rejoined" : "joined"} as ${who}`, kind: "sys" });
      }
      if (awaitingJoin && data && data.type === "error") {
        awaitingJoin = false;
        const warned = recordJoinWarn(data.text || "", hasJoinedOnce, firstJoinWarns, data.code);
        firstJoinWarns = warned.firstJoinWarns;
        if (warned.decision === "retry") {
          dropSocket();
          scheduleReconnect();
        } else {
          wantConnected = false;
          dropSocket();
          forgetTrip();
          setStatus("join rejected", "err");
        }
      }
    };

    sock.onerror = () => {
      if (sock !== ws) return;
      setStatus("error", "err");
    };

    sock.onclose = () => {
      if (sock !== ws) return;
      ws = null;
      awaitingJoin = false;
      if (onSocketClose(wantConnected) === "stop") {
        setStatus("disconnected", "off");
        return;
      }
      appendRow({ text: "connection closed, will retry", kind: "sys" });
      scheduleReconnect();
    };
  }

  function connect(channel, rawNick, tripRaw) {
    const { name, secret } = splitNick(rawNick);
    myChannel = channel;
    nick.value = name || DEFAULT_NICK;
    const typed = String(tripRaw || "");
    myPublicTrip = publicTrip(typed);
    tripOmitted = !!secret || (typed.trim() !== "" && !myPublicTrip);
    metaChannel.value = myChannel;
    metaNick.value = nick.value;
    myTrip = "";
    metaTrip.value = "—";
    wantConnected = true;
    retryAttempt = 0;
    firstJoinWarns = 0;
    hasJoinedOnce = false;
    followTail = true;
    openSocket();
    // Board load is separate from the socket. A failure here must not stop the join,
    // and an automatic reconnect (openSocket alone) must not fetch the board again.
    startBoard(channel);
  }

  function reconnectNowIfNeeded() {
    if (!wantConnected || socketIsLive()) return;
    openSocket();
  }

  function onVisibility() {
    if (document.visibilityState === "visible") reconnectNowIfNeeded();
  }

  function onNickInput(ev) {
    const v = ev.target.value;
    const i = v.indexOf("#");
    if (i < 0) {
      nick.value = v;
      return;
    }
    // Drop the secret. Do not copy it into the trip field: that field is
    // sent as a public id, and this relay does not hash passwords.
    const name = v.slice(0, i);
    ev.target.value = name;
    nick.value = name;
  }

  function onChannelInput(ev) {
    channel.value = ev.target.value;
    if (channel.value.trim()) channelError.value = false;
  }

  function onMessageInput(ev) {
    message.value = ev.target.value;
  }

  function onJoin() {
    // Read the DOM, not just the refs. Autofill can set an input without an
    // input event, and a legacy "name#password" nick has to be split from
    // whatever is actually in the box.
    if (channelEl.value) channel.value = channelEl.value.value;
    const nextChannel = channel.value.trim();
    if (!nextChannel) {
      channelError.value = true;
      if (channelEl.value) channelEl.value.focus();
      return;
    }
    channelError.value = false;
    const typed = tripEl.value ? tripEl.value.value : "";
    if (tripEl.value) tripEl.value.value = "";
    const rawNick = nickEl.value ? nickEl.value.value : nick.value;
    connect(nextChannel, rawNick, typed);
  }

  function disconnect() {
    wantConnected = false;
    clearRetry();
    dropSocket();
    forgetTrip();
    inChat.value = false;
    setStatus("disconnected", "off");
    showBoardPlaceholder();
  }

  function onSend() {
    const text = message.value;
    if (!String(text || "").trim()) {
      message.value = "";
      return;
    }
    followTail = true;
    if (sendChatText(text)) {
      message.value = "";
      nextTick(scrollToEnd);
    }
  }

  function onTask() {
    const payload = {
      type: "task",
      id: shortId(),
      to: "chief",
      title: String(taskTitle.value || "").trim(),
      body: String(taskBody.value || "").trim(),
    };
    const repo = String(taskRepo.value || "").trim();
    if (repo) payload.repo = repo;
    followTail = true;
    if (sendChatText(JSON.stringify(payload))) {
      taskTitle.value = "";
      taskBody.value = "";
      taskRepo.value = "";
    }
  }

  function onOpinion() {
    const payload = {
      type: "opinion",
      from: "muse",
      topic: String(opinionTopic.value || "").trim() || "general",
      text: String(opinionText.value || "").trim(),
    };
    followTail = true;
    if (sendChatText(JSON.stringify(payload))) {
      opinionTopic.value = "";
      opinionText.value = "";
    }
  }

  function onResult() {
    const payload = {
      type: "result",
      id: String(resultId.value || "").trim(),
      from: "muse",
      status: String(resultStatus.value || "done"),
      summary: String(resultSummary.value || "").trim(),
    };
    followTail = true;
    if (sendChatText(JSON.stringify(payload))) {
      resultId.value = "";
      resultStatus.value = "done";
      resultSummary.value = "";
    }
  }

  onMounted(() => {
    document.addEventListener("visibilitychange", onVisibility);
    window.addEventListener("online", reconnectNowIfNeeded);
  });

  onBeforeUnmount(() => {
    document.removeEventListener("visibilitychange", onVisibility);
    window.removeEventListener("online", reconnectNowIfNeeded);
    clearRetry();
    dropSocket();
  });

  return {
    statusText,
    statusKind,
    inChat,
    channel,
    channelError,
    nick,
    message,
    messages,
    users,
    metaChannel,
    metaNick,
    metaTrip,
    taskTitle,
    taskBody,
    taskRepo,
    opinionTopic,
    opinionText,
    resultId,
    resultStatus,
    resultSummary,
    channelEl,
    nickEl,
    tripEl,
    transcriptEl,
    messageEl,
    relayUrl: RELAY_URL,
    boardView,
    reloadBoard,
    onTranscriptScroll,
    onNickInput,
    onChannelInput,
    onMessageInput,
    onJoin,
    disconnect,
    onSend,
    onTask,
    onOpinion,
    onResult,
  };
}
