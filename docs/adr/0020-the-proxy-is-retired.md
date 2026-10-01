# ADR 0020 — The proxy is retired

**Status**: accepted · 2026-10-01 · supersedes [ADR 0017](0017-proxy-writes-only-where-tagged.md)
and the proxy half of [ADR 0014](0014-proxy-is-passive.md)

## Context

`Airp.Proxy` was an OpenAI-compatible endpoint that let JanitorAI's interface, on a phone, play
a story kept here. It worked against real Janitor on 2026-09-29. Two days later:

- **The owner plays from the terminal over SSH and from the web pages on a phone**, and nothing
  else. The web pages do everything the proxy did for a phone, without a third party's
  interface between the reader and the story.
- **Janitor was never a good fit for the store.** Its Custom prompt belongs to the model
  settings rather than to a chat, so the tag naming a story applied to every chat at once, and a
  wrong selection would have written into the wrong story. Its reroll resent the last message,
  which the proxy stored as a second turn. Edits and deletions made there never reached the
  store.
- **The VM it ran on has 1 GB.** With the terminal (~270 MB) and the web pages (100–215 MB)
  running, the proxy's ~130 MB was what pushed the machine into swap: 80% of its time waiting on
  the disk, and a paste in the composer arriving one character at a time. Restarting the
  services cleared the swap; stopping the proxy is what keeps the room.

## Decision

The proxy is removed: its project, its tests, its token, its documentation and its service on
the VM. `FrontEndTurn` and `StoryReports` stay in Infrastructure, because the web pages and the
terminal read them.

**The hard limit in ADR 0014 stands unchanged:** no automated access to JanitorAI — no sign-in,
no private endpoints, no scraping, no browser automation. With the proxy gone there is no
interaction with Janitor at all.

## Consequences

- One less process, one less secret (`AIRP_PROXY_TOKEN`) and one less listening port.
- Playing from Janitor is no longer possible. Bringing it back means restoring the project from
  history, and ADR 0017's rule — write only where a `[[rp:<id>]]` tag says — would apply again.
- Stories played through the proxy are ordinary stories in the store; nothing about them changes.
