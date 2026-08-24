# ADR 0004 — Persist the user's turn first; idempotency anchored on the last reply

**Status**: accepted · 2026-08-14

## Context

A send can fail at the model after the user's words exist. Telling the reader it failed makes
them type it again — and then it is in the conversation, and on the bill, twice.

## Decision

The user's turn is written to the store *before* the model is called. A failed call surfaces
as `ReplyMissingException` carrying the stored turn and the hint "do not send it again". Each
send computes a `RequestHash` over `{conversationId | anchor | text | instruction}`, where the
anchor is the sequence of the **last live reply** — the state the message answers — and the
database enforces uniqueness per conversation.

## Consequences

- A retry after a model failure finds the unanswered row and asks again instead of storing the
  sentence twice; a retry after success returns the stored exchange without a second charge.
- Anchoring on the next free position instead would give the retry a different hash — the exact
  bug this design exists to prevent.
- The same words typed again after a reply has landed anchor differently, and are genuinely a
  new send.
