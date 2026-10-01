// Composer history for the chat input: ArrowUp recalls older sent messages,
// ArrowDown moves toward newer ones (and back to the unsent draft at the end).
// Pure and DOM-free so the node tests can drive it. Nothing is persisted:
// history lives in tab memory and dies with the page, like everything else
// in the muse client.

export function createComposerHistory(limit = 50) {
  let entries = [];
  // -1 means not browsing; otherwise an index into entries.
  let index = -1;
  // The unsent text that was in the box when browsing started.
  let draft = "";

  function push(text) {
    const t = String(text || "").trim();
    index = -1;
    draft = "";
    if (!t) return;
    if (entries[entries.length - 1] !== t) {
      entries.push(t);
      if (entries.length > limit) entries = entries.slice(entries.length - limit);
    }
  }

  // dir: -1 for ArrowUp (older), +1 for ArrowDown (newer). current is the
  // text currently in the box. Returns the text to show, or null when there
  // is nothing to browse (so the key keeps its native caret behavior).
  function step(dir, current) {
    if (!entries.length) return null;
    if (index === -1) {
      if (dir > 0) return null;
      draft = String(current || "");
      index = entries.length - 1;
      return entries[index];
    }
    if (dir < 0) {
      index = Math.max(0, index - 1);
      return entries[index];
    }
    index += 1;
    if (index >= entries.length) {
      index = -1;
      const back = draft;
      draft = "";
      return back;
    }
    return entries[index];
  }

  function cancel() {
    index = -1;
    draft = "";
  }

  function browsing() {
    return index !== -1;
  }

  function size() {
    return entries.length;
  }

  return { push, step, cancel, browsing, size };
}
