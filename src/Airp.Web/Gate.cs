using System.Net;
using Microsoft.Extensions.Primitives;

namespace Airp.Web;

/// <summary>
/// Who may see these pages: one tailnet account, arriving through <c>tailscale serve</c>.
/// </summary>
/// <remarks>
/// <para>
/// There is no password, because there is nothing to type it into that a phone would not
/// then remember in the clear. <c>tailscale serve</c> passes every request on with a
/// <c>Tailscale-User-Login</c> header naming the tailnet account that sent it — measured on the
/// real machine before this was written — and the gate lets one account through.
/// </para>
/// <para>
/// A header is only as good as whoever set it, so the second half is where the app listens:
/// loopback and nothing else. Then the only thing that can reach it is <c>tailscale serve</c> on
/// the same machine, and the header cannot have come from anyone else. The app refuses to start
/// otherwise, rather than start and trust a header anyone on the network could send.
/// </para>
/// </remarks>
public static class Gate
{
    /// <summary>The header <c>tailscale serve</c> sets to the sender's tailnet login.</summary>
    public const string LoginHeader = "Tailscale-User-Login";

    /// <summary>Whether a request came from the one account allowed in.</summary>
    /// <param name="login">The header's values as they arrived.</param>
    /// <param name="allowed">The account allowed in.</param>
    /// <returns><see langword="true"/> for exactly one value naming that account.</returns>
    public static bool Admits(StringValues login, string allowed)
        => login.Count == 1
           && !string.IsNullOrWhiteSpace(allowed)
           && string.Equals(login[0]?.Trim(), allowed.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether everything the app has been told to listen on is loopback.</summary>
    /// <param name="urls">The <c>urls</c> setting (from <c>--urls</c> or its variable), if any.</param>
    /// <param name="httpPorts">The <c>http_ports</c> setting, if any.</param>
    /// <param name="httpsPorts">The <c>https_ports</c> setting, if any.</param>
    /// <returns><see langword="true"/> when nothing could be reached from another machine.</returns>
    /// <remarks>
    /// Nothing set means the framework's own default, <c>localhost:5000</c>, which is loopback.
    /// A bare port list always binds every interface, so any is refused.
    /// </remarks>
    public static bool OnlyLoopback(string? urls, string? httpPorts = null, string? httpsPorts = null)
    {
        if (!string.IsNullOrWhiteSpace(httpPorts) || !string.IsNullOrWhiteSpace(httpsPorts))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(urls))
        {
            return true;
        }

        foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(url.Replace("://+", "://plus", StringComparison.Ordinal).Replace("://*", "://star", StringComparison.Ordinal), UriKind.Absolute, out var uri))
            {
                return false;
            }

            var host = uri.Host.Trim('[', ']');
            var loopback = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                           || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

            if (!loopback)
            {
                return false;
            }
        }

        return true;
    }
}
