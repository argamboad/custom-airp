# ADR 0011 — A question asked out of character is not a turn

**Status**: accepted · 2026-08-18

## Context

Typing `(OOC: how far is the lighthouse?)` into the composer stores it forever: retrieval
embeds it, the summariser compresses it as something that happened, the extractor pulls facts
from it — and append-only makes all of that permanent.

## Decision

`/ask` builds the *byte-identical* prompt the next turn would send, up to the instruction, and
stores the answer only in `Asides` — a table no prompt ever reads. It still writes a spend row
and an audit, because a billed call that left no trace would make the audit stop adding up.
The ask directive orders the model to answer as author, briefly, **only from what it was
given** — an invented detail here is stored nowhere and becomes something the reader believes
and the story then contradicts.

## Consequences

- On a caching provider the question is nearly free, and grounded in exactly what the
  character can currently see.
- `F` in the ask pane promotes an answer to a pinned fact — the deliberate path from aside to
  canon, instead of the accidental one.
