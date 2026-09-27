# Critique note template

Copy this into `knowledge/<id>.md` when you file a real critique. Change `id` to a new slug, match the file name to that id, and replace every specimen sentence with the outline you actually read. This file is the shape. It is not a note in the hive mind until it lives under `knowledge/` and `chief-knowledge check` accepts it.

The specimen below is a grade-4 mathematics illustration so the sections are filled. It is not a review of a submitted outline. Do not file this specimen as if a teacher had sent it.

Front matter must match `knowledge/README.md`: `chief-knowledge check` rejects unknown keys, a summary over 240 characters, an author other than `chief`, `fuse`, or `alex`, and a source that is not `alex`, `room`, or an `https://` URL. `visibility` must be `public`. There is no private visibility in this repo.

```markdown
---
id: lesson-outline-critique-equivalent-fractions
title: "Critique: equivalent fractions on a number line"
summary: "Revise. The exit check does not match the number-line objective, and timing, access, and prerequisites are still open."
tags: [teaching, lesson-outline, critique]
source: room
authors: [chief]
created: 2026-09-27
links: [lesson-outline-critique-shape, classroom-kit-priority]
visibility: public
---

## Outline

- Title: Equivalent fractions on a number line
- Subject and level: Grade 4 mathematics
- Duration: 40 minutes
- Setting: in-person whole class
- Asked by: a trusted trip (record the trip, never the password; the nick is not identity)
- Not included: student names, grades, disability details, contact information, the channel name, and the outline text where it repeats any of those

## Verdict

**revise** — six gates are partial or missing. The outline is still a lesson a teacher can repair.

## Gates

- **Objectives:** met — students will place two equivalent fractions on a 0-to-1 number line and explain why the points match.
- **Audience:** met — grade 4, whole class, in person.
- **Prerequisites:** partial — the outline assumes students can name numerator and denominator, and the warm-up never checks that.
- **Assessment alignment:** missing — the exit ticket asks for a written definition of "equivalent," which does not show placement on a number line.
- **Learning sequence:** met — notice two fractions on a line, teacher model, guided practice on one pair, then independent practice on two pairs.
- **Timing:** partial — 40 minutes is stated, segments are 10 + 15 + 15, and there is no cut line if guided practice runs long.
- **Materials:** met — one paper number line per student, teacher-made, plus a document camera the outline says is already in the room.
- **Accessibility:** partial — a learner who cannot use the document camera still has the paper line, and an oral explanation can replace the written one, but a learner who cannot see the line has no way to place the points.
- **Differentiation:** partial — a worked example supports learners who are not ready, and there is no extension for learners who already place the pairs correctly.
- **Checks for understanding:** met — after guided practice each student holds up the line, and the teacher looks for the two points coinciding.
- **Closure:** partial — students complete the exit ticket, which measures the wrong skill, and the outline does not say what the next period uses.
- **Risks and assumptions:** met — assumes yesterday's unit-fraction lesson happened; if the opening item shows a student cannot place a unit fraction, the outline says to stop after one modeled pair and skip independent practice. No student work is posted publicly.

## Critique

Repair these before teaching. Do not treat this as a replacement outline.

1. Change the exit ticket so the student places one new equivalent pair on a number line and says why the points match. That is the objective.
2. Add a one-minute warm-up that asks students to label a numerator and a denominator, taken from guided practice so the period still adds to 40. If many cannot, stop after one modeled pair and skip independent practice.
3. Name the cut line: if guided practice passes 15 minutes, drop the second independent pair, not the aligned exit check.
4. Add one extension pair with denominators that are not multiples of the first, still on the same 0-to-1 line.
5. For a learner who cannot see the paper line, say how they still place the points (a tactile line, or the teacher plots positions the student describes).
6. Name what the next period uses from that check, so the close is not only the ticket.

## Left open

- Whether this course requires a named standard. The outline does not say. Objectives are still met without one.
- The warm-up result, which only tomorrow's class can show.

## Board

Appended a new task. Did not rewrite earlier lines. Did not change task 2 (the product card stays `claimed` until the room appends a later `done` line).

- Task id: 6
- Title: Revise outline: equivalent fractions on a number line
- Owner: chief
- State: open
```

## Rules that keep the note public

- One outline, one note, one id. A revision of the same outline is a new note that links the earlier critique. Do not `supersedes` the earlier note unless it was wrong.
- `summary` is one line, at most 240 characters, and states the verdict.
- `tags` include `teaching`, `lesson-outline`, and `critique`. Add a subject tag only when it is a lowercase hyphenated word (`mathematics` is fine; a student's name is not a tag).
- `source` is `room` when the outline arrived in the room, `alex` when Alex handed it to you directly, or the `https://` URL of a pull request. Never a channel name.
- `authors` lists who wrote the critique (`chief`, `fuse`, or both). Alex is an author only when Alex wrote the note.
- `links` includes `lesson-outline-critique-shape` and `classroom-kit-priority`.
- The body uses the six headings in this specimen, in this order: Outline, Verdict, Gates, Critique, Left open, Board.
- The Gates section has all twelve lines, in the checklist order, none omitted.
- Quote the outline only in short phrases you need for a mark. Do not paste the whole outline.
- The Board section names the task id and state you appended, or says the board was not updated and why. It does not name the channel. The only board path allowed in a note is `boards/<sha256(trimmed channel)>.jsonl`.

Write it with the tool, from the repo root. The body file is the six headings only. The flags write the front matter. Do not put a second `---` block in the body file. Change the id, title, and summary for the outline you actually read; the values below only match this specimen.

```bash
dotnet run --project src/Chief.Knowledge -- add \
  --title "Critique: equivalent fractions on a number line" \
  --summary "Revise. The exit check does not match the number-line objective, and timing, access, and prerequisites are still open." \
  --tag teaching --tag lesson-outline --tag critique \
  --source room \
  --author chief \
  --link lesson-outline-critique-shape --link classroom-kit-priority \
  --id lesson-outline-critique-equivalent-fractions \
  --body-file /path/to/body.md
```

Then run `dotnet run --project src/Chief.Knowledge -- check`. Exit 0 is required before you commit the note. Run `rebuild` before you trust `search`. A hand-written file is fine when it uses the same front matter as `knowledge/README.md`.
