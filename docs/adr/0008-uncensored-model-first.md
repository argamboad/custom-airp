# ADR 0008 — Model selection: the most unfiltered model available, prose second, cost third

**Status**: accepted · 2026-08-16

## Context

This is an NSFW roleplay client. A censored model does not merely degrade it — it breaks the
Lust dial, and it makes the **summariser** refuse, and a summariser that refuses is a
character that forgets. The background model reads the same transcript the reply model writes.

## Decision

Selection criterion #1 is uncensored. Prose quality second. Cost a distant third: a real
95-message session costs ~$0.12, so at this volume cost is noise. Default is DeepSeek V4 Flash
(over half of OpenRouter's roleplay traffic across DeepSeek variants; 1M context; $0.14/M in).

## Consequences

- `gpt-oss-120b` is among the most censored there is — the worst choice for this use.
- The same model arrives differently filtered from different OpenRouter backends: on a
  refusal, check the audit's *served by* before blaming the model.
- Any configured `BackgroundModel` must be as permissive as the reply model.
