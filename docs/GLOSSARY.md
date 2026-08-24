# Glossary

The project's own vocabulary, in one place. Terms link to the document that treats them fully.

| Term | Meaning |
|---|---|
| **aside** | A question asked about the story out of character (`/ask`). Answered from the exact prompt the next turn would send, stored only in the `Asides` table, which no prompt ever reads. Not a turn. [DATA.md](DATA.md) |
| **anchor** | The sequence of the last live reply — the state a sent message answers. What `RequestHash` is computed over, so a retry collides instead of duplicating. [CALLSTACK.md §1](CALLSTACK.md) |
| **audit** | The per-layer breakdown (`character 4103 · history 28791 (24 dropped) · …`) stored on every reply at assembly time, plus estimated-vs-reported tokens. `airp audit` shows it; *served by* is its most-consulted column. |
| **branch** | A named copy of a conversation up to a chosen turn (`B`), carrying transcript, memory and dials; leaving behind `Spend`, `RequestHash` and tombstoned replies. [FLOWS.md §6](FLOWS.md) |
| **budget** | `Model:ContextBudget`, the prompt's token ceiling — a target for cost and attention, far under the model's window. When compression fails, deliberately exceeded rather than letting turns drop. |
| **card / character** | A text file on the `characters/` shelf defining a character or world. Sent whole as the first prompt layer, never summarised, never dropped — its size is the one permanent decision in the prompt. |
| **compression** | Summarising the stretch of transcript that no longer fits: batches of 10–40, never the 6 newest turns. The moment turns stop being seen in their own words — so facts are extracted from the same stretch in the same breath. [FLOWS.md §2](FLOWS.md) |
| **Credible** | The summariser's refusal line: a summary shorter than one-sixtieth of its source is an answer that stopped, not an account. Refused summaries send the turns whole and go over budget. |
| **dial pack** | The set of controls in force: `dials.json` beside `airp.json`, or the embedded default when no file exists. The file replaces the default whole. Each dial declares a kind (scale/toggle/choice/list/text), a lever (prompt or sampler), whether it is enabled, and a default. Disabled means *pinned to the default and hidden*, not off. [ADR 0016](adr/0016-dials-are-data.md) |
| **dials** | Whatever the pack declares — shipped: the original Creativity (→ temperature 0.6–1.4), ResponseLength (→ ceiling 200–2600, and into the prompt), Lust and inner thoughts, plus pacing, initiative, consequence, prose balance, register, NPC liveliness, user-agency, veils, POV, endings, reply language and anti-loop (→ frequency penalty). The reader's wording and the model's are the same text; per-conversation choices live in `DialValues`. |
| **derived** | Data reproducible from `Messages`: summaries, facts, embeddings. Dropping those tables loses nothing; `airp rebuild` proves it. The ledger and pinned facts are the named exceptions. |
| **fact** | One sentence that is true *now*, with `ValidFrom`/`ValidTo` sequences. Retired by gaining an end, never by deletion. The answer to the question summaries and retrieval don't answer. |
| **inner thoughts** | Optional per-conversation directive: one line per character of what they did not say. Never for the user; omitted if it only repeats the dialogue. |
| **instruction (layer)** | The per-call directive at the very end of the prompt — a regenerate note, `/do`, the ask frame. Every instruction text must say what it is and what the reply must be, or a bare note gets echoed back as the character's turn. |
| **layer order** | `character · persona · directives · world · summaries · history · memories · trackers · instruction` — least to most volatile. A contract, not a preference: it is what the provider's prefix cache keys on. [ARCHITECTURE.md](ARCHITECTURE.md) |
| **ledger** | The `Spend` table: one row per billed call, written before the output is judged, kept whatever became of it. The one non-derived table. [DATA.md](DATA.md) invariant 7 |
| **library** | Four shelves of text files: `characters/`, `personas/`, `snippets/`, `openings/`. Conversations store names, not copies. An opening's filename matching a character's name *is* the association. |
| **memories (layer)** | Retrieval's contribution: compressed turns whose embeddings score ≥ threshold against the newest user turn, re-sorted into transcript order. Changes every turn, hence last-but-two in the prompt. |
| **opening** | A character's first message, on its own shelf, pre-filled into a new chat by filename match. In the store it is simply message #1, role Assistant. |
| **persona** | Who the reader is in the scene — a file on the `personas/` shelf, framed in the prompt ("the user is playing the following person…"), and the name background readers label the reader's turns with. |
| **pinned** | A fact written by hand (`airp fact add`, or `F` on an ask answer). The extractor cannot retire it; the reader can. The exit that stops a bad extraction reinforcing itself. |
| **prefix cache** | The provider-side cache of the unchanged head of the prompt. The reason the layer order exists, and the reason the backend lottery matters: hosts that cache served 47–61%, hosts that don't, 0%. |
| **purge** | The one true erasure: rows of already-hidden conversations removed under the `Purging` flag, ledger kept, `VACUUM` after. Everything else called "delete" is a tombstone. |
| **reroll / regenerate** | Ask the newest reply to be written again. The old reply is tombstoned before the call (and restored if the call fails); its cost stays in the ledger and is counted as discarded at report time. |
| **RequestHash** | SHA-256 over `{conversationId | anchor | text ␟ instruction}`, unique per conversation. The database-enforced answer to "did this send already happen". |
| **resolution rule** | For characters and personas alike: *the conversation's own text → file by name → default file*. No fourth branch, deliberately. |
| **retrieval** | Embedding aged-out turns and bringing back the few that bear on what was just said. Only compressed turns are candidates; failure degrades to nothing, never blocks a reply. |
| **sequence** | The monotonic per-conversation ordering key, never reused (hidden rows keep theirs). What summaries, facts and branches all anchor to — not timestamps, which SQLite can't order anyway. |
| **shelf** | One of the library's four folders. |
| **snippet** | Authored prose the composer expands via `:name` + Tab, sharing the emoji-shortcode rail. |
| **summary** | A model-written account of a closed range `[From..To]` of turns, immutable once stored. When summaries themselves outgrow the budget, the answer is a second layer over them — never an edit. |
| **tombstone** | `DeletedAtUtc` on a message or conversation: hidden from every prompt and view, kept for the audit. What the terminal calls deletion. |
| **tracker / meter** | A named per-conversation value with `Means`/`Anchors`/`Rule`. Injected every turn, the model renders it moved, and `Absorb` reads it back — the round trip that survives compression. On record: a meter the model can see is a meter the model writes towards. |
| **world (layer)** | The live facts rendered as "What is true in this story right now", grouped by subject. Early in the prompt because it changes only when the story does. |
