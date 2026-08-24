# ADR 0007 — Compression in batches, with a floor, a cap, and a credibility test

**Status**: accepted · 2026-08-19

## Context

Once a transcript sits at the budget ceiling, every send overflows by exactly one exchange.
Compressing only the overflow runs a paid call every turn on two messages — and produced
summaries *longer* than the turns they replaced (1.06x measured, against 37x for a real
62-message batch). The opposite failure was worse: a 99-message backlog handed to one call
against a fixed output ceiling came back as `##` — two characters, non-empty, stored, standing
in for the first hundred turns of a real story.

## Decision

- Batches of **at least 10** messages (`WorthACall`), **at most 40** (`AtMostPerSummary`),
  never touching the **6 most recent** (`AlwaysWhole`).
- A summary is refused unless it is long enough to be an account of what it replaces
  (`Credible`: at least source/60 tokens — working summaries measured 3–19x, the failures 84x
  and ~30,000x).
- A refused or unwritable summary sends the turns whole and **goes over budget** rather than
  dropping them. The summariser reserves room for the *resolved* character and persona through
  `ContextBuilder.Reserve`, counted exactly as the builder counts.
- Summarising runs cold (0.3) with a 1200-token ceiling (700 was measured too low — three of
  four real summaries ended mid-word). Fact extraction has its own task profile (0.2 / 4000):
  a reasoning model deliberates before writing JSON, and those tokens bill as output.

## Consequences

- Compression is not a per-turn toll: measured 2 compressions over 8 sends, because a summary
  frees far more room than it occupies. That is a test, not an anecdote.
- One guard that was tried and removed the same hour: refusing summaries cut off near the
  output ceiling. A fraction of the ceiling says nothing about whether an answer covers what
  it replaced, and against a host that always clips it means never compressing at all.
