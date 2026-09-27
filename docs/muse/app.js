(() => {
  const WS_URL = "wss://hack.chat/chat-ws";
  const DEFAULT_NICK = "Muse";

  const el = {
    status: document.getElementById("status"),
    joinPanel: document.getElementById("join-panel"),
    chatPanel: document.getElementById("chat-panel"),
    joinForm: document.getElementById("join-form"),
    channel: document.getElementById("channel"),
    channelError: document.getElementById("channel-error"),
    nick: document.getElementById("nick"),
    password: document.getElementById("password"),
    transcript: document.getElementById("transcript"),
    sendForm: document.getElementById("send-form"),
    message: document.getElementById("message"),
    users: document.getElementById("users"),
    metaChannel: document.getElementById("meta-channel"),
    metaNick: document.getElementById("meta-nick"),
    metaTrip: document.getElementById("meta-trip"),
    disconnect: document.getElementById("disconnect"),
    taskForm: document.getElementById("task-form"),
    opinionForm: document.getElementById("opinion-form"),
    resultForm: document.getElementById("result-form"),
  };

  let ws = null;
  let myNick = DEFAULT_NICK;
  // No default channel: the user has to type one (the same one Chief joins).
  let myChannel = "";
  let online = new Set();

  // The trip password. It lives only in this closure variable, so an automatic
  // rejoin (backoff or reconnect-on-focus) keeps the same trip. It is never put
  // in the DOM, never logged, and never written to localStorage, sessionStorage
  // or the URL. The password field is cleared as soon as Connect is pressed.
  // Disconnect, a first join that is rejected for good, or closing the tab forgets it.
  // A drop after a successful join keeps it: that rejoin retries until Disconnect.
  let myPassword = "";
  // Our own trip as hack.chat reports it in onlineSet ("" when untripped).
  let myTrip = "";

  // Reconnect state. wantConnected is true between Connect and Disconnect.
  // A socket that closes while it's still true gets retried with backoff.
  const BACKOFF_BASE_MS = 1000;
  const BACKOFF_MAX_MS = 30000;
  let wantConnected = false;
  let retryAttempt = 0;
  // Join warnings only. Socket closes use retryAttempt for backoff and must not
  // spend the first-join cap.
  let firstJoinWarns = 0;
  let retryTimer = null;
  let hasJoinedOnce = false;
  let awaitingJoin = false;

  // Join warnings are decided in reconnect.js (kept identical under web/muse).
  // After a successful join, every warn is retried: the nick is usually our own
  // stale session, or the server is rate-limiting. On the very first join, a
  // taken nick or a rate limit gets a few tries (a reload can race its own
  // ghost session); any other warn is bad input and stops.

  el.channel.value = "";
  el.nick.value = DEFAULT_NICK;

  function showChannelError(on) {
    el.channelError.classList.toggle("hidden", !on);
    if (on) el.channel.setAttribute("aria-invalid", "true");
    else el.channel.removeAttribute("aria-invalid");
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

  // The nick sent on the wire. Only the join frame ever carries the password.
  function joinNick() {
    return myPassword ? myNick + "#" + myPassword : myNick;
  }

  function forgetPassword() {
    myPassword = "";
    el.password.value = "";
  }

  function setTrip(trip) {
    myTrip = trip || "";
    el.metaTrip.textContent = myTrip ? "!" + myTrip : "none";
  }

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

  // Fields that must never be rendered. hack.chat's "session" frame carries a
  // resumable session token: anyone holding it can restore a session with our
  // nick and trip without knowing the password, so it's as sensitive as the
  // password itself.
  const SECRET_KEYS = new Set(["token", "pass", "password"]);
  function redact(key, value) {
    return SECRET_KEYS.has(key) ? "<redacted>" : value;
  }

  function handleMessage(data) {
    const cmd = data && data.cmd;
    if (cmd === "session") {
      // Nothing useful to show, and the token must stay off the screen.
      return;
    }
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
      appendRow({ text: data.text || JSON.stringify(data, redact), kind: "sys" });
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
    // Unknown / other. Redact anything credential-like before showing it.
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
      sendRaw({ cmd: "join", channel: myChannel, nick: joinNick() });
      // Echo the name only. myNick never contains the password.
      appendRow({
        text: `${hasJoinedOnce ? "rejoining" : "joining"} #${myChannel} as ${myNick}` +
          (myPassword ? " (with a trip password)" : ""),
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
      let joinedNow = false;
      if (data && data.cmd === "onlineSet") {
        // Join confirmed, so reset the backoff.
        awaitingJoin = false;
        retryAttempt = 0;
        firstJoinWarns = 0;
        joinedNow = true;
      }
      handleMessage(data);
      if (joinedNow) {
        const rejoin = hasJoinedOnce;
        hasJoinedOnce = true;
        // hack.chat marks our own entry with isme and includes the trip it
        // computed from the password ("" or missing when there's none).
        const me = Array.isArray(data.users) ? data.users.find((u) => u && u.isme) : null;
        setTrip(me && typeof me.trip === "string" ? me.trip : "");
        const who = myTrip ? `${myNick} !${myTrip}` : `${myNick} (no trip)`;
        appendRow({ text: `${rejoin ? "rejoined" : "joined"} as ${who}`, kind: "sys" });
      }
      if (awaitingJoin && data && data.cmd === "warn") {
        // Join rejected. Without this we'd sit "connected" but not in the channel.
        awaitingJoin = false;
        const warned = MuseReconnect.recordJoinWarn(data.text || "", hasJoinedOnce, firstJoinWarns);
        firstJoinWarns = warned.firstJoinWarns;
        if (warned.decision === "retry") {
          dropSocket();
          scheduleReconnect();
        } else {
          wantConnected = false;
          dropSocket();
          // Nothing will rejoin now, so there's no reason to keep the password.
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
      if (MuseReconnect.onSocketClose(wantConnected) === "stop") {
        setStatus("disconnected", "off");
        return;
      }
      appendRow({ text: "connection closed, will retry", kind: "sys" });
      scheduleReconnect();
    };
  }

  function connect(channel, rawNick, password) {
    const { name, secret } = splitNick(rawNick);
    myChannel = channel;
    myNick = name || DEFAULT_NICK;
    // The password field wins; otherwise fall back to a legacy "name#pw" nick.
    myPassword = password || secret;
    el.metaChannel.textContent = myChannel;
    el.metaNick.textContent = myNick;
    myTrip = "";
    el.metaTrip.textContent = "—"; // until hack.chat confirms the join
    wantConnected = true;
    retryAttempt = 0;
    firstJoinWarns = 0;
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

  // Typing (or pasting) "name#..." into the Nick box moves everything after the
  // "#" into the password field and focuses it, so the rest of the password is
  // typed masked instead of in plain sight.
  el.nick.addEventListener("input", () => {
    const v = el.nick.value;
    const i = v.indexOf("#");
    if (i < 0) return;
    const rest = v.slice(i + 1);
    el.nick.value = v.slice(0, i);
    if (rest) el.password.value = rest;
    el.password.focus();
  });

  el.channel.addEventListener("input", () => {
    if (el.channel.value.trim()) showChannelError(false);
  });

  el.joinForm.addEventListener("submit", (e) => {
    e.preventDefault();
    const channel = el.channel.value.trim();
    if (!channel) {
      // Refuse a blank join. The password (if any) stays in its masked field.
      showChannelError(true);
      el.channel.focus();
      return;
    }
    showChannelError(false);
    const password = el.password.value;
    // Clear the field right away. The password stays in memory only (myPassword).
    el.password.value = "";
    connect(channel, el.nick.value, password);
    // If the Nick box still held "name#pw" (autofill, or the input handler
    // didn't run), leave only the name in it.
    if (el.nick.value.includes("#")) el.nick.value = myNick;
  });

  el.disconnect.addEventListener("click", () => {
    wantConnected = false;
    clearRetry();
    dropSocket();
    forgetPassword();
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
    // Optional. Leave it blank for anything that shouldn't show up on the public status view.
    // Never tag Voizle. The Voizle knowledge graph stays off this repo and off the status page.
    const repo = String(fd.get("repo") || "").trim();
    if (repo) payload.repo = repo;
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
