# ADR 0016 — Dials are data: a configurable pack instead of a hardcoded trio

**Status**: accepted · 2026-08-24

## Context

The application ships three dials (Lust, ResponseLength, Creativity) and one toggle
(InnerThoughts), each a column on the conversation row and a case in the settings view.
`Airp:Scales` already proved the wording wants to be configurable; the set of dials wants the
same. Meanwhile the single Lust axis bundles three separable things — receptiveness, pacing,
and explicitness of prose — and boundary statements ("no limits") that belong to a dedicated
control.

## Decision

Dials become entries in a pack file, `dials.json`, beside `airp.json` in the data directory.
The application ships a default pack; the file overrides it.

**The taxonomy.** Every dial declares:

- `kind` — `scale` (exactly five levels, read by index; fewer ignores the dial whole),
  `toggle` (on-text or nothing), `choice` (named options), `list` (items through a template),
  `text` (one free value through a template).
- `lever` — `prompt` (the chosen text is injected into the **directives layer**, framed by
  the application), `sampler` (the chosen value becomes the API parameter named in `maps`;
  nothing is injected), or `both`.
- `enabled` — whether the TUI offers it (default `true`). **Disabled is pinned, not off**:
  the dial's `default` still applies on every prompt; it is merely hidden and unadjustable.
  A stored per-conversation value survives disablement and resurfaces on re-enable.
- `default` — always present. `null` is a legal value meaning "inject nothing / the model's
  own behaviour", which is what preserves today's semantics for the shipped dials. Scale
  defaults are level indexes (0–4): indexes survive relabeling.
- `title`, `help`, and for free-text kinds `accepts`/`examples` — documentation as data, not
  comments, because the configuration writer regenerates comments and because the settings
  screen renders these fields directly.

**The rules the application enforces**, all inherited from earlier decisions:

- Prompt-lever text is injected **framed** — a bare directive gets echoed back as the
  character's turn (the lesson `RegenerateDirective` and `AskDirective` already encode).
- Sampler-lever labels are screen-only; the model never sees a number, because a number the
  model can see is a number it performs (the tracker reservation, ADR 0015).
- Screen text is wire text: the level description the reader picks is the sentence sent.
- All dial text lives in the directives layer — cache-stable until a dial moves (ADR 0005).
- Five levels or nothing, matching the existing `Airp:Scales` contract.

**The shipped pack** carries the four existing controls with today's behaviour, plus:
pacing, initiative, consequence, prose-balance, register, npc-liveliness, agency-guard
(shipped disabled **and unset** — the persona layer already carries the standard rule, and
two statements of one rule pull against each other), veils, pov, ending, language, and
anti-loop (`frequency_penalty`). Lust is narrowed to heat alone; pacing and veils take back
the jobs it was smuggling.

## Consequences

- An untouched conversation injects exactly what it injects today: every added dial defaults
  to null/off.
- The settings view stops being three hardcoded cases and renders whatever the pack declares.
- Per-conversation dial values generalise from three columns to a keyed store (see the
  migration notes in the implementing PR).
- A user who wants a dial gone entirely deletes it from the pack; disabling it instead keeps
  it applying silently — the difference is deliberate and documented in the file header.
- Every prompt-lever level is a permanent token tax on conversations that set it; the pack's
  wording stays terse, and the budget arithmetic (ADR 0007) is unaffected because directives
  are counted like any other fixed layer.
