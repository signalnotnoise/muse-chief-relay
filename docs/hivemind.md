# HIVEMIND board, knowledge, and room-chat mirror

Room boards can be read from the Appwrite database `hivemind`. When that read is not configured or fails, Muse keeps using `boards/<sha256>.jsonl` (same-origin, then GitHub raw). The local `knowledge/` notes and the `chief-knowledge` index stay the source for the C# tool. This change does not deploy GitHub Pages, does not change a droplet, and does not turn the board into Appwrite-only.

Room chat can be mirrored into the same database. That mirror is **off by default**.

## Environment

Copy `.env.example`. Leave the key out of git.

| Variable | Value |
|---|---|
| `APPWRITE_ENDPOINT` | `https://appwrite.voizel.com/v1` |
| `APPWRITE_PROJECT_ID` | `6a6da0ae001f1d0582d2` |
| `APPWRITE_DATABASE_ID` | `hivemind` (default when unset) |
| `APPWRITE_API_KEY` | server-side only; never commit or log it |
| `HIVEMIND_MESSAGE_MIRROR` | `1` writes accepted room chats. Unset or `0` writes nothing, even when `APPWRITE_*` is set. Default is off. |

The static Pages bundle does not include the key. With the variables unset, the page only reads git/jsonl. Do not set `HIVEMIND_MESSAGE_MIRROR=1` in a deployed environment until Fuse has reviewed the mirror and Alex has said go.

## Behavior

- Board identity is the lowercase hex SHA-256 of the trimmed channel. The channel name is not a field, a log line, or a URL.
- A Node process with the variables set, and any caller that passes `{ hivemind }` into `createRoomBoard`, tries Appwrite first (`cards` for that `boardKey`, then the `boards` document when there are no cards). `web/muse/hivemindClient.js` logs `hivemind <operation> failed (<code>)` and returns. The panel then loads git/jsonl. Chat is not stopped.
- An empty miss (no board document and no cards) also uses git/jsonl. A board document with no cards is an empty Appwrite board.
- Writes are create-or-verify for `boards`, `cards`, and `knowledge`. The document id is `s1_` plus the first 32 hex digits of SHA-256 over `collection + NUL + identity` (board: the hash; card: `hash:seq`; note: the slug). The same bytes are left in place. Different bytes are not updated or deleted.
- `readKnowledge(slug)` and `searchKnowledge(term)` read public notes. A note whose `visibility` is not `public` is not returned. The local index is not rebuilt or removed.

## Room chat mirror

ChatBridge is the observer. After a chat frame is appended to `inbox.jsonl`, and only when `HIVEMIND_MESSAGE_MIRROR` is `1`, the bridge enqueues one JSON object for `node web/muse/messageMirror.js`. The enqueue returns immediately. A helper failure is a log line. It does not drop the chat, skip the inbox, or stop the bridge. Unset, `0`, or any value other than `1` does not start the helper and does not create workspace or message documents.

The helper uses the existing Node Appwrite client (`web/muse/hivemindClient.js`). There is no second client.

| | |
|---|---|
| Workspace `key` | Lowercase SHA-256 hex of the trimmed channel, same as a board key. A caller may also pass a slug. The channel name is not stored. `name` and `description` are left unset. |
| Workspace `createdTs` | Epoch seconds. First write wins. A later chat that finds the workspace already stored leaves that timestamp in place and is not a conflict. |
| Message document id | The room message UUID (`id` on the chat frame). A create that conflicts (409, or the document is already there) is success: the stored document is left as-is. |
| Message fields | `workspaceKey`, `threadKey` (default `room`), `sender` (nick only), `text`, `ts` (epoch seconds). Voizle chat `ts` is Unix milliseconds (`Date.now()`). A value above 100000000000 and at most 100000000000000 is stored as whole seconds. A hack.chat `time` already in seconds is unchanged. A larger value is refused. |
| Text | Longer than 8192 characters is refused. It is not truncated and not written. |
| Left out | Trip, join password, pass, token, and the channel name. |

Retries are bounded (the helper tries a few times, then stops). The bridge keeps at most 32 chats in memory while the helper runs (one Node process at a time). A larger burst is not dropped. Each extra offer is appended to `{base}/hivemind-mirror-queue.jsonl` and drained when a memory slot is free. That file is one helper JSON object per line (`id`, `workspaceKey`, `sender`, `text`, `ts`, `threadKey`). It does not hold the channel name, trip, or join password. A restart reads it again from the start. Appwrite dedup (the room UUID; a 409 leaves the stored document) makes that replay safe. A torn tail is skipped when it is not JSON. If the file cannot be written, that offer is refused and logged, and the chat path still returns. The Node helper's own pending list is also 32, but the bridge starts one process per offer, so a burst does not fill it.

Logs stay `hivemind message mirror deferred (queue)` (kept on disk), `hivemind message mirror refused (<reason>)`, or `hivemind <operation> failed (<code>)`. They do not include the API key, the channel, or the message text.

The helper is `node web/muse/messageMirror.js` on the bridge host (`HIVEMIND_MIRROR_NODE` and `HIVEMIND_MIRROR_SCRIPT` override the binary and the script path). It needs the `web/muse` dependencies, including `node-appwrite`. With the flag left at `0`, the bridge never starts that process. A chat whose frame `id` is not a UUID is refused and not written. The relay has to stamp that id; this repo does not invent one.

`node --test tests/muse/` covers the soft-fail and the mocked happy path, including the mirror gate. Those tests do not call the live database.
