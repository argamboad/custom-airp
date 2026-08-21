# Contributing

This started as one person's replacement for a $20/month subscription, and it is public
because the design might be useful to someone else. Contributions are welcome; so are bug
reports that say what you were doing when it happened.

## Getting it running

```bash
git clone https://github.com/argamboad/custom-airp
cd custom-airp
dotnet build
dotnet test
```

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Everything the application
stores lives under `%LOCALAPPDATA%\Airp` on Windows and `~/.local/share/Airp` elsewhere; set
`AIRP_HOME` to somewhere disposable and you can develop without going near your own history.

```bash
AIRP_HOME=/tmp/airp-dev dotnet run --project src/Airp.Terminal -- library --samples
```

From an IDE there are two launch profiles and **the sandbox is the first one**, so F5 runs
against `.airp-dev` beside the project rather than against your own conversations. The other is
named `Airp (real data)` in capitals, because the difference between them is invisible until it
is not: messages are append-only and a send is billed, so a debug session against the real
database is a permanent, paid write to a story someone is playing. A sandbox home has no key in
it and so cannot send at all until `airp secret set` puts one there.

Both profiles set `DOTNET_ENVIRONMENT=Development`, which is what makes
`src/Airp.Terminal/appsettings.Development.json` apply — that is where to put settings you only
want while debugging. Configuration is read in the ordinary order: `appsettings.json`, then
`appsettings.{Environment}.json`, then the user's own `airp.json`, then `AIRP_*` variables, then
the command line. Anything the user's file sets therefore wins over the development one, which
is why the sandbox — whose file starts empty — is where a development override actually takes
effect.

## What is most wanted

- **Another provider.** Only OpenRouter has ever been exercised. The request this client sends
  is plain OpenAI, so others ought to work already — if one does, say so, and if it does not,
  that is a bug worth a report. See *Which APIs it works with* in the README.
- **A secret store that is not DPAPI.** On Linux and macOS the key can only come from an
  environment variable, because the alternative was writing it to disk in the clear.
  Implementations of `ISecretStore` over libsecret or the macOS keychain would fix that.
- **Being wrong about the memory.** Compression, retrieval and fact extraction have hundreds
  of tests and very few lived turns. A long conversation that behaves oddly is more useful
  than a feature request.

## What this project will not do

**Nothing here touches JanitorAI.** No authenticating against it, no private endpoints, no
scraping, no browser automation — their terms prohibit bots, and the only interaction is
passive: Janitor calls a proxy you run, never the other way round. A pull request that crosses
that line will be declined however well it is written.

## House style

The code reads like prose and the comments explain *why*, not what. If a decision was measured
rather than guessed, the measurement belongs in the comment — several of the ones already there
exist because the obvious approach was tried first and cost something.

- **Warnings are errors.** `dotnet build` is the check.
- **Tests where there is real logic**, none for getters. View tests resolve keys through the
  real `KeyMap`: a hand-built keystroke once shipped a binding nobody could press.
- **Named arguments on calls with many parameters.** Three bugs in one day came from a
  parameter added in the middle of a positional list.
- **English throughout** — code, comments, documents, tests, commit messages.
- Commit subjects are a sentence about the behaviour, not a category prefix. `git log` will
  show you the shape.

## The invariants

These are load-bearing, and a change that breaks one needs to say why in the pull request:

1. **`Messages` is append-only.** The store refuses to persist a deleted or edited message.
   Deleting writes a tombstone. `airp purge` is the one exception and it lifts the guard by
   name rather than sneaking past it.
2. **The user's turn is stored before the model is called**, so a provider failure cannot eat
   what someone typed.
3. **The prompt's layer order is a contract**, from least to most volatile. Retrieval goes
   after the transcript, where it reads worse and costs nothing, because putting it in the
   middle invalidates the provider's prefix cache on every turn.
4. **Every billed call is audited and recorded.** A budget nobody checks against reality stops
   meaning anything, silently.
5. **The API key never passes through `IConfiguration`.** Variables ending in `_KEY` or
   `_TOKEN` are discarded so that no configuration dump can print one.

## Content

The library that ships is deliberately tame — the sample story is a lighthouse in 1963. What
anyone writes in their own `characters/` folder is their business and stays on their machine.
Please do not send character files as contributions.
