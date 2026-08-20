using Airp.Application.Options;

namespace Airp.Application.Context;

/// <summary>What a model is being asked to do.</summary>
public enum ModelTask
{
    /// <summary>Write the next turn of the conversation. The reader waits on this.</summary>
    Reply = 0,

    /// <summary>Compress turns that no longer fit. Nobody reads the output directly.</summary>
    Summary,

    /// <summary>Answer a question about the story, out of character. Read once, stored nowhere.</summary>
    Aside,
}

/// <summary>How a task is dispatched: which model, and how it should sample.</summary>
/// <param name="Model">Model identifier.</param>
/// <param name="Temperature">Sampling temperature.</param>
/// <param name="MaxTokens">Ceiling on the generated output.</param>
public readonly record struct ModelChoice(string Model, double Temperature, int MaxTokens);

/// <summary>
/// Decides which model answers which kind of request.
/// </summary>
/// <remarks>
/// <para>
/// The two tasks want opposite things. A reply is read by a person and wants warmth and
/// variation, so it runs hot and long. A summary is read only by the next prompt and wants to
/// be accurate and dull, so it runs cold and short — a summariser that embellishes is
/// inventing history that every later turn will then treat as fact.
/// </para>
/// <para>
/// Both default to the same model, because at this volume the saving from a cheaper one is
/// fractions of a cent and the requirement is awkward: whatever summarises adult roleplay has
/// to be willing to read it. Splitting them is a configuration change for when a specific
/// model turns out to be better or faster at the background work, not a default guess.
/// </para>
/// </remarks>
public static class ModelRouter
{
    /// <summary>Resolves how a task should be dispatched.</summary>
    /// <param name="task">What is being asked for.</param>
    /// <param name="settings">The configured models.</param>
    /// <param name="temperature">Temperature the conversation asks for, for a reply.</param>
    /// <param name="maxTokens">Ceiling the conversation asks for, for a reply.</param>
    /// <returns>The choice.</returns>
    public static ModelChoice For(
        ModelTask task,
        ModelOptions settings,
        double? temperature = null,
        int? maxTokens = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return task switch
        {
            ModelTask.Summary => new ModelChoice(
                string.IsNullOrWhiteSpace(settings.BackgroundModel) ? settings.Name : settings.BackgroundModel,
                // Cold on purpose: an invented detail here becomes a fact the character
                // believes for the rest of the conversation.
                Temperature: 0.3,

                // Raised from 700, which was measured too low the day compression started
                // taking real batches: three of four summaries of a real story ended
                // mid-word — "KKG Facility Tour (" — because the account ran past the
                // ceiling. A summary is written in chronological order, so what a clipped
                // one loses is its tail: the most recent events in the stretch, which are
                // exactly the ones the next turn needs.
                MaxTokens: 1200),

            // The main model, not the background one: the question is about an adult scene and
            // whatever answers it has to be as willing to read that as the model writing it.
            // Cold for the same reason a summary is — an answer here can be promoted to a
            // pinned fact, so an embellishment becomes something the character believes.
            ModelTask.Aside => new ModelChoice(
                settings.Name,
                Temperature: 0.4,
                MaxTokens: 600),

            _ => new ModelChoice(
                settings.Name,
                temperature ?? settings.Temperature,
                maxTokens ?? settings.MaxTokens),
        };
    }
}
