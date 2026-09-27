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
- If a pass leaks, pick a new one. The trip changes with it, so update every `publish_trips` and
  trusted-trip list that named the old trip.

## Muse web client: password handling

The client sends the password to hack.chat exactly once per join, as the legacy
`{"cmd":"join","channel":…,"nick":"name#password"}` frame. hack.chat splits the nick at the first `#`
and derives the trip from the part after it. Beyond that frame, the client:

- never displays the password: the field is `type="password"` and is cleared as soon as you press
  Connect; every echo (join line, sidebar, online list) shows only the name;
- never logs it (no `console.*` calls) and never writes it to `localStorage`, `sessionStorage`,
  cookies or the URL. The join inputs have no `name` attribute. The form is rendered by the Vue
  app, so a failed script load is an empty page rather than a native submit that could put the
  field in a query string;
- keeps it in one JavaScript variable inside the chat module's closure. It is not a Vue `ref`
  and not a property of `window`, so an automatic rejoin after a dropped socket, a tab coming
  back into view or the network returning gets the **same trip**. Disconnect, a first join that
  is rejected for good (bad nick, or a taken nick that is still taken after a few tries), or
  closing/reloading the tab forgets it. A drop after a successful join does not: that rejoin
  keeps retrying, password included, until Disconnect.
- treats a legacy `name#password` typed into the Nick box the same way: as soon as the `#` is typed
  or pasted, the rest moves into the masked Password field and focus follows it. A `name#password`
  that reaches Connect without an input event (e.g. autofill) is split at submit, and the Nick box
  is reset to the name.

The client also never renders hack.chat's `session` frame. Its `token` lets whoever holds it restore
a session with your nick and trip without the password (hack.chat's `session` command), so it gets
the same treatment as the password. Other unrecognised frames are shown with any `token`, `pass` or
`password` field replaced by `<redacted>`.

Limits you should know about:

- hack.chat only uses the text **up to the next `#`** in the password (its legacy join splits with
  `split('#', 2)`). `abc#def` gives the same trip as `abc`, so don't put `#` in a trip password.
- Your browser's password manager may offer to save it (`autocomplete="current-password"`). That's
  your browser's store, not the page's; decline if you don't want it saved.
- Anything running in the page (a malicious extension, devtools) can read a JS variable. The
  guarantee is "not shown, not logged, not stored", not "unreadable by code in your own tab".
- The trip is shown once hack.chat confirms the join (`joined as alex !Ab12Cd`, and under *trip* in
  the sidebar). Tell operators that trip out-of-band so they can add it to their trusted list.

## Channel names

The channel name is the only thing keeping a hack.chat channel private. The Muse client has no
default channel and doesn't remember one: you type it each time, and it isn't put in the URL,
`localStorage`, `sessionStorage`, the console, or the page title. Keep real channel names out of
public pages, examples, issues and screenshots, and use a placeholder like `your-channel-name`.

Board filenames are the SHA-256 of the channel, so the repo and Pages don't contain the name.
A hash of a guessable name can be brute-forced, so pick a high-entropy channel. A name already
published (git history, old PRs, old Pages builds) stays known, and rotating the channel is the
only fix. The Vue client hashes the channel with Web Crypto after Connect and renders the board
as text. A missing board, a failed fetch, or a page without `crypto.subtle` leaves the chat up.
The channel and the hash are not written to the URL, storage, logged output, or the page title.

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
