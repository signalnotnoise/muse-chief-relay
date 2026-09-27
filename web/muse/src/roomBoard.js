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
  const state = blankState();

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
