# ADR 0018 — The web pages trust the tailnet's word for who is asking

**Status**: accepted · 2026-09-30

## Context

The stories as web pages, for a phone, need to let exactly one person in. The database behind
them holds every story in the clear, and it is NSFW. The obvious answers each cost something:
a password is one more secret to store, type on a phone and leak; a token in the URL ends up in
the browser's history; sessions and cookies are a login system to build and get right.

The pages are only ever reached through `tailscale serve`, which already knows who is asking —
the tailnet authenticated the device — and passes the account along in `Tailscale-User-Login`.

## Decision

No password. One configured login (`Airp:Web:Login`) is let in, compared against that header;
every other request is answered "Not available." and nothing else.

A header is only evidence if nothing else can send it, so the process **refuses to start**
unless it listens on loopback alone, where only `tailscale serve` on the same machine can
reach it — and refuses to start without a login to allow. Both exit with 78 and say why.

## Consequences

- Nothing to type, store or rotate. Access follows the tailnet: remove a device from it and
  the pages are gone for that device.
- Anyone who can run a process on the machine can reach the loopback port and forge the header.
  That is accepted: such a process can already read the database file directly.
- It holds only for `tailscale serve`. Funnel or any public tunnel is outside what this was
  decided for: never expose the port.
- Every response is `no-store`, `no-referrer`, `noindex`, with a CSP allowing only the page's own
  origin, and the tab title is "Stories" on every page — the same privacy the terminal keeps by
  never setting its window title.
