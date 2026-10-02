# Agent spawn pattern (Muse's setup)

Standing requirement from Alex (2026-10-01): every time he asks Muse to
spawn an agent, it follows this pattern exactly. The Design agent is the
reference implementation.

## The pattern

1. **Dedicated side chat with an expert persona.** Create a Muse side chat
   named for the agent. Give it a domain-expert persona (e.g. Design's:
   "software engineer focused on UX, expert in Vue 3.6, Headless UI,
   Tailwind CSS — opinionated, concrete, never generic"). The persona is set
   with an instruction message in the chat; every response carries that
   voice.
2. **Room → side chat pipe (one-way).** If the agent has a relay-room nick,
   its room messages are forwarded into the side chat with a
   `Relay room · <time>` attribution header. Replies in the side chat stay
   1:1 — they never post back into the room.
3. **The side-chat agent responds, not just relays.** When a forwarded
   message arrives, the agent engages with it substantively in its expert
   voice — what works, what risks, what it would do differently.
4. **Shared context file.** A running markdown log (e.g.
   `~/workspace/design-bot/design-context.md`) mirrors the agent's room
   messages and the side-chat conversation. It is the agent's memory across
   ephemeral workers.
5. **Workers brief from context first.** Every worker spawned to handle the
   agent's work reads the context file *before* acting, receives the latest
   entries embedded in its wake payload, and appends a summary of what it
   did back to the file. No worker ever starts cold.

## Reference: Design's forward pipe

The Hatch hook runtime is poll-based (minimum 5s interval) — there is no
push trigger into the chat API, which only the main agent holds. So the
pipe is: hook script (5s poll) → worker → instant handoff to the main
agent → `chat.send_message` into the side chat. Detection polls;
everything after is event-driven. End to end is typically 15–30 seconds.

- Hook script watches ChatBridge's room `inbox.jsonl` for fresh inbound
  chat frames from the agent's nick (staleness guard, join-intro reposts
  skipped, offset written before `wake`). `Chief.Bridge` is the
  compatibility launch of that same process. The per-agent mention inbox
  under `{base}/agents/<id>/` is a separate file and exists only when
  `mentions.enabled` is true. A sender trip on `chatbridge.inbox.wake` is
  untrusted evidence. This pipe does not copy a side chat back into the room.
- The hook worker has **no** chat tools in its runtime — its prompt tells
  it to report a `FORWARD_REQUEST` payload in its summary immediately
  instead of attempting the send.
- The main agent forwards via `chat.send_message`, appends the agent's
  message to the context file, and instructs the side-chat agent to append
  its response there too.

## Reference: Design's worker briefing

The mention-watch hook (`design-mention-watch`) wakes a worker when the
agent is addressed in the room. Its prompt:

- Step 0 reads the context file before anything else.
- The wake payload embeds the last ~3KB of context (`recent_context`).
- After handling mentions, the worker appends a one-line summary to the
  context file.
- Concurrency: a lock file with O_EXCL claim; the holder drains the whole
  task queue.

## Boundaries

- No room names, trips, passwords, tokens, or chat IDs in committed files.
  This doc uses placeholders; live values live in gitignored local files.
- The side chat is 1:1 with the user. Forwarding is room → side chat only.
- Workers never speak for Alex and never claim his approval.
