# ADR 0014 — The proxy is passive, and no automated access to JanitorAI exists

**Status**: accepted · 2026-08-18 · **hard limit, not negotiable**

## Context

Playing from Janitor's UI against this store requires an OpenAI-compatible endpoint Janitor
can call. Janitor's Terms of Use prohibit bots and scripts.

## Decision

The only interaction is **passive**: Janitor calls us, because the user configured a Proxy
URL. No authenticating against janitorai.com, no private endpoints, no scraping, no browser
automation — and no looking for a way around it. The proxy takes only the newest user turn
from the request, discards the front end's truncated history, and rebuilds the prompt from
our store through the same compose path the terminal uses.

## Consequences

- `SessionResolver` must map an anonymous request to a conversation — by explicit `[[rp:id]]`
  tag, unique speaker, or opening prefix — and **refuses rather than guesses**, because a turn
  written into the wrong conversation is permanent (ADR 0003).
- `stream: true` is honoured by chunking the finished reply as SSE; true streaming would
  change the storage contract (a partially-arrived reply is not a turn) and waits on that
  decision.
- If a task appears to require crossing this line, it stops there.
