using Airp.Domain.Conversations;

namespace Airp.Infrastructure.Storage.Local;

/// <summary>A conversation owned by this machine rather than by a remote site.</summary>
public sealed class ConversationRecord
{
    /// <summary>Identifier, assigned here. Opaque to everything above the provider.</summary>
    public required string Id { get; set; }

    /// <summary>What to call it in the chat list.</summary>
    public required string Name { get; set; }

    /// <summary>Name of the character replying.</summary>
    public string? Speaker { get; set; }

    /// <summary>
    /// The character definition, sent as the leading system turn.
    /// </summary>
    /// <remarks>
    /// Stored on the conversation rather than as a message because it is not one: it is not
    /// something anybody said, it never scrolls past, and a transcript that showed it as a turn
    /// would be lying about the shape of the conversation. It also has to stay byte-stable
    /// across turns to survive the model's prefix cache.
    /// </remarks>
    public string? CharacterDefinition { get; set; }

    /// <summary>
    /// Name of a description in the character library, or null when this conversation holds
    /// its own text.
    /// </summary>
    /// <remarks>
    /// The name rather than a copy, for the same reason personas work that way: a character
    /// gets rewritten as they are played in, and the fixes should reach the conversations
    /// being played. A conversation that wants to stand apart from the library keeps its own
    /// <see cref="CharacterDefinition"/> instead, and that one wins.
    /// </remarks>
    public string? CharacterName { get; set; }

    /// <summary>
    /// Name of the persona this story is played as, or null to use the default.
    /// </summary>
    /// <remarks>
    /// The name rather than the text, deliberately. Editing a persona should reach every
    /// conversation played as them; a copy taken at creation would leave each one frozen at
    /// whatever the description said that day.
    /// </remarks>
    public string? PersonaName { get; set; }

    /// <summary>
    /// A description written for this story alone, overriding any named persona.
    /// </summary>
    /// <remarks>
    /// For the one-off that does not deserve a name. It wins over the named one because it
    /// cannot have been written for anything else.
    /// </remarks>
    public string? Persona { get; set; }

    /// <summary>Model identifier for this conversation, or null for the configured default.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// How forward the character is, on the terminal's five-step scale, or null when unset.
    /// </summary>
    /// <remarks>
    /// The three dials below came from ourdream's interface, but only the wire format was
    /// theirs — "how explicit", "how long", "how varied" are the questions any roleplay client
    /// asks. Kept because the terminal already has a screen for them, and because
    /// <c>ChatSettingScale</c> already carries a written description of every level, which is
    /// exactly what a system prompt needs.
    /// </remarks>
    public int? Lust { get; set; }

    /// <summary>How much the character writes per reply, or null when unset.</summary>
    public int? ResponseLength { get; set; }

    /// <summary>How varied the replies are, or null when unset.</summary>
    public int? Creativity { get; set; }

    /// <summary>
    /// Whether each character shows what they are not saying, after they say it.
    /// </summary>
    /// <remarks>
    /// Off by default and worth turning on. In a scene with a model, what a character withholds
    /// is the one thing there is no other way to reach — you cannot ask what they are really
    /// thinking without leaving the scene. It also gives the model somewhere to state a
    /// motive, and a character who says what they want out loud contradicts itself far less.
    /// </remarks>
    public bool InnerThoughts { get; set; }

    /// <summary>When the conversation was created, in UTC.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>
    /// When the conversation was hidden, or null while it is live.
    /// </summary>
    /// <remarks>
    /// A tombstone, not a delete. The terminal offers to delete a conversation and the reader
    /// expects it gone; the history it contains is still the thing this application exists to
    /// keep. Hiding satisfies both, and nothing above the provider can tell the difference.
    /// </remarks>
    public DateTimeOffset? DeletedAtUtc { get; set; }

    /// <summary>The messages, oldest first.</summary>
    public List<MessageRecord> Messages { get; set; } = [];
}

/// <summary>
/// A compressed account of turns too old to send whole.
/// </summary>
/// <remarks>
/// <para>
/// Derived data, and the only table here that is. Every summary can be written again from the
/// messages it covers, which is what makes deleting the whole table a recoverable act — and
/// what makes it safe to change how summarising works later without having lied about
/// anything in the meantime.
/// </para>
/// <para>
/// Each covers a fixed, closed range of sequences and is never rewritten. A single rolling
/// summary would have to be, and rewriting is the operation this store exists to refuse. When
/// the summaries themselves grow too numerous to send, the answer is a second layer that
/// summarises them — not an edit to these.
/// </para>
/// </remarks>
public sealed class SummaryRecord
{
    /// <summary>Identifier, assigned here.</summary>
    public required string Id { get; set; }

    /// <summary>Identifier of the conversation this belongs to.</summary>
    public required string ConversationId { get; set; }

    /// <summary>First message sequence covered, inclusive.</summary>
    public long FromSequence { get; set; }

    /// <summary>Last message sequence covered, inclusive.</summary>
    public long ToSequence { get; set; }

    /// <summary>The summary itself.</summary>
    public required string Text { get; set; }

    /// <summary>When it was written, in UTC.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>The model that wrote it.</summary>
    public string? Model { get; set; }

    /// <summary>How many turns it stands in for.</summary>
    public int MessageCount { get; set; }
}

/// <summary>
/// Something the conversation established, and the stretch over which it was true.
/// </summary>
/// <remarks>
/// <para>
/// Invariant 5, and the piece that separates this from a keyword lorebook. A summary says what
/// happened and retrieval says what was said; neither answers <em>what is true now</em>. Without
/// that distinction, a summary written three hundred turns ago saying she distrusts you keeps
/// telling the model exactly that, long after she stopped.
/// </para>
/// <para>
/// A fact stops being true by gaining a <see cref="ValidToSequence"/>, never by being deleted or
/// edited. That the character once distrusted you is itself part of the story, and a later scene
/// may turn on it. Only the currently-true set is sent to the model; the rest stays readable.
/// </para>
/// <para>
/// Derived, like summaries: everything here can be extracted again from the messages it came
/// from, so the table can be dropped without losing history.
/// </para>
/// </remarks>
public sealed class FactRecord
{
    /// <summary>Identifier, assigned here.</summary>
    public required string Id { get; set; }

    /// <summary>Identifier of the conversation this belongs to.</summary>
    public required string ConversationId { get; set; }

    /// <summary>
    /// Who or what the fact is about: a character, a place, or a relationship between two.
    /// </summary>
    /// <remarks>
    /// Kept as free text rather than a foreign key to a table of entities. The cast of a
    /// roleplay is discovered as it is played, and a schema that demanded an NPC be created
    /// before it could be mentioned would be a schema fighting the thing it models.
    /// </remarks>
    public required string Subject { get; set; }

    /// <summary>The fact, in one sentence.</summary>
    public required string Text { get; set; }

    /// <summary>Message sequence at which it became true.</summary>
    public long ValidFromSequence { get; set; }

    /// <summary>Message sequence at which it stopped being true, or null while it holds.</summary>
    public long? ValidToSequence { get; set; }

    /// <summary>The fact that replaced this one, when one did.</summary>
    public string? SupersededById { get; set; }

    /// <summary>When it was recorded, in UTC.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>The model that extracted it, or null when a person wrote it.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Whether the extractor is allowed to retire this fact.
    /// </summary>
    /// <remarks>
    /// Set on anything written by hand. A person who states a fact outright has said something
    /// the transcript may never mention, and letting a model decide it has stopped being true
    /// would make the one thing the reader can control the one thing they cannot rely on. They
    /// can still retire it themselves.
    /// </remarks>
    public bool Pinned { get; set; }
}

/// <summary>
/// A named meter the story keeps: affection, trust, suspicion, coin, whatever the story needs.
/// </summary>
/// <remarks>
/// <para>
/// The value lives here rather than only in the transcript, and that is the whole point. A card
/// that asks a model to render a meter has to show it the previous value so it can move it; when
/// that turn scrolls out of the window the model loses the thread and the number drifts. Stored,
/// it is injected every turn and survives compression.
/// </para>
/// <para>
/// Nothing here says what a meter means. The name and the note carry that, and both are written
/// by the reader — a story about a heist wants different meters than a story about a marriage,
/// and a schema that guessed which would be wrong for both.
/// </para>
/// </remarks>
public sealed class TrackerRecord
{
    /// <summary>Identifier, assigned here.</summary>
    public required string Id { get; set; }

    /// <summary>Identifier of the conversation this belongs to.</summary>
    public required string ConversationId { get; set; }

    /// <summary>
    /// What the meter is called, shown to the model and to the reader.
    /// </summary>
    /// <remarks>
    /// Free text, and often names two parties: <c>AFFECTION — Elena</c>, or
    /// <c>TRUST — Elena and Ferrin</c>. It is the label the model renders, so it reads
    /// the way the reader wants it to read.
    /// </remarks>
    public required string Name { get; set; }

    /// <summary>Where the meter currently stands.</summary>
    public double Value { get; set; }

    /// <summary>The top of the scale.</summary>
    public double Max { get; set; } = 100;

    /// <summary>How much it moved on the last turn.</summary>
    public double Delta { get; set; }

    /// <summary>Why it moved, in the model's own few words.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// What the meter measures and what moves it, in one sentence.
    /// </summary>
    /// <remarks>
    /// The difference between a meter that holds and one that wanders. A name alone leaves the
    /// model inferring from the word: <c>ADMIRATION</c> could plausibly rise on competence, on
    /// courage or on kindness, and left to guess it will pick whichever fits the scene in front
    /// of it — a different one each turn. Naming what moves it settles that once.
    /// </remarks>
    public string? Means { get; set; }

    /// <summary>
    /// What points on the scale mean, so the number is read the same way twice.
    /// </summary>
    /// <remarks>
    /// Without anchors a value is a number with no units. Is 60 out of 100 warm regard or
    /// devotion? Two or three labelled points fix it — and they matter more than the scale's
    /// size, because what the reader sees is the label the model chose to write beside it.
    /// </remarks>
    public string? Anchors { get; set; }

    /// <summary>
    /// A rule the model is told to apply to this meter, or null for none.
    /// </summary>
    /// <remarks>
    /// Where a meter stops being decoration. "Cannot rise while TRUST is below 40" turns a
    /// number into a mechanic the story has to respect.
    /// </remarks>
    public string? Rule { get; set; }

    /// <summary>Message sequence at which it last changed.</summary>
    public long UpdatedAtSequence { get; set; }

    /// <summary>When it was created, in UTC.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>One turn of a locally owned conversation.</summary>
/// <remarks>
/// Append-only. Nothing in this application updates the text of a row that exists or removes
/// one; <see cref="DeletedAtUtc"/> is how a message stops being shown. <c>AirpDbContext</c>
/// refuses to save a delete rather than trusting that rule to be remembered.
/// </remarks>
public sealed class MessageRecord
{
    /// <summary>Identifier, assigned here.</summary>
    public required string Id { get; set; }

    /// <summary>Identifier of the conversation this belongs to.</summary>
    public required string ConversationId { get; set; }

    /// <summary>
    /// Position within the conversation, monotonic and never reused.
    /// </summary>
    /// <remarks>
    /// The ordering key, in preference to the timestamp. Two turns of one exchange can land in
    /// the same clock tick, and an import carries whatever timestamps the export had — neither
    /// gives a total order, and a transcript needs one.
    /// </remarks>
    public long Sequence { get; set; }

    /// <summary>Who wrote it.</summary>
    public ChatRole Role { get; set; }

    /// <summary>The message body, with <c>\n</c> line endings.</summary>
    public required string Text { get; set; }

    /// <summary>When it was sent, in UTC.</summary>
    public DateTimeOffset SentAtUtc { get; set; }

    /// <summary>When the message was hidden, or null while it is shown.</summary>
    public DateTimeOffset? DeletedAtUtc { get; set; }

    /// <summary>
    /// Hash of the request that produced this turn, for callers that must not double-send.
    /// </summary>
    /// <remarks>
    /// Unique per conversation where present. A retry after a failure that actually succeeded
    /// then collides instead of writing the message twice, which is the failure worth
    /// designing against: the model was paid for either way, but the transcript only has to be
    /// wrong once to stay wrong.
    /// </remarks>
    public string? RequestHash { get; set; }

    /// <summary>The model that wrote an assistant turn, as the API reported it.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// The upstream host that served the turn, when the API named one.
    /// </summary>
    /// <remarks>
    /// The same model name is served by many hosts at different prices, with different caching,
    /// and with different willingness to write a given scene. Recorded per turn because that is
    /// the only moment the answer exists: a router picks per request, and nothing afterwards can
    /// reconstruct which one wrote a reply that came out badly.
    /// </remarks>
    public string? Provider { get; set; }

    /// <summary>Tokens of input that produced this turn, when known.</summary>
    public int? PromptTokens { get; set; }

    /// <summary>Tokens generated for this turn, when known.</summary>
    public int? CompletionTokens { get; set; }

    /// <summary>What the context builder predicted the prompt would cost.</summary>
    /// <remarks>
    /// Kept beside the count the API actually reported, so the estimate can be checked against
    /// reality instead of trusted. A budget built on a number nobody ever verifies is a budget
    /// that quietly stops meaning anything.
    /// </remarks>
    public int? EstimatedPromptTokens { get; set; }

    /// <summary>
    /// What went into the prompt that produced this turn, layer by layer.
    /// </summary>
    /// <remarks>
    /// Invariant 4. Once retrieval exists, "why did it say that" is answerable only if the
    /// exact context is recorded at the moment it is assembled — it cannot be reconstructed
    /// afterwards, because the retrieval that produced it depended on state that has moved on.
    /// </remarks>
    public string? ContextAudit { get; set; }

    /// <summary>
    /// The turn's embedding, for retrieval, or null while it has not been embedded.
    /// </summary>
    /// <remarks>
    /// Only turns that have aged out of the prompt are embedded. Recent ones are sent whole
    /// anyway, so a vector for them would cost a call to retrieve what is already there.
    /// Stored as a BLOB of little-endian floats: a few thousand of them scanned in memory is
    /// microseconds, and an index would be machinery guarding nothing.
    /// </remarks>
    public byte[]? Embedding { get; set; }

    /// <summary>The conversation this belongs to.</summary>
    public ConversationRecord? Conversation { get; set; }

    /// <summary>Projects onto the domain model the terminal reads.</summary>
    /// <returns>The message as the rest of the application sees it.</returns>
    public ChatMessage ToDomain() => new()
    {
        Id = Id,
        ConversationId = ConversationId,
        Role = Role,
        Text = Text,
        SentAtUtc = SentAtUtc,
    };
}
