# Architecture Decision Records

Why the code is the way it is — one decision per record, with what was measured to make it.
The diagrams and walkthroughs live in [ARCHITECTURE.md](../ARCHITECTURE.md),
[FLOWS.md](../FLOWS.md) and [CALLSTACK.md](../CALLSTACK.md).

| # | Decision |
|---|---|
| [0001](0001-local-first.md) | The history lives on the owner's machine |
| [0002](0002-retrieval-over-context.md) | A retrieval layer instead of an infinite context window |
| [0003](0003-append-only-messages.md) | Messages are append-only, enforced by the database context |
| [0004](0004-persist-first-idempotency.md) | Persist the user's turn first; idempotency anchored on the last reply |
| [0005](0005-prompt-layer-order.md) | Prompt layers ordered by volatility — the prefix-cache contract |
| [0006](0006-memory-three-mechanisms.md) | The memory is three mechanisms; derived state is rebuildable |
| [0007](0007-compression-batching.md) | Compression in batches, with a floor, a cap and a credibility test |
| [0008](0008-uncensored-model-first.md) | Model selection: a model that will not refuse the story, prose second, cost third |
| [0009](0009-openrouter-first-cost-from-response.md) | OpenRouter first; cost read from the response; hosts pinned |
| [0010](0010-spend-ledger-and-audit.md) | Auditing is mandatory; Spend is a ledger, not a summary |
| [0011](0011-asides-are-not-turns.md) | A question asked out of character is not a turn |
| [0012](0012-library-as-files.md) | The library is text files, referenced by name, one resolution rule |
| [0013](0013-secrets-never-in-configuration.md) | Secrets never pass through IConfiguration |
| [0014](0014-proxy-is-passive.md) | The proxy is passive; no automated access to JanitorAI |
| [0015](0015-dials.md) | Three dials, each wired to the strongest lever available |
| [0016](0016-dials-are-data.md) | Dials are data: a configurable pack instead of a hardcoded trio |

New records take the next number. A superseded decision keeps its file and gains a
**Status**: superseded by NNNN line — the history of being wrong is part of the record.
