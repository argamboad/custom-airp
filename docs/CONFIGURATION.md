# Configuration

Every setting, its default, and where it is read. Source of truth:
[AirpOptions.cs](../src/Airp.Application/Options/AirpOptions.cs) and
[ModelOptions.cs](../src/Airp.Application/Options/ModelOptions.cs).

## How the layers stack

Later wins. `Host.CreateApplicationBuilder` adds the first two from the binary's directory;
`Program.BuildHost` adds the rest ([Program.cs:148](../src/Airp.Terminal/Program.cs)):

1. `appsettings.json`
2. `appsettings.{Environment}.json` (applies when `DOTNET_ENVIRONMENT` is set — the launch
   profiles set `Development`)
3. **`airp.json`** in the data directory — the user's own file, reload-on-change
4. `AIRP_*` environment variables, re-rooted under the `Airp` section
   (`AIRP_Theme` → `Airp:Theme`, `AIRP_Model__Name` → `Airp:Model:Name`). Variables ending in
   `_KEY` or `_TOKEN` are **discarded** — secrets do not pass through configuration
5. Command line: `--provider`, `--theme`, `--transcript-width`, `--keyboard`, `--refresh`

`airp.json` management: `EnsureExistsAsync` writes defaults once and never looks inside again;
`airp config --rewrite` is the only thing that reads an existing file, and it is **additive** —
set keys keep their values, missing keys arrive with defaults, and the `// one of: …`
annotations above `theme` and `keyboard` are regenerated from `Enum.GetNames`. The reader
tolerates comments and trailing commas; hand-written comments elsewhere are lost on the next
save. Paths resolve against the data directory (`%LOCALAPPDATA%\Airp`, `AIRP_HOME` overrides),
except `ExportDirectory`, made absolute against the app root at options-build time.

## `Airp:` — the application

| Key | Default | What it does |
|---|---|---|
| `Theme` | `Dark` | `Dark` · `Light` · `HighContrast` · `Monochrome` |
| `Keyboard` | `Standard` | `Standard` · `Vim` (a shortcut layer — hjkl, G, n/N, u — not a modal editor) |
| `TranscriptWidthPercent` | `60` | centred reading column, clamped 30–100; 100 = full window. `--transcript-width` sets it, `airp model` prints it |
| `AutoRefreshSeconds` | `60` | background store re-read; ≤0 disables |
| `MouseSupport` | `false` | click + scroll wheel |
| `ExportDirectory` | `./exports` | made absolute at startup so exports never land where the shell was standing |
| `DefaultPersona` | — | **file name** in `personas/`, no extension. Descriptions never live in configuration — a name defined in both places once silently preferred the wrong one |
| `DatabaseFile` | `./airp.db` | relative to the data directory |
| `RestoreSession` | `true` | reopen the last chat and view |
| `Provider` | `local` | the seam. The only registered value; anything else fails at startup by name |
| `Scales` | empty | your own wording for the dials, keyed `Lust` / `ResponseLength` / `Creativity`; a replacement must supply exactly five levels or it is ignored |
| `MessageCharacterLimit` | `0` (none) | composer refuses to send past it |
| `InstructionCharacterLimit` | `0` (none) | same, for regenerate instructions |

## `Airp:Model:` — the model

| Key | Default | What it does |
|---|---|---|
| `BaseUrl` | `https://openrouter.ai/api/v1` | any OpenAI-compatible endpoint |
| `Name` | `deepseek/deepseek-v4-flash` | selection criterion #1 is **uncensored**; prose second, cost a distant third |
| `ApiKeyName` | `OPENROUTER_API_KEY` | the **name** of a secret in `ISecretStore` (`airp secret set`), never the key |
| `BackgroundModel` | null → `Name` | summariser + extractor. Must be as permissive as the reply model — a summariser that refuses is a character that forgets |
| `EmbeddingModel` | `openai/text-embedding-3-small` | 1536 dims, ~$0.02/M |
| `EmbeddingBaseUrl` | null → `BaseUrl` | what makes going direct-to-DeepSeek survivable: DeepSeek has no `/embeddings`, and without the split retrieval dies silently |
| `EmbeddingApiKeyName` | null → `ApiKeyName` | second endpoint usually means second account |
| `RecallCount` | `4` (0–20) | retrieved turns per prompt; small on purpose |
| `RecallThreshold` | `0.35` | cosine floor below which a recalled turn is noise |
| `Temperature` | `1.0` | fallback when the Creativity dial is unset (dial: 0.6–1.4; summaries pinned at 0.3, facts 0.2 regardless) |
| `MaxTokens` | `1024` | reply ceiling fallback (dial: 200–2600) |
| `ContextBudget` | `32000` | prompt ceiling, far under the model's window on purpose — attention thins, and every token is paid on every turn |
| `TimeoutSeconds` | `180` | per call |
| `IgnoreProviders` | `[]` | host **slugs** never to route to (`deepinfra`, not `DeepInfra`). A wrong slug is dropped silently — verify with the audit's *served by* |
| `PreferProviders` | `[]` | hosts to try first; the largest saving available, since whether a host caches the prefix decides most of a 60k-token turn's cost |
| `AllowProviderFallbacks` | null | `false` turns the preference into a restriction |

`airp cost --providers` is where routing decisions come from — its `out/call` column is how a
host answering token soup is spotted (128 tokens/call against 575–791 elsewhere).

## Secrets — never in configuration

| Name | Used by |
|---|---|
| `OPENROUTER_API_KEY` (or whatever `ApiKeyName` says) | model + embeddings calls |
| `AIRP_PROXY_TOKEN` | the proxy's bearer; it refuses to start without one. A different string from the model key on purpose — this one gets typed into a third party's settings |

Stored DPAPI-protected under `AppPaths.Root/secrets` via `airp secret set`. Never paste a key
into chat or on a command line.

## Launch profiles

Two, and the sandbox is first: `Airp (sandbox)` sets `AIRP_HOME=.airp-dev` — a home with no
key, so F5 cannot spend money until a key is put there. `Airp (real data)` uses the real home,
where the user's `airp.json` still wins over `appsettings.Development.json`. Both set
`DOTNET_ENVIRONMENT=Development`.
