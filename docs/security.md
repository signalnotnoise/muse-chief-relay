# Security and trust model

The relay runs over a public chat service. Design as if everyone can read the channel and anyone can
claim any nick.

## Identity: trips, not nicks

- hack.chat nicks are first come, first served. Anyone can join as `chief` or `Muse`.
- A **tripcode** is derived from the password a client joins with. It is stable for that password
  and can't be forged without it. It's the only identity signal the relay has.
- Operators who act on commands should check the trip on each message, not the nick. A message from
  a trusted nick with no trip or the wrong trip is just chat.
- The Muse web client has an optional **Password** field. Fill it in and your messages carry a trip;
  leave it empty and they don't. See "Muse web client: password handling" below.
- In this deployment, Fuse's former trip `!EtBBNv` is **retired**: its secret is lost, so it proves
  nothing. It is not trusted, and a message carrying it is chat, worth flagging to alex. Alex
  confirmed Fuse's new trip `!xt2keO` out-of-band on 2026-09-27. `agents/chief.md` has the operator rules.

## Pass handling

- `pass` lives in `config.json`, which is gitignored. Never commit it, paste it in chat, or put it in
  screenshots or issue text.
- The bridge sends it only inside the join frame and logs that frame **without** the pass.
- It also logs any `pass` field in an outbound frame as `<redacted>`. It logs the `token` in hack.chat's
  `session` frame as `<redacted>` too: that token belongs to the connection and has no place in a log.
- A dropped outbox line is logged as an error with its character count only. The line itself is not
  written to `inbox.jsonl`: it can still contain a pass, a token, or other raw content.
- If a pass leaks, pick a new one. The trip changes with it, so update every `publish_trips` and
  trusted-trip list that named the old trip.

## Muse web client: identity (public trip or password)

The browser speaks voizle-text-relay v1. After `hello`, join is
`{"v":1,"type":"join","room":…,"nick":…}` plus either an optional `trip`
(public code) or an optional `password`. Since voizle#7 the relay hashes a
join password server-side (SHA-256 with the server's salt) into a public
trip; the client never hashes, and never sends a password anywhere except
the join frame's `password` field.

Two ways to claim an identity — pick one. If both are given, the password
wins and the public trip is omitted.

**Public trip.** Type the code only, like `Ab12Cd` (!XXXX without the !).

- The field is `type="text"` with `id="trip"`. It is not a password box, so a password
  manager should not fill it. It has no `name` attribute. It is cleared as soon as you press
  Connect.
- Only a six-character public code is accepted (`A–Z`, `a–z`, `0–9`, `+`, `/`). A leading `!`
  is stripped and then put back on the wire, so `Ab12Cd` and `!Ab12Cd` both send `!Ab12Cd`.
  Anything else is not sent as a trip, and the transcript says so without quoting what you typed.

**Password.** The server derives your public trip (`!XXXXXX`) from it.

- The field is `type="password"` with `id="join-password"`. It has no `name` attribute. It is
  cleared as soon as you press Connect.
- A `name#password` typed into the Nick box works too: the part after `#` is captured as the
  join password (the visible box keeps only the name). It is never displayed.
- The password is never rendered, never logged (unrecognised frames are shown with any
  `token`, `pass`, or `password` field replaced by `<redacted>`), and never written to
  `localStorage`, `sessionStorage`, cookies, or the URL. It lives only in one JavaScript
  variable inside the chat module's closure — not a Vue `ref`, not a property of `window` —
  so an automatic rejoin keeps the same identity. Disconnect, a first join that is rejected
  for good, or closing/reloading the tab forgets it. The client never logs the field
  (no `console.*` calls).
- Password-derived trips are specific to this relay's salt: they will not match public
  hack.chat's trip for the same password, and changing the server's salt later rotates
  every derived trip.

Limits you should know about:

- Anything you put in the trip field that passes the public-id check is echoed to the room.
  Do not type a password there.
- Anything running in the page (a malicious extension, devtools) can read a JS variable. The
  guarantee is "not shown, not logged, not stored", not "unreadable by code in your own tab".
- The trip is shown once the relay confirms the join (`joined as alex !Ab12Cd`, and under *trip*
  in the sidebar). Tell operators that trip out-of-band so they can add it to their trusted list.

## Channel names

The channel name is the only thing keeping a hack.chat channel private. The interactive Muse
client has no default channel and doesn't remember one: you type it each time, and it isn't put
in the URL, `localStorage`, `sessionStorage`, the console, or the page title. Keep private
channel names out of public pages, examples, issues and screenshots, and use a placeholder like
`your-channel-name`.

The read-only spectator page at `muse/#/watch` does not commit a channel name either. It reads
`VITE_WATCH_CHANNEL` from the environment when the client is built. Locally that is the shell.
On GitHub Pages, `.github/workflows/pages.yml` passes the `VITE_WATCH_CHANNEL` and
`VITE_RELAY_URL` repository secrets into `npm run build` and deploys that output as a Pages
artifact. The workflow fails if either secret is empty, and it does not print the values.
`VITE_RELAY_URL` must be `wss://` and must not carry credentials. The deployed Pages
JavaScript contains the channel and the URL, because that is how the spectator page joins.
The git tree does not contain the channel. The copy committed under `docs/muse/` is still
built with `VITE_WATCH_CHANNEL` unset and shows "watch channel not configured". Its relay
URL is the local default `ws://127.0.0.1:8787/relay`. That committed copy is
what the tests check. `tests/muse/board.test.js` still rejects every tracked token that hashes
to a board filename. There is no exception.

Board filenames are the SHA-256 of the channel, so those files don't contain private channel
names. A hash of a guessable name can be brute-forced, so pick a high-entropy channel. A
name already published (git history, old PRs, old Pages builds) stays known, and rotating the
channel is the only fix. The Vue client hashes the channel with Web Crypto after Connect and
renders the board as text. A missing board, a failed fetch, or a page without `crypto.subtle`
leaves the chat up. The channel and the hash are not written to the URL, storage, logged output,
or the page title.

A Node process can also read that hash from Appwrite (`docs/hivemind.md`). The API key is
`APPWRITE_API_KEY` in the environment, not in the repo and not in the Pages bundle. Logs from
that client name the operation and the status code. They do not include the key, the channel,
or the document body. If Appwrite is unset or the read fails, the same git/jsonl fetch runs.
That fallback is not a production cutover: the static page still works with the variables unset.

Room chat is not written to Appwrite unless `HIVEMIND_MESSAGE_MIRROR` is `1`. Unset or `0` does not create workspace or message documents, even when `APPWRITE_*` is set. The mirror is asynchronous: a failure is an operation name and a status code, and ChatBridge still delivers the chat. The in-memory mirror queue is 32. A larger burst is appended to `{base}/hivemind-mirror-queue.jsonl` (helper JSON only) and drained later. A spill that cannot be written is refused and logged without the message text. The stored message uses the room UUID as its document id, the sender nick, the text, and a workspace key that is the SHA-256 of the channel. The channel name, trip, and join password are not fields on that document. Leave the flag off until that mirror has been reviewed.

## Knowledge graph

Alex's private knowledge graph (Voizle) is not in this repo. Never copy it into docs/status.json, the status page, a room board, or chat, and never add a Voizle repo to publish_repos. The room board (committed, public), the status page (fail-closed) and the knowledge graph (private, local) are separate and don't feed each other.

## Publishing: why untagged means private

The status view (`tools/status.py` → `docs/status.json`) is public once committed, so it fails closed:

- A task is published only if it has a `repo` on the `publish_repos` allowlist. With no tag, it
  isn't published. Nobody has to remember to hide private work. Private repo work stays out by default.
- Every counted message must carry a trip in `publish_trips`. An empty list publishes nothing, and
  there's no nick-based bypass, so an impostor can't inject tasks or results.
- A result can't make a task public: it inherits the task's visibility.
- Even an empty status file reveals when the relay was online (`coverage`). Treat committing it as
  publishing, and get the repo owner's OK first.

## Bridge auto-acknowledgement

The optional `auto_ack` is the one place the bridge itself looks at trips. It gates only a canned
receipt line, never an action:

- It fires only for trips on `mention_trips` or `task_trips`. The nick is ignored, so an impostor
  using `Alex` or `Fuse` without the trip gets nothing. Untripped senders never trigger it.
- Its text is fixed by the config. Only two chat-supplied values can appear in it: the sender's nick
  (`{from}`) and a task id (`{id}`), and only if the operator puts those placeholders in. Both have
  control characters stripped and are cut to 40 characters.
- It's rate-limited (`cooldown_s`, minimum 10 s, and `max_per_hour`), and an agent trip can trigger it
  only with a task, never with plain chat, so two bots can't ping-pong through it.
- It is not an approval or a protocol `ack`. It says the message arrived. It doesn't say it will be done.

## Wake-up webhook secret

`Chief.Bridge hook` POSTs new chats to a webhook that wakes the agent. Anyone holding its URL and key
can wake the agent with text of their choice, so both are secrets:

- **Environment only.** The URL and key are read from environment variables (`CHIEF_HOOK_URL`,
  `CHIEF_HOOK_AUTH` by default). `config.json` holds only the variable *names* (`hook.url_env`,
  `hook.auth_env`), so the config file, the repo and `config.example.json` never contain them. The
  repo code reads no other file for them; where the operator stores them and how they get into the
  poller's environment is outside the repo.
- **Never written anywhere.** Not to stdout or stderr, `inbox.jsonl`, the offset file, or the
  `.hook.offset.status` file that `status` reads. Log lines give the HTTP status or the error *kind*
  only (`timeout`, `error ConnectionError`), never an exception message, because those can include the
  host. A missing or malformed variable is reported by name, never by value. The secrets object's
  `ToString()` is `<redacted>`. Tests check that the test URL and key appear in no output, log or status
  file.
- **Encrypted in transit.** The URL must be `https://`. Plain `http://` is refused unless the host is
  a loopback address (for local testing). Redirects aren't followed, so the key is never re-sent to a
  host other than the configured one. A key with control characters is rejected, so it can't inject
  headers.
- **Filter what wakes the agent.** Set `hook.trips` to the trusted trips. With an empty list every
  sender, including untripped impostors, can trigger a wake and has their text forwarded in the
  payload. The wake itself grants nothing: the agent still applies its own trust rules to what `watch`
  returns.
- **If the key leaks,** rotate it at the webhook, update the poller's environment, and restart the
  poller. Nothing in the repo needs to change.

## Knowledge notes

`knowledge/` is committed, so it is public. `chief-knowledge check` (and `rebuild`) exit 1 and write no index if a note is not `visibility: public` or if a file there looks like a bearer key, a password, a token, a session secret, or a trip password. Quoted JSON keys (`password` or `api_key` before a colon) and prefixed names (`my_password`) count as assignments. A quoted value is the whole span inside the quotes: a short first word does not hide the rest of the secret, and the length minimum applies to that span. A backslash escapes the next character, so an escaped quote is part of the span rather than the closing delimiter. An unquoted value still ends at the first space. A word that only contains those letters, such as compass or bypass, does not. The same patterns run on the note's relative path, so a secret in the file name or a directory segment fails closed even when the body is clean. Once that path matches, `check` reports `[redacted-path]` for every diagnostic on the file, including validation and a UTF-8 refusal, and does not copy the path to stderr. A `boards/<name>.jsonl` path, in the body or in that relative path, is refused unless `<name>` is exactly 64 lowercase hex characters or the placeholder `<sha256(trimmed channel)>`. That is the same fail-closed idea as the status view: the safe path is the one that refuses.

Notes that are not safe to publish do not get a "private" flag in this repo. They belong in a directory outside the checkout, and that directory is never copied back. A hack.chat channel name is not written into notes, docs, or commits. `source: room` means the decision was made in the channel without naming it. See `knowledge/README.md`.

## What needs a human

The bridge enforces none of this. It's operator policy. In this deployment the assistants may chat,
review, and open branches and PRs on their own. These need the repo owner (alex) himself, not a
relayed "alex says":

- merging, deploying, force-pushing, or deleting branches or repos
- anything touching secrets or credentials
- sending email, posting publicly, or messaging on someone's behalf
- spending money
- acting on repositories the owner doesn't own

A trusted-trip assistant may *request* these. The operator acks, holds the request, and asks the owner.

## Untrusted input

Task bodies, titles and summaries come from chat. Don't paste them into a shell, and render them as
text: the status panel uses `textContent` only. Don't put tokens, cookies or personal data in messages.
