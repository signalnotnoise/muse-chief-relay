import { nextTick, onBeforeUnmount, onMounted, ref } from "vue";
import { onSocketClose, recordJoinWarn } from "./reconnect.js";
import { isNearBottom } from "./scroll.js";

const WS_URL = "wss://hack.chat/chat-ws";
const DEFAULT_NICK = "Muse";
const BACKOFF_BASE_MS = 1000;
const BACKOFF_MAX_MS = 30000;

// Fields that must never be rendered. hack.chat's "session" frame carries a
// resumable session token: anyone holding it can restore a session with our
// nick and trip without knowing the password, so it's as sensitive as the
// password itself.
const SECRET_KEYS = new Set(["token", "pass", "password"]);

function redact(key, value) {
  return SECRET_KEYS.has(key) ? "<redacted>" : value;
}

// hack.chat's legacy join takes "name#password" in the nick field and splits
// at the first "#". People still type that into the Nick box, so do the same
// split here: the name is the only part that is ever displayed.
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
  const passwordEl = ref(null);
  const transcriptEl = ref(null);
  const messageEl = ref(null);

  let ws = null;
  let myChannel = "";
  // The trip password. It lives only in this closure variable, not in Vue
  // state, so it is not on the component instance. An automatic rejoin keeps
  // the same trip. It is never logged and never written to browser storage
  // or the address bar. The password field is cleared as soon as Connect is
  // pressed. Disconnect, a first join that is rejected for good, or closing
  // the tab forgets it. A drop after a successful join keeps it.
  let myPassword = "";
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
    metaTrip.value = myTrip ? "!" + myTrip : "none";
  }

  function forgetPassword() {
    myPassword = "";
    if (passwordEl.value) passwordEl.value.value = "";
  }

  function joinNick() {
    return myPassword ? nick.value + "#" + myPassword : nick.value;
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
    if (sendRaw({ cmd: "chat", text: t })) return true;
    appendRow({ text: "not connected, message not sent (it's still in the box)", kind: "sys" });
    return false;
  }

  function handleMessage(data) {
    const cmd = data && data.cmd;
    if (cmd === "session") {
      return;
    }
    if (cmd === "onlineSet") {
      online = new Set(Array.isArray(data.nicks) ? data.nicks : []);
      syncUsers();
      appendRow({ text: `online: ${[...online].join(", ") || "(nobody)"}`, kind: "sys" });
      return;
    }
    if (cmd === "onlineAdd") {
      if (data.nick) online.add(data.nick);
      syncUsers();
      appendRow({ text: `${data.nick} joined`, kind: "sys" });
      return;
    }
    if (cmd === "onlineRemove") {
      if (data.nick) online.delete(data.nick);
      syncUsers();
      appendRow({ text: `${data.nick} left`, kind: "sys" });
      return;
    }
    if (cmd === "info" || cmd === "warn") {
      appendRow({ text: data.text || JSON.stringify(data, redact), kind: "sys" });
      return;
    }
    if (cmd === "chat") {
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
    setStatus(hasJoinedOnce ? "reconnecting…" : "connecting…", "off");

    const sock = new WebSocket(WS_URL);
    ws = sock;

    sock.onopen = () => {
      if (sock !== ws) return;
      setStatus("connected", "on");
      const first = !hasJoinedOnce;
      inChat.value = true;
      if (first) messages.value = [];
      online = new Set();
      syncUsers();
      awaitingJoin = true;
      sendRaw({ cmd: "join", channel: myChannel, nick: joinNick() });
      appendRow({
        text: `${hasJoinedOnce ? "rejoining" : "joining"} #${myChannel} as ${nick.value}` +
          (myPassword ? " (with a trip password)" : ""),
        kind: "sys",
      });
      if (first) nextTick(() => messageEl.value && messageEl.value.focus());
    };

    sock.onmessage = (ev) => {
      if (sock !== ws) return;
      let data;
      try { data = JSON.parse(ev.data); }
      catch { appendRow({ text: String(ev.data), kind: "sys" }); return; }
      let joinedNow = false;
      if (data && data.cmd === "onlineSet") {
        awaitingJoin = false;
        retryAttempt = 0;
        firstJoinWarns = 0;
        joinedNow = true;
      }
      handleMessage(data);
      if (joinedNow) {
        const rejoin = hasJoinedOnce;
        hasJoinedOnce = true;
        const me = Array.isArray(data.users) ? data.users.find((u) => u && u.isme) : null;
        setTrip(me && typeof me.trip === "string" ? me.trip : "");
        const who = myTrip ? `${nick.value} !${myTrip}` : `${nick.value} (no trip)`;
        appendRow({ text: `${rejoin ? "rejoined" : "joined"} as ${who}`, kind: "sys" });
      }
      if (awaitingJoin && data && data.cmd === "warn") {
        awaitingJoin = false;
        const warned = recordJoinWarn(data.text || "", hasJoinedOnce, firstJoinWarns);
        firstJoinWarns = warned.firstJoinWarns;
        if (warned.decision === "retry") {
          dropSocket();
          scheduleReconnect();
        } else {
          wantConnected = false;
          dropSocket();
          forgetPassword();
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

  function connect(nextChannel, rawNick, password) {
    const { name, secret } = splitNick(rawNick);
    myChannel = nextChannel;
    nick.value = name || DEFAULT_NICK;
    myPassword = password || secret;
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
    const rest = v.slice(i + 1);
    const name = v.slice(0, i);
    ev.target.value = name;
    nick.value = name;
    if (rest && passwordEl.value) passwordEl.value.value = rest;
    if (passwordEl.value) passwordEl.value.focus();
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
    const typed = passwordEl.value ? passwordEl.value.value : "";
    if (passwordEl.value) passwordEl.value.value = "";
    const rawNick = nickEl.value ? nickEl.value.value : nick.value;
    connect(nextChannel, rawNick, typed);
  }

  function disconnect() {
    wantConnected = false;
    clearRetry();
    dropSocket();
    forgetPassword();
    inChat.value = false;
    setStatus("disconnected", "off");
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
    passwordEl,
    transcriptEl,
    messageEl,
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
