# ADR 0017 — The proxy writes only where a tag says

**Status**: superseded by [ADR 0020](0020-the-proxy-is-retired.md) · accepted 2026-09-29 · narrowed the resolver in [ADR 0014](0014-proxy-is-passive.md)

## Context

`SessionResolver` mapped an anonymous request to a conversation by three strategies in trust
order: an explicit `[[rp:<id>]]` tag, a character name that exactly one stored conversation
had, and an opening turn that matched exactly one. Every strategy refused when more than one
conversation fitted, which read as "never guesses".

Reading it again before pointing a real front end at a real store showed that it did guess, just
not ambiguously. "Exactly one stored story has Blake" is not "this chat is that story": the front
end has chats the store knows nothing about. A test chat there that mentioned a name, or opened
the same way, would have written a turn into a real story — permanently (ADR 0003) and billed.

It also read every conversation's messages on every request, to compare openings.

## Decision

The tag is the only way in. A request without `[[rp:<id>]]` writes nothing and is refused with
a message saying how to add one; a tag that names no stored conversation is refused and says
so. Nothing is inferred from names or openings.

## Consequences

- Every front-end chat that should reach the store carries the tag in its custom prompt — one
  step of setup per story. `airp audit <chat>` prints the id.
- A chat without one is safe to have: nothing it sends is written anywhere.
- A request lists the conversations to check the id against, and no longer reads their messages.
