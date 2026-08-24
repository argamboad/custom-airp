# One send, line by line

The hot path — what actually executes between pressing Enter in the composer and the reply
appearing — with file and line references into the real code. Line numbers are as of the
commit that introduced this document; the shape moves slowly, the lines may drift.

The same walkthrough as a picture: [FLOWS.md §1](FLOWS.md). The classes:
[ARCHITECTURE.md](ARCHITECTURE.md).

---

## 0. From the key to the provider

| Step | Where |
|---|---|
| The process started as `airp` with no verb → `run` → the TUI | `Program.Main`, [Program.cs:54](../src/Airp.Terminal/Program.cs) |
| The shell's loop reads the key, paste-aware | `Shell.LoopAsync`, [Shell.cs:192](../src/Airp.Terminal/Ui/Shell.cs) |
| `KeyMap` resolves it under the composer's `KeyContext` | `Shell.DispatchAsync`, [Shell.cs:235](../src/Airp.Terminal/Ui/Shell.cs) |
| `ConversationView` returns `ViewAction.Run("Sending…", work)` — the shell spins while the work runs | `Shell.RunWithSpinnerAsync`, [Shell.cs:393](../src/Airp.Terminal/Ui/Shell.cs) |
| The view's work calls the service; the service is a thin pass-through to the provider interface | `ConversationService.SendAsync`, [ConversationService.cs:39](../src/Airp.Application/Services/ConversationService.cs) |

Everything from here on is `LocalConversationProvider` —
[LocalConversationProvider.cs](../src/Airp.Infrastructure/Providers/LocalConversationProvider.cs).

---

## 1. Idempotency, then persistence — `SendAsync` (line 221)

```text
SendAsync(conversationId, text, instruction, progress, ct)
├─ 237  OpenAsync                 lazy migration behind a semaphore, first call only (line 94)
├─ 238  RequireAsync              the conversation row, or ContractException
├─ 245  anchor                    max Sequence of LIVE ASSISTANT rows — the state this
│                                 message answers, NOT the next free slot
├─ 255  Hash(...)                 SHA256 over {conversationId | anchor | text ␟ instruction},
│                                 first 32 hex chars; the instruction is part of
│                                 the request's identity, joined by U+001F so prose cannot
│                                 fake the separator
├─ 257  lookup by RequestHash
│   ├─ found + answered   →      return the stored exchange; no second charge
│   ├─ found + unanswered →      the previous attempt died at the model; reuse the row
│   └─ not found          → 302  INSERT the user's turn NOW — invariant 2: persist
│                                 before calling the model
└─ 306  ReplyAsync(store, conversation, pending: sent, instruction, ...)
```

Why the anchor matters: anchored on the next free position, a retry after a failure would hash
differently and store the line twice. Anchored on the last reply, the retry collides with the
unique `(ConversationId, RequestHash)` index instead —
[AirpDbContext.cs:75](../src/Airp.Infrastructure/Storage/Local/AirpDbContext.cs) — and the
database enforces what a check-then-insert could race past.

---

## 2. Building the prompt — `ComposeAsync` (line 811)

Shared verbatim by a send, a carry-on, a regenerate, an aside, and every pass of a rebuild —
an aside that differed by one layer would miss the prefix cache the next real turn is about to
hit.

```text
ComposeAsync(store, conversation, instruction, ct)
├─ 829  visible history            live rows, ordered by Sequence
├─ 843  character  = TextLibrary.ResolveAsync(own text → file by name → default)
├─ 850  persona    = same rule, with Airp:DefaultPersona as the default   (TextLibrary.cs:206)
├─ 858  known      = FactExtractor.LiveAsync    facts with ValidToSequence == null
├─ 861  meters     = trackers by name
├─ 871  pack       = IDialService.PackAsync     dials.json, or the embedded default
├─ 873  dialValues = the conversation's stored choices (DialValues rows)
├─ 875  directives = DialEngine.Directives(pack, values)   every prompt-lever dial
│                    in force, rendered in the screen's own words (DialEngine.cs:54)
├─ 876  sampler    = DialEngine.Sampler(pack, values)      temperature / ceiling /
│                    frequency penalty, from the sampler-lever dials (DialEngine.cs:124)
├─ 887  prepared   = ConversationSummariser.PrepareAsync(...)   ← §3, may compress
├─ 905  live       = FactExtractor.LiveAsync AGAIN — extraction may have just run,
│                    and a fact established in the compressed stretch must reach
│                    THIS turn's prompt, not the next one's
├─ 911  budget     = prepared.CompressionFailed ? int.MaxValue : ContextBudget
│                    — going over budget costs cents; dropping turns costs the story
├─ 918  memories   = RecallAsync(...)                            ← §4
└─ 924  LocalPrompt.Build(named arguments, every one)            ← §5
```

The resolution at 843/850 is the line the 202-message failure taught: the summariser must see
the **resolved** layers, because `conversation.CharacterDefinition` is empty in every
conversation the application creates — the conversation stores a name, the text lives in a
file.

---

## 3. Compression — `ConversationSummariser.PrepareAsync` ([ConversationSummariser.cs:99](../src/Airp.Infrastructure/Providers/ConversationSummariser.cs))

```text
PrepareAsync(store, conversation, history, settings, character, persona, directives, world, trackers, ct)
├─ 111  existing summaries → covered watermark (last ToSequence)
├─ 124  reserved  = ContextBuilder.Reserve(character, PersonaLayer(persona),
│                   directives, world, joined summaries, trackers)
│                   + Retrieval(history, settings)     mean turn × RecallCount (line 323)
│                   + settings.MaxTokens + 200
│                   — counted EXACTLY as the builder counts, per-message overhead included
├─ 135  allowance = ContextBudget − reserved
├─ 142  walk back from the newest uncovered turn until the allowance is spent;
│       everything older is what would be dropped
├─ 155  toCompress = Worthwhile(uncovered, overflowing)          (line 268)
│         floor WorthACall = 10      (line 227 — 2-message stretches compress at 1.06×)
│         cap   AtMostPerSummary = 40 (line 239 — 99 messages once came back as "##")
│         guard AlwaysWhole = 6      (line 248 — never the exchange being played)
├─ 159  WriteAsync                                               (line 336)
│   ├─ 343  Transcript.Render        reader named by persona, never "User"
│   ├─ 345  ModelRouter.For(Summary)  T=0.3, ceiling 1200
│   ├─ 349  Background.CompleteAsync  one retry, retryable failures only
│   ├─ 363  Spend row FIRST — billed whatever is decided next
│   ├─ 365  empty reply → null
│   ├─ 392  produced < Credible(source/60, min 20) → refused, null   (line 301)
│   └─ 404  SummaryRecord [FromSequence .. ToSequence]
│   (null at any point → log, return CompressionFailed = true: send whole, go over)
├─ 164  save summary, then the SAME stretch to the extractor:
│       FactExtractor.UpdateAsync                                (FactExtractor.cs:115)
│   ├─ 127  live facts rendered as "id-prefix | subject | text"
│   ├─ 135  ModelRouter.For(Facts)   T=0.2, ceiling 4000 — a reasoning model
│   │       deliberates before writing JSON, and those tokens bill as output
│   ├─ 140  Background.CompleteAsync, Spend row at 153
│   ├─ 181  Parse: JSON dug out of prose or a code fence
│   └─ 206  ApplyAsync: INSERT new facts (ValidFrom = stretch start);
│           retire by id prefix — never a pinned fact (line 258)
└─ 209  return (summaries, recent tail after the watermark, CompressionFailed: false)
```

Measured, not hoped: honest reservation did **not** make compression a per-turn toll — 2
compressions over 8 sends on the real story, because a summary frees far more room than it
occupies.

---

## 4. Retrieval — `RecallAsync` (provider line 943) → `MemoryRetriever`

```text
RecallAsync(store, conversation, prepared, ct)        (line 956)
├─ no embedding client, or no recent turns → []
├─ compressedUpTo = prepared.Recent[0].Sequence − 1;  ≤0 → []
├─ query = the newest USER turn among the recent ones; blank → []
├─ MemoryRetriever.BackfillAsync                     (MemoryRetriever.cs:49)
│         aged-out, live, un-embedded turns, ≤128 per pass → EmbedAsync → BLOBs
└─ MemoryRetriever.RecallAsync                       (MemoryRetriever.cs:105)
          embed the query · cosine vs. every candidate
          keep ≥ RecallThreshold (0.35) · top RecallCount (4)
          re-sort into transcript order — a scene out of sequence invites reordering
          → ["Earlier in this conversation:", "[seq] Name: text", ...]
```

Every failure path returns `[]` with a warning logged. Retrieval improves a prompt; it never
blocks a reply.

---

## 5. Assembly — `LocalPrompt.Build` → `ContextBuilder.Build` ([ContextBuilder.cs:168](../src/Airp.Application/Context/ContextBuilder.cs))

```text
ContextBuilder.Build(character, persona, directives, world, summaries,
                     history, memories, trackers, instruction, budget)
├─ 185  Fixed(name, text)      one system ModelMessage per non-blank layer,
│                              costed by TokenEstimator.ForMessage
├─ 198  character · persona (framed by PersonaLayer, line 134: "The user is
│       playing the following person… never write their words or actions")
│       · directives · world · summaries (joined) · memories (joined) · trackers
├─ 212  remaining = budget − fixed layers   ← the transcript gets what is left
├─ 217  fill NEWEST-FIRST until remaining is spent, then reverse —
│       the turns nearest the reply are the ones that survive;
│       everything older is counted in Dropped
├─ 237  instruction goes LAST, and its role flips: after an assistant turn it is
│       sent as a USER turn — several hosts answer a prompt ending
│       assistant+system with 200 and no content at all
└─ 259  BuiltContext { Messages, Sections, Budget }
        .Describe() → "character 4103 · history 28791 (24 dropped) · total …/32000"
        — the exact string stored as the audit
```

Send order = layer order: `character, persona, directives, world, summaries, history,
memories, trackers, instruction`. Least → most volatile; the prefix cache contract lives here.

---

## 6. The call and the write-back — `ReplyAsync` (line 695)

```text
ReplyAsync(store, conversation, pending, instruction, progress, ct)
├─ 711  composed = ComposeAsync(...)                     §2–§5
├─ 723  choice = ModelRouter.For(Reply, settings,
│                composed.Sampler — the dial engine's temperature (0.6..1.4),
│                ceiling (200..2600) and frequency penalty, each falling back
│                to the configured default when its dial is unset)
├─ 731  _model.CompleteAsync(messages, conversation.Model ?? choice.Model, ...)
│         OpenRouterClient (OpenRouterClient.cs:56):
│         · payload is plain OpenAI; "provider" routing object only when
│           Prefer/IgnoreProviders are set (line 178), omitted otherwise
│         · 200 with no content THROWS, naming finish_reason, host, and
│           whether only reasoning came back (line 131)
│         · reads model, provider, usage.cost, cached_tokens, generation id
│           straight off the response — cost is never computed from a price list
├─ 741  on failure: ReplyMissingException carrying the pending turn —
│       "the message was kept"; nothing is rolled back
├─ 753  MessageRecord: reply text, Sequence = NextSequenceAsync (counts hidden
│       rows too — a freed number would collide with the unique index),
│       Model, Provider, PromptTokens, CompletionTokens,
│       EstimatedPromptTokens, ContextAudit = context.Describe()
├─ 771  Trackers.Absorb: parse "[NAME] bar v/max | Δ d | note" lines out of the
│       reply, clamp to [0, Max], write value/delta/note back to the store —
│       the round trip that lets a meter survive compression
├─ 776  Spend row via Ledger.Row — written whatever becomes of the reply;
│       a reroll a second later hides the message and leaves this row
└─ 779  SaveChangesAsync — through GuardAppendOnly,
        which would throw on any message delete or text edit
```

The reply returns up the same stack: provider → service → view, which merges it into the
transcript and redraws.

---

## What runs when it goes differently

| Variation | Divergence point |
|---|---|
| Carry on (no user turn) | `ContinueAsync` (line 373): no pending row, a framed "carry the scene forward" instruction — then `ReplyAsync` as above |
| Regenerate | `RegenerateAsync` (line 319): tombstone the newest reply **before** the call, restore it on failure — then `ReplyAsync` with `RegenerateDirective` |
| Aside (`/ask`) | `AskAsync` (line 459): `ComposeAsync` with `AskDirective`, `ModelTask.Aside`; answer goes to `Asides` + a spend row — **never** to `Messages` |
| Rebuild | `RebuildMemoryAsync` (line 1094): delete derived memory (pinned facts kept), then loop `ComposeAsync` until a pass writes no summary |
| From the proxy | identical from `SendAsync` down; only the entry differs ([FLOWS.md §7](FLOWS.md)) |
