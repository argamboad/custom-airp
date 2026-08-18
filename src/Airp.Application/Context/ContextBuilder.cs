using Airp.Application.Abstractions;

namespace Airp.Application.Context;

/// <summary>
/// One layer of the prompt, kept separate so the audit can say what each one cost.
/// </summary>
/// <param name="Name">What the layer is, for the audit.</param>
/// <param name="Messages">The turns it contributed, in order.</param>
/// <param name="EstimatedTokens">What they were estimated to cost.</param>
/// <param name="Dropped">Turns the budget left out of this layer.</param>
public readonly record struct ContextSection(
    string Name,
    IReadOnlyList<ModelMessage> Messages,
    int EstimatedTokens,
    int Dropped);

/// <summary>A prompt, with the accounting that produced it.</summary>
public sealed record BuiltContext
{
    /// <summary>The turns to send, in order.</summary>
    public required IReadOnlyList<ModelMessage> Messages { get; init; }

    /// <summary>What each layer contributed.</summary>
    public required IReadOnlyList<ContextSection> Sections { get; init; }

    /// <summary>The budget this was built against.</summary>
    public int Budget { get; init; }

    /// <summary>What the whole prompt was estimated to cost.</summary>
    public int EstimatedTokens => Sections.Sum(static s => s.EstimatedTokens);

    /// <summary>Turns left out across every layer.</summary>
    public int Dropped => Sections.Sum(static s => s.Dropped);

    /// <summary>A one-line accounting, for the audit and for the reader.</summary>
    /// <returns>Something like <c>character 42 · history 8100 (12 dropped) · total 8142/12000</c>.</returns>
    public string Describe()
    {
        var parts = Sections
            .Where(static s => s.Messages.Count > 0 || s.Dropped > 0)
            .Select(static s => s.Dropped > 0
                ? $"{s.Name} {s.EstimatedTokens} ({s.Dropped} dropped)"
                : $"{s.Name} {s.EstimatedTokens}");

        return $"{string.Join(" · ", parts)} · total {EstimatedTokens}/{Budget}";
    }
}

/// <summary>
/// Assembles the prompt from its layers, within a token budget.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The order of the layers is the contract, not a preference.</strong> They run from
/// least to most volatile, and a provider's prefix cache holds everything up to the first
/// thing that changed since the last turn. Retrieved memories change every turn by
/// definition, so they go last — after the transcript, where they read less naturally and
/// cost nothing. Putting them in the middle, which is the instinctive place, would invalidate
/// the cache on every single turn.
/// </para>
/// <para>
/// Measured both ways before this was written: with a local model the difference is seconds
/// against minutes per turn, and with a caching API it is $0.0028 against $0.14 per million
/// input tokens.
/// </para>
/// <para>
/// When the budget binds, the transcript is what gives — oldest first. The character
/// definition, the dials and the retrieved memories are all either small or chosen for this
/// turn; dropping any of them to keep an old message would be trading the reason for the reply
/// against one more line of it.
/// </para>
/// </remarks>
public static class ContextBuilder
{
    /// <summary>Names of the layers, in the order they are sent.</summary>
    public static class Layer
    {
        /// <summary>The character definition. Never changes.</summary>
        public const string Character = "character";

        /// <summary>Who the reader is playing. Changes about as often as they do.</summary>
        public const string Persona = "persona";

        /// <summary>The conversation's dials. Changes when the reader moves one.</summary>
        public const string Directives = "directives";

        /// <summary>
        /// What the story has established and not yet undone.
        /// </summary>
        /// <remarks>
        /// Early, because it changes only when something in the story changes — which is
        /// occasionally, not every turn. Putting it after the transcript would push it into the
        /// volatile end of the prompt for no reason.
        /// </remarks>
        public const string WorldState = "world";

        /// <summary>Summaries of turns too old to send whole.</summary>
        public const string Summaries = "summaries";

        /// <summary>The transcript. Append-only, so every earlier turn is unchanged.</summary>
        public const string History = "history";

        /// <summary>Memories retrieved for this turn. Volatile, and therefore last.</summary>
        public const string Memories = "memories";

        /// <summary>
        /// The story's meters, with their current values.
        /// </summary>
        /// <remarks>
        /// Late, with the volatile layers, because a meter moves on almost every turn. Putting
        /// it up with the world state — where it reads like it belongs — would invalidate the
        /// cached prefix every time a number changed by one.
        /// </remarks>
        public const string Trackers = "trackers";

        /// <summary>A one-off directive for this call, such as a regenerate reason.</summary>
        public const string Instruction = "instruction";
    }

    /// <summary>Builds a prompt.</summary>
    /// <param name="characterDefinition">The character, or null.</param>
    /// <param name="directives">The conversation's dials rendered as text, or null.</param>
    /// <param name="worldState">What the story has established and not yet undone, or null.</param>
    /// <param name="summaries">Summaries of older turns, oldest first.</param>
    /// <param name="history">The transcript, oldest first.</param>
    /// <param name="memories">Memories retrieved for this turn.</param>
    /// <param name="trackers">The story's meters and their current values, or null.</param>
    /// <param name="instruction">A one-off directive for this call, or null.</param>
    /// <param name="budget">The token ceiling for the whole prompt.</param>
    /// <returns>The prompt and its accounting.</returns>
    public static BuiltContext Build(
        string? characterDefinition,
        string? persona,
        string? directives,
        string? worldState,
        IReadOnlyList<string>? summaries,
        IReadOnlyList<ModelMessage> history,
        IReadOnlyList<string>? memories,
        string? trackers,
        string? instruction,
        int budget)
    {
        ArgumentNullException.ThrowIfNull(history);

        var sections = new List<ContextSection>();
        var fixedCost = 0;

        ContextSection Fixed(string name, string? text, ModelRole role = ModelRole.System)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new ContextSection(name, [], 0, 0);
            }

            var message = new ModelMessage(role, text);
            var cost = TokenEstimator.ForMessage(message);
            fixedCost += cost;
            return new ContextSection(name, [message], cost, 0);
        }

        var character = Fixed(Layer.Character, characterDefinition);

        // Framed rather than sent raw. A persona is usually written in the third person —
        // "Allan is a Scottish philosopher who…" — which on its own reads as one more person in
        // the scene, and a model that mistakes it for one will introduce them and wait. The
        // frame says whose description this is, and states the rule where it is most likely to
        // be broken.
        var you = Fixed(
            Layer.Persona,
            string.IsNullOrWhiteSpace(persona)
                ? null
                : "The user is playing the following person. Speak to them as this person, and "
                  + "never write their words or actions for them.\n\n" + persona);
        var dials = Fixed(Layer.Directives, directives);
        var world = Fixed(Layer.WorldState, worldState);
        var summary = Fixed(
            Layer.Summaries,
            summaries is { Count: > 0 } ? string.Join("\n\n", summaries) : null);
        var recalled = Fixed(
            Layer.Memories,
            memories is { Count: > 0 } ? string.Join("\n", memories) : null);
        var meters = Fixed(Layer.Trackers, trackers);

        // Whatever is left after the layers that are not negotiable belongs to the transcript.
        var remaining = Math.Max(0, budget - fixedCost);
        var kept = new List<ModelMessage>();
        var spent = 0;

        // Newest first while filling, so the turns nearest the reply are the ones that survive.
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var cost = TokenEstimator.ForMessage(history[i]);

            if (spent + cost > remaining)
            {
                break;
            }

            kept.Add(history[i]);
            spent += cost;
        }

        kept.Reverse();

        // Carrying on adds no turn of the reader's own, so the prompt would end on the
        // character's last reply with only a system note after it — and several providers
        // answer that shape with 200 and no content at all, which reaches the reader as "the
        // model did not answer". A directive that follows a reply is the reader asking for
        // more, so it goes in as their turn and the prompt ends where every backend expects.
        var directive = Fixed(
            Layer.Instruction,
            instruction,
            kept.Count > 0 && kept[^1].Role == ModelRole.Assistant ? ModelRole.User : ModelRole.System);

        sections.Add(character);
        sections.Add(you);
        sections.Add(dials);
        sections.Add(world);
        sections.Add(summary);
        sections.Add(new ContextSection(Layer.History, kept, spent, history.Count - kept.Count));
        sections.Add(recalled);
        sections.Add(meters);
        sections.Add(directive);

        var messages = new List<ModelMessage>();

        foreach (var section in sections)
        {
            messages.AddRange(section.Messages);
        }

        return new BuiltContext
        {
            Messages = messages,
            Sections = sections,
            Budget = budget,
        };
    }
}
