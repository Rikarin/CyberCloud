using CyberCloud.Gateway.Host.Hubs;
using Microsoft.AspNetCore.SignalR;
using System.Reflection;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     The terminal hub's client contract — the names the portal's <c>terminal-session.spec.ts</c>
///     pins from its side, pinned from this one.
/// </summary>
/// <remarks>
///     SignalR binds a hub method by the string a client sends, so the contract between
///     <c>TerminalHub</c> and the portal is four words and nothing the compiler checks. This suite
///     reads them off the hub's attributes; the portal's spec asserts the same four literals against
///     its <c>terminalProtocol</c>. A change to one side fails one of the two, which is the whole
///     point of there being two.
/// </remarks>
public sealed class TerminalHubTests {
    [Fact]
    public void TheWireNamesAreTheFourThePortalSpeaks() {
        TerminalProtocol.Attach.ShouldBe("Attach");
        TerminalProtocol.Send.ShouldBe("Send");
        TerminalProtocol.Resize.ShouldBe("Resize");
        TerminalProtocol.Output.ShouldBe("Output");

        var bound = typeof(TerminalHub)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(static m => m.GetCustomAttribute<HubMethodNameAttribute>()?.Name ?? m.Name)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();

        bound.ShouldBe([TerminalProtocol.Attach, TerminalProtocol.Resize, TerminalProtocol.Send]);
    }

    [Fact]
    public async Task EveryMethodRefusesByNameUntilTheSessionGrainExists() {
        // docs/plan/19's session grain is owed, and a hub that accepted and dropped input would be
        // worse than one that says so. The message is what the portal shows in the pane.
        var hub = new TerminalHub();

        var attach = await Should.ThrowAsync<HubException>(() => hub.Attach("s", 80, 24));
        var send = await Should.ThrowAsync<HubException>(() => hub.Send("s", [1, 2, 3]));
        var resize = await Should.ThrowAsync<HubException>(() => hub.Resize("s", 100, 30));

        foreach (var refusal in new[] { attach, send, resize }) {
            refusal.Message.ShouldContain("docs/plan/19");
            refusal.Message.ShouldContain("the data plane is owed");
        }
    }
}
