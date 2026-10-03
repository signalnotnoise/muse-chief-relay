// Folder-style workspace view: the room's workspaces, derived from the
// transcript, rendered like folders. Opening one narrows the transcript to
// just that workspace's thread — unrelated chats are filtered out.
//
// Membership is deliberately simple and explainable: a workspace owns its
// `workspace: <goal>` ask line, the room-side watcher's goal card quoting
// that goal, and any message that contains the full goal text. Chat frames
// carry no server-side workspace tagging yet, so quoting the goal is the
// contract that puts a message inside the folder.

import { parseWorkspaceAsk } from "./workspaceAsk.js";

// The watcher's success card: New workspace: "<goal>" (asked by @<nick>) — …
// The DB-failure card ("tried to create the workspace … but the DB write
// failed") intentionally does NOT match — nothing was created.
export const WORKSPACE_CARD_RE =
  /^\s*New workspace:\s*"(.+?)"\s*\(asked by\s+@([A-Za-z0-9_.\-]+)\)/i;

/** Collapse a goal to its canonical comparable form. */
export function normalizeGoal(goal) {
  return String(goal == null ? "" : goal)
    .replace(/\s+/g, " ")
    .trim()
    .toLowerCase();
}

/**
 * Parse the room-side watcher's goal card back into {goal, requester},
 * or null when the line is not a goal card.
 */
export function parseWorkspaceCard(text) {
  const m = WORKSPACE_CARD_RE.exec(String(text == null ? "" : text));
  if (!m) return null;
  const goal = m[1].trim();
  if (!goal) return null;
  return { goal, requester: m[2] };
}

/**
 * Derive the workspace list from transcript rows. An ask line and the goal
 * card quoting the same goal merge into one entry; an ask with no card yet
 * is a pending workspace (requested, waiting on the room-side watcher).
 * Returns most-recent-first entries: {key, goal, requester, askId, cardId}.
 */
export function deriveWorkspaces(messages) {
  const byKey = new Map();
  for (const row of messages || []) {
    if (!row || typeof row.text !== "string") continue;
    const rowId = row.id != null ? row.id : null;

    const ask = parseWorkspaceAsk(row.text);
    if (ask != null) {
      const key = normalizeGoal(ask);
      if (!byKey.has(key)) {
        byKey.set(key, {
          key,
          goal: ask,
          requester: row.nick || null,
          askId: rowId,
          cardId: null,
        });
      }
      continue;
    }

    const card = parseWorkspaceCard(row.text);
    if (card) {
      const key = normalizeGoal(card.goal);
      const existing = byKey.get(key);
      if (existing) {
        if (existing.cardId == null) existing.cardId = rowId;
        if (!existing.requester) existing.requester = card.requester;
      } else {
        byKey.set(key, {
          key,
          goal: card.goal,
          requester: card.requester,
          askId: null,
          cardId: rowId,
        });
      }
    }
  }
  return [...byKey.values()].reverse();
}

/**
 * True when a transcript row belongs inside the workspace's folder: the ask
 * line, the goal card, or any message quoting the full goal.
 */
export function messageInWorkspace(row, workspace) {
  if (!row || !workspace) return false;
  if (row.id != null && (row.id === workspace.askId || row.id === workspace.cardId))
    return true;
  const goal = normalizeGoal(workspace.goal);
  if (!goal) return false;
  return String(row.text == null ? "" : row.text)
    .toLowerCase()
    .includes(goal);
}
