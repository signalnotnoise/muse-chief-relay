// Room board. Read-only, and only after Connect. The hash lives in this closure:
// not in the page URL, storage, logged output, or the page title. Disconnect drops it.
// boardLoadGen discards a load that finishes after a channel change or Disconnect.
// Automatic reconnects call openSocket and do not come through here.

import MuseBoard from "../board.js";

export const BOARD_PLACEHOLDER = "Join a channel to see its room board.";

export function formatBoardTs(ts) {
  const d = new Date(Number(ts) * 1000);
  if (!Number.isFinite(d.getTime())) return String(ts);
  return d.toLocaleString([], { dateStyle: "medium", timeStyle: "short" });
}

export function skippedLineText(n) {
  return n + (n === 1 ? " line skipped" : " lines skipped") + " (not a complete task, decision, or scratch).";
}

function blankState() {
  return {
    statusText: "",
    statusKind: "",
    viaText: "",
    reloadDisabled: true,
    message: BOARD_PLACEHOLDER,
    board: null,
  };
}

export function createRoomBoard(options = {}) {
  const api = options.board || MuseBoard;
  const pageUrl = options.pageUrl || (() => (typeof document !== "undefined" ? document.baseURI : ""));
  const now = options.now || (() => Date.now());

  let boardLoadGen = 0;
  let boardHash = null;
  let envReader;
  const state = blankState();

  // Prefer HIVEMIND when a reader is passed in, or when this process is Node
  // and APPWRITE_* is set. The browser bundle does not statically import the
  // server SDK. A miss, a bad env, or a thrown read falls through to git/jsonl.
  function runningInNode() {
    const proc = globalThis.process;
    return !!(proc && proc.versions && proc.versions.node && proc.env);
  }

  function reader() {
    if (options.hivemind === null) return Promise.resolve(null);
    if (options.hivemind && typeof options.hivemind.readBoard === "function") {
      return Promise.resolve(options.hivemind);
    }
    if (!runningInNode()) return Promise.resolve(null);
    const env = globalThis.process.env;
    if (!env.APPWRITE_ENDPOINT || !env.APPWRITE_PROJECT_ID || !env.APPWRITE_API_KEY) {
      return Promise.resolve(null);
    }
    if (!envReader) {
      const load = new Function("m", "return import(m)");
      envReader = load("node:module").then((nodeModule) => {
        const require = nodeModule.createRequire(import.meta.url);
        const mod = require("../hivemindClient.js");
        if (!mod || typeof mod.createHivemind !== "function") return null;
        return mod.createHivemind({ env: env });
      }).catch(() => null);
    }
    return envReader;
  }

  async function readHive(hash) {
    let hive;
    try {
      hive = await reader();
    } catch (e) {
      return null;
    }
    if (!hive || typeof hive.readBoard !== "function") return null;
    try {
      const result = await hive.readBoard(hash);
      if (!result || result.status !== "ok" || !result.board || !Array.isArray(result.board.tasks)) return null;
      return result;
    } catch (e) {
      return null;
    }
  }

  function snapshot() {
    return {
      statusText: state.statusText,
      statusKind: state.statusKind,
      viaText: state.viaText,
      reloadDisabled: state.reloadDisabled,
      message: state.message,
      board: state.board,
    };
  }

  function publish() {
    if (typeof options.onUpdate === "function") options.onUpdate(snapshot());
  }

  function showBoardMessage(message) {
    state.message = message;
    state.board = null;
  }

  function showBoardPlaceholder() {
    boardLoadGen++;
    boardHash = null;
    state.reloadDisabled = true;
    state.viaText = "";
    state.statusText = "";
    state.statusKind = "";
    showBoardMessage(BOARD_PLACEHOLDER);
    publish();
    return snapshot();
  }

  function showBoardFailure(message) {
    state.viaText = "";
    state.statusText = "unavailable";
    state.statusKind = "err";
    showBoardMessage(message);
    publish();
  }

  async function renderLoaded(gen, hash, cacheBust) {
    const hive = await readHive(hash);
    if (gen !== boardLoadGen) return snapshot();
    if (hive) {
      state.board = hive.board;
      state.message = "";
      state.viaText = " · " + (hive.via || "Appwrite") + " · " + hash.slice(0, 8);
      state.statusText = "read-only";
      state.statusKind = "ok";
      publish();
      return snapshot();
    }
    let sources;
    try {
      sources = api.boardSources(hash, pageUrl(), cacheBust || 0);
    } catch (e) {
      if (gen !== boardLoadGen) return snapshot();
      showBoardFailure("Couldn't read the room board.");
      return snapshot();
    }
    const result = await api.loadBoardText(sources, options.fetchImpl);
    if (gen !== boardLoadGen) return snapshot();
    if (result.status === "ok") {
      state.board = api.parseBoard(result.text);
      state.message = "";
      state.viaText = " · " + result.via + " · " + hash.slice(0, 8);
      state.statusText = "read-only";
      state.statusKind = "ok";
      publish();
      return snapshot();
    }
    state.viaText = "";
    if (result.status === "missing") {
      state.statusText = "no board";
      state.statusKind = "";
      showBoardMessage("No board for this channel yet.");
      publish();
      return snapshot();
    }
    showBoardFailure("Couldn't read the room board.");
    return snapshot();
  }

  async function startBoard(channel) {
    const gen = ++boardLoadGen;
    boardHash = null;
    state.reloadDisabled = true;
    state.viaText = "";
    state.statusText = "loading";
    state.statusKind = "";
    publish();
    let pending;
    try {
      if (!api || typeof api.boardFileFor !== "function") throw new Error("board unavailable");
      pending = api.boardFileFor(channel);
    } catch (e) {
      if (gen !== boardLoadGen) return snapshot();
      showBoardFailure("Couldn't read the room board.");
      return snapshot();
    }
    try {
      const hash = await Promise.resolve(pending);
      if (gen !== boardLoadGen) return snapshot();
      if (!hash) {
        showBoardFailure("Room board needs HTTPS or localhost.");
        return snapshot();
      }
      boardHash = hash;
      state.reloadDisabled = false;
      return await renderLoaded(gen, hash, 0);
    } catch (e) {
      if (gen !== boardLoadGen) return snapshot();
      showBoardFailure("Couldn't read the room board.");
      return snapshot();
    }
  }

  function reloadBoard() {
    if (!boardHash || state.reloadDisabled) return Promise.resolve(snapshot());
    const gen = ++boardLoadGen;
    const hash = boardHash;
    state.statusText = "loading";
    state.statusKind = "";
    state.viaText = "";
    publish();
    return Promise.resolve(renderLoaded(gen, hash, now())).catch(() => {
      if (gen !== boardLoadGen) return snapshot();
      showBoardFailure("Couldn't read the room board.");
      return snapshot();
    });
  }

  return {
    startBoard,
    reloadBoard,
    showBoardPlaceholder,
    getState: snapshot,
  };
}
