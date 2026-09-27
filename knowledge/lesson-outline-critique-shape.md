---
id: lesson-outline-critique-shape
title: Lesson outline critiques use this note shape
summary: A critique of a teacher outline is a public knowledge note with a verdict, twelve SME-gate lines, and the board task id. The specimen is not a submitted outline.
tags: [teaching, lesson-outline, critique]
source: room
authors: [chief]
created: 2026-09-27
links: [classroom-kit-priority, room-boards]
visibility: public
flagged: important
---

Alex authorized the Lesson outline coach card. A critique is one note under `knowledge/`. The checklist is `docs/lesson-outline-coach/sme-gate-checklist.md`. The copy-ready specimen is `docs/lesson-outline-coach/critique-note.md`. The playbook is `agents/lesson-outline-coach.md`.

`chief-knowledge check` accepts the note only when the front matter matches `knowledge/README.md`. Required fields are `id`, `title`, `summary` (one line, at most 240 characters), `tags`, `source` (`alex`, `room`, or an https URL), `authors` (`chief`, `fuse`, `alex`), `created` (`YYYY-MM-DD`), and `visibility: public`. Link this note and `classroom-kit-priority`. Use tags `teaching`, `lesson-outline`, and `critique`.

The body has these headings, in order:

1. **Outline** — title, subject and level, duration, setting, and the trusted trip that asked. No student names, grades, disability details, contact information, or channel name.
2. **Verdict** — `ready`, `revise`, or `blocked`, then one sentence.
3. **Gates** — all twelve checklist lines, in checklist order: Objectives, Audience, Prerequisites, Assessment alignment, Learning sequence, Timing, Materials, Accessibility, Differentiation, Checks for understanding, Closure, Risks and assumptions. Each line is `met`, `partial`, or `missing`, plus the evidence in the outline.
4. **Critique** — what to change, in teacher language. This is not a replacement outline.
5. **Left open** — assumptions only the teacher or the next class can settle.
6. **Board** — the task id, title, owner, and state appended for this outline, or why the board was not updated. Do not name the channel. A board path in this folder must be `boards/<sha256(trimmed channel)>.jsonl`.

`ready` means every gate is met. Any partial or missing gate is `revise`. `blocked` means there is no outline to file: a topic only, a request to write the lesson from scratch, or text that is not safe to publish. In that last case, do not write the note.

The filled grade-4 number-line writeup in `docs/lesson-outline-coach/critique-note.md` shows the sections. It is an illustration of the shape, not a critique anyone submitted, and it is not itself a file under `knowledge/`.
