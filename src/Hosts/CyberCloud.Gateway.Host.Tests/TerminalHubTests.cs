using CyberCloud.Gateway.Host.Hubs;
using CyberCloud.Gateway.Host.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;
using System.Reflection;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     The terminal hub's client contract — the names the portal's <c>terminal-session.spec.ts</c>
///     pins from its side, pinned from this one.
/// </summary>
/// <remarks>
///     SignalR binds a hub method by the string a client sends, so the contract between
///     <c>TerminalHub</c> and the portal is five words and nothing the compiler checks. This suite
///     reads them off the hub's attributes; the portal's spec asserts the same five literals against
///     its <c>terminalProtocol</c>. A change to one side fails one of the two, which is the whole
///     point of there being two.
/// </remarks>
public sealed class TerminalHubTests {
    [Fact]
    public void TheWireNamesAreTheFiveThePortalSpeaks() {
        TerminalProtocol.Attach.ShouldBe("Attach");
        TerminalProtocol.Send.ShouldBe("Send");
        TerminalProtocol.Resize.ShouldBe("Resize");
        TerminalProtocol.Output.ShouldBe("Output");
        TerminalProtocol.Ended.ShouldBe("Ended");

        var bound = typeof(TerminalHub)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            // The connection lifecycle overrides are SignalR's, not the contract's.
            .Where(static m => m.GetBaseDefinition().DeclaringType == typeof(TerminalHub))
            .Select(static m => m.GetCustomAttribute<HubMethodNameAttribute>()?.Name ?? m.Name)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();

        bound.ShouldBe([TerminalProtocol.Attach, TerminalProtocol.Resize, TerminalProtocol.Send]);
    }

    [Fact]
    public async Task ASessionIdThatIsNotAPodUidIsRefusedBeforeAnyGrainIsAddressed() {
        // ⚠ The session id is the one caller-supplied part of the grain key. A crafted one — a path
        // separator, another prefix — is refused on its shape, before ForTenant is even reached, so
        // it cannot become a key the session grain did not mint. The grain factory is a substitute
        // that must never be touched.
        var grains = Substitute.For<IGrainFactory>();
        var hub = new TerminalHub(grains, new ProcessConcurrencyLimiter(new()), Substitute.For<IHubContext<TerminalHub>>());

        foreach (var crafted in new[] { "s", "../conn/x", "terminal/" + Guid.NewGuid().ToString("D"), "" }) {
            var attach = await Should.ThrowAsync<HubException>(() => hub.Attach(crafted, 80, 24));
            var send = await Should.ThrowAsync<HubException>(() => hub.Send(crafted, [1, 2, 3]));
            var resize = await Should.ThrowAsync<HubException>(() => hub.Resize(crafted, 100, 30));

            foreach (var refusal in new[] { attach, send, resize }) {
                refusal.Message.ShouldContain("is not a session id");
            }
        }

        grains.ReceivedCalls().ShouldBeEmpty();
    }
}
