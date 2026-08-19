using CyberCloud.ResourceManager.Contracts.Generation;

namespace CyberCloud.ResourceManager.Contracts.Tests.Generation;

/// <summary>
///     <c>CliTokens</c> — the derived answer to "does this short name already mean something".
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The rows below are the measured behaviour of <c>System.CommandLine</c> 2.0.10, not a
///         reading of its documentation.</b> A throwaway program built the four shapes against the
///         pinned package: two siblings sharing a token throw
///         <c>ArgumentException: An item with the same key has already been added</c>; a child whose
///         alias equals its parent's name throws; a child whose alias equals a <i>different</i>
///         parent's name does not. That last row is the one that matters, because the defence this
///         replaces was built entirely on the assumption that it does.
///     </para>
///     <para>
///         ⚠ <b>The comparison is ordinal.</b> Measured too: siblings named <c>alpha</c> and
///         <c>Alpha</c> parse cleanly. So this reports what the parser reports and does not fold case
///         — a check stricter than the failure it is guarding would refuse builds that work.
///     </para>
/// </remarks>
public sealed class CliTokenTests {
    const string Network = "CyberCloud.Network";

    [Fact]
    public void TheGroupKeyIsTheNamespacesLastSegmentLowerCasedWhole() {
        // Not kebab-cased: `dbfor-postgre-sql` is not a word anybody would type, and the alias table
        // maps `postgres` onto `dbforpostgresql`.
        CliTokens.GroupOf("CyberCloud.DBforPostgreSQL").ShouldBe("dbforpostgresql");
        CliTokens.GroupOf(Network).ShouldBe("network");
    }

    [Fact]
    public void TheCommandNameIsTheTypePathKebabedWithSlashesAsHyphens() {
        CliTokens.CommandOf("virtualNetworks/subnets").ShouldBe("virtual-networks-subnets");
        CliTokens.CommandOf("servers").ShouldBe("servers");
    }

    // ⚠ THERE IS DELIBERATELY NO "THE TREE AS IT STANDS COLLIDES NOWHERE" CASE HERE, and writing one
    // was the first thing this file did. It would be a list of every provider's namespace, type path
    // and short name — which is precisely the artifact this whole change exists to delete, and it
    // would have gone stale on the day the next provider landed exactly as its six predecessors did.
    // That question belongs where the real tree is:
    // <c>GeneratedSurfaceTests.NoGroupInAnyShippedTreeGivesOneTokenTwoMeanings</c> asks it of the verb
    // tree the build embedded, and ProviderRegistry.Build asks it of every provider a silo loads.
    // What is below is the mechanism, which literals are the right way to pin.

    [Fact]
    public void TwoTypesInOneGroupSharingAShortNameCollideAndBothAreNamed() {
        var problems = CliTokens.Collisions(
            [
                new(Network, "virtualNetworks", "vnet"),
                new(Network, "publicIpAddresses", "vnet")
            ]
        );

        var only = problems.ShouldHaveSingleItem();

        // ⚠ NAMING BOTH IS THE POINT. What System.CommandLine says is "An item with the same key has
        // already been added. Key: vnet" — no provider, no type, no file. A reader of that message
        // has to find both ends themselves, and three agents have had to.
        only.ShouldContain("CyberCloud.Network/virtualNetworks");
        only.ShouldContain("CyberCloud.Network/publicIpAddresses");
        only.ShouldContain("vnet");
    }

    [Fact]
    public void AShortNameThatIsASiblingsCommandNameCollides() {
        var problems = CliTokens.Collisions(
            [
                new(Network, "virtualNetworks", ""),
                new(Network, "publicIpAddresses", "virtual-networks")
            ]
        );

        problems.ShouldHaveSingleItem().ShouldContain("virtual-networks");
    }

    [Fact]
    public void AShortNameThatIsItsOwnGroupsKeyCollides() {
        // Measured: `cyc network network` throws, because the group command's token dictionary holds
        // the group's own name alongside its children's.
        CliTokens.Collisions([new(Network, "virtualNetworks", "network")])
            .ShouldHaveSingleItem()
            .ShouldContain("network");
    }

    [Fact]
    public void AShortNameThatIsANotherGroupsKeyDoesNotCollide() {
        // ⚠ THE ROW THAT REFUTES THE CHECK THIS REPLACES. `TheShortNameIsNoneOfTheTwelveCliGroupKeys
        // InTheTree` and its five siblings asserted a short name against EVERY group key in the tree.
        // Measured against System.CommandLine 2.0.10, `cyc monitor network` parses cleanly when
        // `network` is also a top-level group: the two live under different parents and never share a
        // dictionary. Those tests forbade thirteen strings that cannot collide and never checked the
        // one scope that can.
        CliTokens.Collisions(
            [
                new(Network, "virtualNetworks", "vnet"),
                new("CyberCloud.Monitor", "workspaces", "network")
            ]
        ).ShouldBeEmpty();
    }

    [Fact]
    public void ATypeWithNoShortNameContributesOnlyItsCommandName() {
        CliTokens.Collisions(
            [
                new(Network, "virtualNetworks", ""),
                new(Network, "publicIpAddresses", "")
            ]
        ).ShouldBeEmpty();
    }

    [Fact]
    public void TwoProvidersWhoseNamespacesEndInTheSameSegmentShareOneGroupAndAreCompared() {
        // ⚠ The cross-provider case the group key makes possible: two namespaces, one group, one
        // dictionary. A per-provider check cannot see this and the literal lists were an attempt to.
        CliTokens.Collisions(
            [
                new(Network, "virtualNetworks", "vnet"),
                new("Contoso.Network", "gateways", "vnet")
            ]
        ).ShouldHaveSingleItem().ShouldContain("Contoso.Network/gateways");
    }

    [Fact]
    public void TheOrderTheDeclarationsArriveInDoesNotChangeTheMessage() {
        // A message that differs between two runs of the same build is a message nobody trusts.
        CliDeclaration first = new(Network, "virtualNetworks", "vnet");
        CliDeclaration second = new(Network, "publicIpAddresses", "vnet");

        CliTokens.Collisions([first, second])
            .ShouldBe(CliTokens.Collisions([second, first]));
    }
}
