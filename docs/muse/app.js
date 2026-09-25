(() => {
  const WS_URL = "wss://hack.chat/chat-ws";
  const DEFAULT_CHANNEL = "fuse-grok-6f4e970cd8";
  const DEFAULT_NICK = "Muse";

  const el = {
    status: document.getElementById("status"),
    joinPanel: document.getElementById("join-panel"),
    chatPanel: document.getElementById("chat-panel"),
    joinForm: document.getElementById("join-form"),
    channel: document.getElementById("channel"),
    nick: document.getElementById("nick"),
    transcript: document.getElementById("transcript"),
    sendForm: document.getElementById("send-form"),
    message: document.getElementById("message"),
    users: document.getElementById("users"),
    metaChannel: document.getElementById("meta-channel"),
    metaNick: document.getElementById("meta-nick"),
    disconnect: document.getElementById("disconnect"),
    taskForm: document.getElementById("task-form"),
    opinionForm: document.getElementById("opinion-form"),
    resultForm: document.getElementById("result-form"),
  };

  let ws = null;
  let myNick = DEFAULT_NICK;
  let myChannel = DEFAULT_CHANNEL;
  let online = new Set();

  // Reconnect state. wantConnected is true between Connect and Disconnect.
  // A socket that closes while it's still true gets retried with backoff.
  const BACKOFF_BASE_MS = 1000;
  const BACKOFF_MAX_MS = 30000;
  let wantConnected = false;
  let retryAttempt = 0;
  let retryTimer = null;
  let hasJoinedOnce = false;
  let awaitingJoin = false;

  // hack.chat rejects a join with a "warn" and leaves the socket open. After
  // a mobile tab dies, the old session can hold our nick for a while, so a
  // taken nick (or a rate limit) is worth retrying; anything else (e.g. an
  // invalid nick) is permanent.
  const RETRYABLE_JOIN_WARN = /taken|too fast|rate|wait/i;
  // A page reload can race its own not-yet-expired session, so a first join
  // gets a few retries before we decide the nick really belongs to someone else.
  const FIRST_JOIN_MAX_RETRIES = 3;

  el.channel.value = DEFAULT_CHANNEL;
  el.nick.value = DEFAULT_NICK;

  function setStatus(text, kind) {
    el.status.textContent = text;
    el.status.className = "status " + (kind || "off");
  }

  function showChat(on) {
    el.joinPanel.classList.toggle("hidden", on);
    el.chatPanel.classList.toggle("hidden", !on);
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

  function appendRow({ nick, text, kind, tag }) {
    const row = document.createElement("div");
    row.className = "row " + (kind || "");
    if (nick === myNick) row.classList.add("me");

    const who = document.createElement("div");
    who.className = "who";
    if (nick) {
      const n = document.createElement("span");
      n.className = "nick";
      n.textContent = nick;
      who.appendChild(n);
    }
    const time = document.createElement("span");
    time.className = "time";
    time.textContent = nowStamp();
    who.appendChild(time);
    row.appendChild(who);

    if (tag) {
      const tg = document.createElement("div");
      tg.className = "tag";
      tg.textContent = tag;
      row.appendChild(tg);
    }

    const body = document.createElement("div");
    body.className = "body";
    body.textContent = text;
    row.appendChild(body);

    el.transcript.appendChild(row);
    el.transcript.scrollTop = el.transcript.scrollHeight;
  }

  function renderUsers() {
    el.users.innerHTML = "";
    [...online].sort((a, b) => a.localeCompare(b)).forEach((n) => {
      const li = document.createElement("li");
      li.textContent = n;
      if (n === myNick) li.classList.add("me");
      el.users.appendChild(li);
    });
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
    if (cmd === "onlineSet") {
      online = new Set(Array.isArray(data.nicks) ? data.nicks : []);
      renderUsers();
      appendRow({ text: `online: ${[...online].join(", ") || "(nobody)"}`, kind: "sys" });
      return;
    }
    if (cmd === "onlineAdd") {
      if (data.nick) online.add(data.nick);
      renderUsers();
      appendRow({ text: `${data.nick} joined`, kind: "sys" });
      return;
    }
    if (cmd === "onlineRemove") {
      if (data.nick) online.delete(data.nick);
      renderUsers();
      appendRow({ text: `${data.nick} left`, kind: "sys" });
      return;
    }
    if (cmd === "info" || cmd === "warn") {
      appendRow({ text: data.text || JSON.stringify(data), kind: "sys" });
      return;
    }
    if (cmd === "chat") {
      const nick = data.nick || "?";
      const text = data.text || "";
      const proto = tryParseProtocol(text);
      if (proto) {
        appendRow({
          nick,
          text: JSON.stringify(proto, null, 2),
          kind: "proto",
          tag: proto.type || "protocol",
        });
      } else {
        appendRow({ nick, text });
      }
      return;
    }
    // Unknown / other
    appendRow({ text: JSON.stringify(data), kind: "sys" });
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
    // Detach first so the old socket's close can't schedule a retry.
    old.onopen = old.onmessage = old.onerror = old.onclose = null;
    try { old.close(); } catch (_) { /* ignore */ }
  }

  function scheduleReconnect() {
    clearRetry();
    const exp = BACKOFF_BASE_MS * 2 ** Math.min(retryAttempt, 10);
    // ±20% jitter, then hard cap so the wait never exceeds 30s.
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
      showChat(true);
      if (!hasJoinedOnce) el.transcript.innerHTML = "";
      online = new Set();
      renderUsers();
      awaitingJoin = true;
      sendRaw({ cmd: "join", channel: myChannel, nick: myNick });
      appendRow({
        text: `${hasJoinedOnce ? "rejoining" : "joining"} #${myChannel} as ${myNick}`,
        kind: "sys",
      });
      // Only focus on the first join; refocusing on an auto-rejoin pops the
      // keyboard on mobile.
      if (!hasJoinedOnce) el.message.focus();
    };

    sock.onmessage = (ev) => {
      if (sock !== ws) return;
      let data;
      try { data = JSON.parse(ev.data); }
      catch { appendRow({ text: String(ev.data), kind: "sys" }); return; }
      if (data && data.cmd === "onlineSet") {
        // Join confirmed, so reset the backoff.
        awaitingJoin = false;
        retryAttempt = 0;
        hasJoinedOnce = true;
      }
      handleMessage(data);
      if (awaitingJoin && data && data.cmd === "warn") {
        // Join rejected. Without this we'd sit "connected" but not in the channel.
        awaitingJoin = false;
        const retryable = RETRYABLE_JOIN_WARN.test(data.text || "") &&
          (hasJoinedOnce || retryAttempt < FIRST_JOIN_MAX_RETRIES);
        if (retryable) {
          dropSocket();
          scheduleReconnect();
        } else {
          wantConnected = false;
          dropSocket();
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
      if (!wantConnected) {
        setStatus("disconnected", "off");
        return;
      }
      appendRow({ text: "connection closed, will retry", kind: "sys" });
      scheduleReconnect();
    };
  }

  function connect(channel, nick) {
    myChannel = channel || DEFAULT_CHANNEL;
    myNick = nick || DEFAULT_NICK;
    el.metaChannel.textContent = myChannel;
    el.metaNick.textContent = myNick;
    wantConnected = true;
    retryAttempt = 0;
    hasJoinedOnce = false;
    openSocket();
  }

  function reconnectNowIfNeeded() {
    if (!wantConnected || socketIsLive()) return;
    openSocket();
  }

  // Mobile browsers kill sockets in background tabs, so reconnect right
  // away when the tab comes back instead of waiting out the backoff.
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "visible") reconnectNowIfNeeded();
  });
  window.addEventListener("online", reconnectNowIfNeeded);

  el.joinForm.addEventListener("submit", (e) => {
    e.preventDefault();
    connect(el.channel.value.trim(), el.nick.value.trim());
  });

  el.disconnect.addEventListener("click", () => {
    wantConnected = false;
    clearRetry();
    dropSocket();
    showChat(false);
    setStatus("disconnected", "off");
  });

  el.sendForm.addEventListener("submit", (e) => {
    e.preventDefault();
    if (sendChatText(el.message.value)) el.message.value = "";
  });

  el.taskForm.addEventListener("submit", (e) => {
    e.preventDefault();
    const fd = new FormData(el.taskForm);
    const payload = {
      type: "task",
      id: shortId(),
      to: "chief",
      title: String(fd.get("title") || "").trim(),
      body: String(fd.get("body") || "").trim(),
    };
    if (sendChatText(JSON.stringify(payload))) el.taskForm.reset();
  });

  el.opinionForm.addEventListener("submit", (e) => {
    e.preventDefault();
    const fd = new FormData(el.opinionForm);
    const payload = {
      type: "opinion",
      from: "muse",
      topic: String(fd.get("topic") || "").trim() || "general",
      text: String(fd.get("text") || "").trim(),
    };
    if (sendChatText(JSON.stringify(payload))) el.opinionForm.reset();
  });

  el.resultForm.addEventListener("submit", (e) => {
    e.preventDefault();
    const fd = new FormData(el.resultForm);
    const payload = {
      type: "result",
      id: String(fd.get("id") || "").trim(),
      from: "muse",
      status: String(fd.get("status") || "done"),
      summary: String(fd.get("summary") || "").trim(),
    };
    if (sendChatText(JSON.stringify(payload))) el.resultForm.reset();
  });
})();
