# ADR 0010 — Auditing is mandatory, and Spend is a ledger, not a summary

**Status**: accepted · 2026-08-15 (audit), 2026-08-19 (ledger)

## Context

A budget nobody checks against reality stops meaning anything, silently. And two of the four
kinds of billed call — compression and extraction — fire without the reader asking, invisible
for as long as spending was inferred from message rows.

## Decision

Every reply stores its per-layer breakdown, estimated tokens, and the provider-reported
counts beside them (`airp audit`). Every billed call writes exactly one `Spend` row — written
*before* the output is judged, kept whatever became of it, including replies rerolled a second
later. Whether a reply was discarded is read from its tombstone at report time, never stored.
`Spend` is the one table that is **not** derived: what a router actually charged exists
nowhere else once the response is gone.

## Consequences

- Purging a conversation counts its ledger and keeps it; there is deliberately no FK from
  `Spend` to `Messages`.
- `Cost` is `decimal`, converted once at the ledger boundary — hundreds of ~$0.0028 rows must
  not sum to `0.0006000000000000001`.
- Embedding calls are not in the ledger; the report says so rather than implying completeness.
