using Microsoft.Extensions.DependencyInjection;
using Airp.Domain.Conversations;
using Airp.Terminal.Ui;

namespace Airp.Terminal.Views;

/// <summary>
/// Opens a list row.
/// </summary>
/// <remarks>
/// A single place on purpose. Opening a row was once decided separately by the list, the
/// session restore and global search, and they drifted: two of them sent a chat to a screen
/// built for something else.
/// </remarks>
internal static class RowView
{
    /// <summary>Builds the screen a row opens into.</summary>
    /// <param name="row">The chat being opened.</param>
    /// <param name="services">Container used to construct the view.</param>
    /// <returns>The view.</returns>
    public static IView For(Chat row, IServiceProvider services)
        => ActivatorUtilities.CreateInstance<ConversationView>(services, row);
}
