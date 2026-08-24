# ADR 0001 — The history lives on the owner's machine

**Status**: accepted · 2026-08-14

## Context

This project replaces two hosted services: one cost $20/month, the other is free but forgets.
Both keep the conversation history on their servers; neither leaves a copy the owner controls,
and a story played for months can be lost to a pricing change, a policy change, or a deleted
account.

## Decision

Conversations are stored locally — SQLite in the platform's application-data directory
(`%LOCALAPPDATA%\Airp` on Windows, `~/.local/share/Airp` on Linux, `~/Library/Application
Support/Airp` on macOS; `AIRP_HOME` overrides) — and the model is a service we call, not a
place the story lives. The only remote dependency is a chat-completions
API, selectable by configuration.

## Consequences

- The database holds the entire history in the clear, and it is NSFW: it must never be exposed
  on a direct port, and backups are the owner's responsibility.
- Everything downstream (append-only storage, derived memory, the spend ledger) is possible
  *because* the store is ours.
- A privacy-maximal variant exists at no design cost: point the client at a local Ollama
  (measured 4.58 tok/s on this hardware — a privacy option, not a cost one).
