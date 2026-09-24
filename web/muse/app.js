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
  let intentionalClose = false;

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
    if (!t) return;
    sendRaw({ cmd: "chat", text: t });
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

  function connect(channel, nick) {
    intentionalClose = false;
    myChannel = channel || DEFAULT_CHANNEL;
    myNick = nick || DEFAULT_NICK;
    el.metaChannel.textContent = myChannel;
    el.metaNick.textContent = myNick;
    setStatus("connecting…", "off");

    if (ws) {
      try { ws.close(); } catch (_) { /* ignore */ }
    }

    ws = new WebSocket(WS_URL);

    ws.onopen = () => {
      setStatus("connected", "on");
      showChat(true);
      el.transcript.innerHTML = "";
      online = new Set();
      renderUsers();
      sendRaw({ cmd: "join", channel: myChannel, nick: myNick });
      appendRow({ text: `joining #${myChannel} as ${myNick}`, kind: "sys" });
      el.message.focus();
    };

    ws.onmessage = (ev) => {
      let data;
      try { data = JSON.parse(ev.data); }
      catch { appendRow({ text: String(ev.data), kind: "sys" }); return; }
      handleMessage(data);
    };

    ws.onerror = () => setStatus("error", "err");

    ws.onclose = () => {
      setStatus("disconnected", intentionalClose ? "off" : "err");
      if (!intentionalClose) {
        appendRow({ text: "connection closed", kind: "sys" });
      }
    };
  }

  el.joinForm.addEventListener("submit", (e) => {
    e.preventDefault();
    connect(el.channel.value.trim(), el.nick.value.trim());
  });

  el.disconnect.addEventListener("click", () => {
    intentionalClose = true;
    if (ws) ws.close();
    showChat(false);
    setStatus("disconnected", "off");
  });

  el.sendForm.addEventListener("submit", (e) => {
    e.preventDefault();
    const text = el.message.value;
    el.message.value = "";
    sendChatText(text);
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
    sendChatText(JSON.stringify(payload));
    el.taskForm.reset();
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
    sendChatText(JSON.stringify(payload));
    el.opinionForm.reset();
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
    sendChatText(JSON.stringify(payload));
    el.resultForm.reset();
  });
})();
