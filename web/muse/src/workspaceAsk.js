// Canonical "ask for a new workspace" line for the room.
//
// The intuitive way to start new work with the room's agents is one chat
// line, goal first: `workspace: <goal>`. The goal *is* the workspace — no
// name to invent, no settings. A room-side watcher turns the line into a
// real Workspaces document; the client just needs to compose the line
// exactly the same way whether it was typed or built by the goal sheet.

export const WORKSPACE_ASK_PREFIX = "workspace:";

/**
 * Collapse a raw goal to a single line and build the canonical ask line.
 * Returns null when the goal is empty.
 */
export function buildWorkspaceAsk(goal) {
  const flat = String(goal == null ? "" : goal)
    .replace(/\s+/g, " ")
    .trim();
  if (!flat) return null;
  return `${WORKSPACE_ASK_PREFIX} ${flat}`;
}

/**
 * Parse a chat line back into its goal, or null when it is not an ask line.
 * The prefix match is case-insensitive; the goal keeps its original casing.
 */
export function parseWorkspaceAsk(text) {
  const m = /^\s*workspace:\s*(.+?)\s*$/i.exec(String(text == null ? "" : text));
  if (!m) return null;
  const goal = m[1].trim();
  return goal ? goal : null;
}
