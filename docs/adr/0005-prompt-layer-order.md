# ADR 0005 — Prompt layers ordered by volatility: the prefix-cache contract

**Status**: accepted · 2026-08-15

## Context

Providers cache the unchanged head of a prompt. Retrieved memories change every turn; the
character definition never changes. Where each layer sits decides how much of a 60k-token
prompt is re-billed and re-processed each turn.

## Decision

The order is a contract, not a preference: `character · persona · directives · world ·
summaries · history · memories · trackers · instruction` — least to most volatile. Retrieval
goes *after* the transcript, not in the middle where it reads more naturally. When the budget
binds, the transcript gives, oldest first.

## Consequences

- Measured both ways: seconds vs. minutes per turn on a local model; $0.0028/M vs. $0.14/M on
  a caching API. Individual live calls have reached 100% of the prompt served from cache,
  which is the proof the prefix is stable.
- Anything that changes with a toggle (inner thoughts) belongs with the dials, in the cacheable
  half; anything per-turn (trackers) belongs at the tail.
- Whether the serving host caches at all is a lottery (61% vs. 0% same day, same conversation)
  — which is why provider pinning exists (ADR 0009).
