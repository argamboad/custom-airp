namespace Airp.Infrastructure.Providers;

/// <summary>
/// The answer to a question asked about the story, out of character.
/// </summary>
/// <remarks>
/// It is handed back rather than added to a transcript. Nothing that reads a conversation —
/// the prompt builder, the summariser, the extractor, retrieval — will ever see it. The one
/// place it is written is the asides table, so that a billed call still shows up in the audit.
/// </remarks>
/// <param name="Question">What was asked.</param>
/// <param name="Answer">What came back.</param>
/// <param name="Model">The model that answered.</param>
/// <param name="Provider">The backend that served it, worth knowing when an answer reads oddly.</param>
/// <param name="EstimatedPromptTokens">What this client estimated the prompt at.</param>
/// <param name="PromptTokens">What the provider reported for the prompt.</param>
/// <param name="CompletionTokens">What the provider reported for the answer.</param>
/// <param name="ContextAudit">The per-layer breakdown, as the audit prints it.</param>
public readonly record struct AskAnswer(
    string Question,
    string Answer,
    string? Model,
    string? Provider,
    int EstimatedPromptTokens,
    int? PromptTokens,
    int? CompletionTokens,
    string? ContextAudit);
