# ADR 0008 — Model selection: a model that will not refuse the story, prose second, cost third

**Status**: accepted · 2026-08-16

## Context

The content here is user-authored roleplay fiction, and mature themes are ordinary in that
genre. A model that refuses such content does not merely degrade the client — it breaks it
structurally: the Lust dial's upper range does nothing, and the **summariser** reads the same
transcript the reply model writes, so a refusal there means a stretch of story leaves the
prompt with nothing written down about it. A summariser that refuses is a character that
forgets — the exact failure this project exists to prevent.

## Decision

Selection criterion #1 is that the model must be willing to both write and read whatever the
story contains (the community's term is *uncensored*). Prose quality second. Cost a distant
third: a real 95-message session costs ~$0.12, so at this volume cost is noise. Default is
DeepSeek V4 Flash (over half of OpenRouter's roleplay traffic across DeepSeek variants; 1M
context; $0.14/M in).

## Consequences

- Heavily filtered models are disqualified regardless of quality — `gpt-oss-120b` is among
  the most restrictive measured, and the worst fit for this use.
- The same model can arrive differently filtered from different OpenRouter backends: on a
  refusal, check the audit's *served by* before blaming the model.
- Any configured `BackgroundModel` must meet the same bar as the reply model, because it
  reads what the reply model wrote.
