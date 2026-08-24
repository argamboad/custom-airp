# ADR 0015 — Three dials, each wired to the strongest lever available

**Status**: accepted · 2026-08-15

## Context

The dials came from the replaced service's interface, but "how forward / how long / how
varied" are questions any roleplay client answers. Each can reach the model as sampler
settings, as prompt text, or both — and the weakest wiring is asking a model in prose to "be
more varied".

## Decision

| Dial | Where it goes |
|---|---|
| Creativity | the sampler's **temperature**, 0.6–1.4 — never the prompt |
| ResponseLength | the **token ceiling**, 200–2600, *and* the prompt |
| Lust | the **prompt**, in the scale's own wording |

The text the reader sees on screen and the text the model receives are the same
(`Airp:Scales` replaces both together). When summarising, temperature is pinned at 0.3
regardless of Creativity: a creative summariser invents details the character then believes
forever.

## Consequences

- Null means "never set" and is distinct from the middle level; a partial update carries only
  what changed.
- Inner thoughts is a toggle beside the dials, in the cacheable half of the prompt; its
  directive never writes for the user and omits a line that only repeats the dialogue.
- On record: a tracker the model can see is a meter the model writes towards — the reservation
  is documented with the feature, not hidden.
