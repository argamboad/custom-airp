# ADR 0006 — The memory is three mechanisms, and all derived state is rebuildable

**Status**: accepted · 2026-08-15

## Context

One mechanism cannot answer the three questions a long story asks: what happened, what was
said, and what is true now. A summary loses wording; retrieval has no notion of "no longer
true"; a fact list does not tell the story.

## Decision

Three mechanisms, each firing on its own and only when needed:

| | Question | How |
|---|---|---|
| Summaries | what happened? | the model compresses the stretch that no longer fits |
| Retrieval | what was said? | embeddings + cosine over already-compressed turns |
| Facts | what is true now? | extracted from the same stretch, with `ValidFrom`/`ValidTo` |

Summaries and facts are produced at the same moment — when turns stop being seen in their own
words, the last chance to read them cheaply. All three are **derived**: `airp rebuild` deletes
and reproduces them through the ordinary compose path. Facts written by hand are pinned — the
extractor cannot retire them — because they derive from nothing and are the reader's one
override against a bad extraction reinforcing itself.

## Consequences

- A conversation that fits in the budget never spends an extra call.
- Retrieval only embeds compressed turns; recent ones are sent whole anyway.
- Background failures degrade to a log line, retried once, and never block a reply — but a
  failed extraction is silent, which is why the retry exists (a real story lost its first
  62 messages' extraction to a single empty response).
