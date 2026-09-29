using Airp.Web;
using Microsoft.Extensions.Primitives;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// The web pages let one tailnet account in, and only while nothing but tailscale serve can
/// reach them.
/// </summary>
/// <remarks>
/// The login is a header, measured on the real machine as what tailscale serve sets. A header is
/// trustworthy only if nobody else could have sent it, which is why the second half — refusing
/// to listen anywhere but loopback — matters as much as the first.
/// </remarks>
public class WebGateTests
{
    private const string Owner = "argamboad@gmail.com";

    [Fact]
    public void The_owner_is_let_in()
        => Gate.Admits(new StringValues(Owner), Owner).ShouldBeTrue();

    [Fact]
    public void Case_does_not_decide_who_someone_is()
        => Gate.Admits(new StringValues("Argamboad@Gmail.com"), Owner).ShouldBeTrue();

    [Theory]
    [InlineData("someone.else@gmail.com")]
    [InlineData("")]
    public void Anyone_else_is_turned_away(string login)
        => Gate.Admits(new StringValues(login), Owner).ShouldBeFalse();

    [Fact]
    public void No_header_is_turned_away()
        => Gate.Admits(StringValues.Empty, Owner).ShouldBeFalse();

    [Fact]
    public void Two_headers_are_turned_away_even_if_one_is_the_owner()
        => Gate.Admits(new StringValues([Owner, Owner]), Owner).ShouldBeFalse();

    [Fact]
    public void Nothing_configured_lets_nobody_in()
        => Gate.Admits(new StringValues(Owner), "  ").ShouldBeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("http://127.0.0.1:5291")]
    [InlineData("http://localhost:5291")]
    [InlineData("http://[::1]:5291")]
    [InlineData("http://127.0.0.1:5291;http://localhost:5292")]
    public void Loopback_is_somewhere_it_may_listen(string? urls)
        => Gate.OnlyLoopback(urls).ShouldBeTrue();

    [Theory]
    [InlineData("http://0.0.0.0:5291")]
    [InlineData("http://*:5291")]
    [InlineData("http://+:5291")]
    [InlineData("http://100.99.143.97:5291")]
    [InlineData("http://127.0.0.1:5291;http://0.0.0.0:5292")]
    [InlineData("not a url")]
    public void Anywhere_else_is_refused(string urls)
        => Gate.OnlyLoopback(urls).ShouldBeFalse();

    [Fact]
    public void A_bare_port_list_binds_every_interface_and_is_refused()
    {
        Gate.OnlyLoopback(null, httpPorts: "8080").ShouldBeFalse();
        Gate.OnlyLoopback(null, httpsPorts: "8443").ShouldBeFalse();
    }
}
