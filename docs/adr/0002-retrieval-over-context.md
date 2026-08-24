# ADR 0002 — A retrieval layer instead of an infinite context window

**Status**: accepted · 2026-08-15

## Context

The services this replaces handle long stories by dropping old turns — the forgetting this
project exists to prevent. Models now offer million-token windows, which suggests the opposite
approach: send everything, always.

## Decision

> You do not need an infinite context window if you have a good retrieval layer.

The prompt is built against a deliberate budget (`Model:ContextBudget`, default 32,000 tokens,
far below the model's window). What no longer fits is compressed, extracted and embedded — not
dropped, and not kept whole forever.

## Consequences

- Every token is paid for on every turn, so a full window multiplies cost by ~30 for no gain;
  attention also thins over long contexts.
- The budget forces a recorded choice: the audit says exactly what each layer cost and what
  was left out (see ADR 0010).
- The memory subsystem (ADR 0006) exists because the budget does.
