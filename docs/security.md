# Security and trust model

The relay runs over a public chat service. Design as if everyone can read the channel and anyone can
claim any nick.

## Identity: trips, not nicks

- hack.chat nicks are first come, first served. Anyone can join as `chief` or `Muse`.
- A **tripcode** is derived from the password a client joins with. It is stable for that password
  and can't be forged without it. It's the only identity signal the relay has.
- Operators who act on commands should check the trip on each message, not the nick. A message from
  a trusted nick with no trip or the wrong trip is just chat.
- The Muse web client doesn't send a password yet, so its messages have no trip.

## Pass handling

- `pass` lives in `config.json`, which is gitignored. Never commit it, paste it in chat, or put it in
  screenshots or issue text.
- Both bridges send it only inside the join frame and log that frame **without** the pass.
- If a pass leaks, pick a new one. The trip changes with it, so update every `publish_trips` and
  trusted-trip list that named the old trip.

## Publishing: why untagged means private

The status view (`tools/status.py` → `docs/status.json`) is public once committed, so it fails closed:

- A task is published only if it has a `repo` on the `publish_repos` allowlist. With no tag, it
  isn't published. Nobody has to remember to hide private work. Private repo work stays out by default.
- Every counted message must carry a trip in `publish_trips`. An empty list publishes nothing, and
  there's no nick-based bypass, so an impostor can't inject tasks or results.
- A result can't make a task public: it inherits the task's visibility.
- Even an empty status file reveals when the relay was online (`coverage`). Treat committing it as
  publishing, and get the repo owner's OK first.

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
