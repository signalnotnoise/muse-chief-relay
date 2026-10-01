# HIVEMIND board and knowledge read

Room boards can be read from the Appwrite database `hivemind`. When that read is not configured or fails, Muse keeps using `boards/<sha256>.jsonl` (same-origin, then GitHub raw). The local `knowledge/` notes and the `chief-knowledge` index stay the source for the C# tool. This change does not deploy GitHub Pages, does not change a droplet, and does not turn the board into Appwrite-only.

## Environment

Copy `.env.example`. Leave the key out of git.

| Variable | Value |
|---|---|
| `APPWRITE_ENDPOINT` | `https://appwrite.voizel.com/v1` |
| `APPWRITE_PROJECT_ID` | `6a6da0ae001f1d0582d2` |
| `APPWRITE_DATABASE_ID` | `hivemind` (default when unset) |
| `APPWRITE_API_KEY` | server-side only; never commit or log it |

The static Pages bundle does not include the key. With the variables unset, the page only reads git/jsonl.

## Behavior

- Board identity is the lowercase hex SHA-256 of the trimmed channel. The channel name is not a field, a log line, or a URL.
- A Node process with the variables set, and any caller that passes `{ hivemind }` into `createRoomBoard`, tries Appwrite first (`cards` for that `boardKey`, then the `boards` document when there are no cards). `web/muse/hivemindClient.js` logs `hivemind <operation> failed (<code>)` and returns. The panel then loads git/jsonl. Chat is not stopped.
- An empty miss (no board document and no cards) also uses git/jsonl. A board document with no cards is an empty Appwrite board.
- Writes are create-or-verify for `boards`, `cards`, and `knowledge`. The document id is `s1_` plus the first 32 hex digits of SHA-256 over `collection + NUL + identity` (board: the hash; card: `hash:seq`; note: the slug). The same bytes are left in place. Different bytes are not updated or deleted.
- `readKnowledge(slug)` and `searchKnowledge(term)` read public notes. A note whose `visibility` is not `public` is not returned. The local index is not rebuilt or removed.

`node --test tests/muse/` covers the soft-fail and the mocked happy path. Those tests do not call the live database.
