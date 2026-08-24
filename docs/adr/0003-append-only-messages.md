# ADR 0003 — Messages are append-only, and the database refuses otherwise

**Status**: accepted · 2026-08-14

## Context

"Delete a message" and "edit a message" are obvious features, and deleting a row is the obvious
implementation. That kind of rule holds for a year and then quietly stops holding.

## Decision

`Messages` rows are never deleted and their text is never edited. Hiding is a tombstone
(`DeletedAtUtc`); correction is a new turn. The rule is enforced in
`AirpDbContext.SaveChanges`, which throws on any pending message delete or text edit — the one
exception is `Purging`, set only by the purge command, whose entire purpose is erasure.

## Consequences

- A reroll keeps the rejected reply, hidden — "why did it say that" is almost always asked
  about a reply that was thrown away.
- The in-place edit mattered more than the delete: it loses what was said while looking
  innocent.
- A silent loss of history becomes a failing test on the day the mistake is written.
