# ADR 0009 — OpenRouter first; cost read from the response; hosts pinned by request field

**Status**: accepted · 2026-08-16, extended 2026-08-20

## Context

Choosing a model is an experiment that should be a configuration change. Computing cost
locally from a price list drifts from the invoice: prices change, hosts differ, cache
discounts apply per call. And a router fans one model across hosts that differ in price,
caching, and willingness to answer at all.

## Decision

- One key, one OpenAI-compatible endpoint (OpenRouter) first; going direct is worth it only
  after a model is chosen. Embeddings are separable (`EmbeddingBaseUrl`/`EmbeddingApiKeyName`)
  because DeepSeek's own API exposes no `/embeddings` — without the split, going direct takes
  retrieval away silently.
- **Never compute cost from a price list.** `usage.cost` and
  `prompt_tokens_details.cached_tokens` arrive inline on every response; the generation id is
  stored for reconciling. `Cost` is nullable throughout — unreported and zero are different
  facts.
- Host routing is a request field, not a prompt change: `IgnoreProviders` / `PreferProviders`
  sent as OpenRouter's `provider` object, omitted entirely when unset (it is the one
  non-OpenAI part of the request).
- A 200 with no message content is an error that says why: `finish_reason` separates a filter
  from a ceiling from a host with nothing to say, and a reasoning field with null content is a
  third thing again.

## Consequences

- The backend lottery is what costs money: measured per-host cache rates of 61% / 47% / 0% on
  the same conversation. Pinning hosts is the largest saving available.
- Slugs are lower-case and wrong ones are dropped silently; the audit's *served by* is the
  only confirmation a change took. `airp cost --providers` is the decision input — its
  out/call column is how a token-soup host is spotted (128 tokens/call vs. 575–791).
