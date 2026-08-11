using CyberCloud.Core.Resources;
using Shouldly;

namespace CyberCloud.Core.Tests;

/// <summary>
///     A faithful model of <c>Orleans.Multitenant</c> 4.0.0's tenant-qualified key encoding, plus
///     the assertions <see cref="ResourceKey" /> depends on.
/// </summary>
/// <remarks>
///     <para>
///         <b>Provenance.</b> The algorithm below was read out of the shipped assembly
///         (<c>~/.nuget/packages/orleans.multitenant/4.0.0/lib/net10.0/Orleans.Multitenant.dll</c>,
///         nuspec repository commit <c>1762e3bedc55ee5ba9a488e4ad5f10a13312f2ed</c>), decompiled
///         with <c>ilspycmd</c>. It lives in the internal static class
///         <c>Orleans.Multitenant.Internal.TenantIdExtensions</c> — methods <c>AsTenantId</c>,
///         <c>GetTenantQualifiedKey</c>, <c>TryGetTenantId</c> and <c>GetKey</c>. It was then
///         <i>executed</i> against the real package out of tree: a capturing
///         <c>IGrainFactory</c> behind <c>IGrainFactory.ForTenant(t).GetGrain(type, key)</c> gives
///         the physical key verbatim, and <c>GrainIdExtensions.GetTenantId</c> /
///         <c>GetKeyWithinTenant</c> decode it. 18 encode/decode pairs round-tripped with zero
///         mismatches.
///     </para>
///     <para>
///         <b>What ADR-002 (docs/plan/02:127-135) gets right and wrong.</b> The ADR says the
///         physical key is <c>{tenantId}|{keyWithinTenant}</c> and that <c>'|'</c> is the separator,
///         <i>"doubled to escape"</i>, with <c>'~'</c> prefixed when the inner key starts with
///         either character.
///     </para>
///     <list type="bullet">
///         <item><b>CONFIRMED</b> — <c>'|'</c> is the separator.</item>
///         <item><b>CONFIRMED</b> — <c>'~'</c> is prefixed iff the inner key starts with <c>'|'</c> or <c>'~'</c>.</item>
///         <item>
///             <b>REFUTED, in part</b> — doubling escapes <c>'|'</c> <i>in the tenant id</i>, not in
///             the key within the tenant. A tenanted inner key is concatenated <b>verbatim</b>:
///             tenant <c>t1</c> + inner <c>a|b</c> gives <c>t1|a|b</c>, not <c>t1|a||b</c>. Decoding
///             still works because the terminator is the first <i>un</i>doubled <c>'|'</c> and the
///             tenant id can no longer contain one.
///         </item>
///         <item>
///             <b>NOT IN THE ADR</b> — the null-tenant branch is a different encoding entirely: no
///             prefix, no <c>'~'</c> rule, and the <i>whole key</i> has its <c>'|'</c> doubled.
///             docs/plan/06:87-93 makes <c>IClusterConnectionGrain</c> a null-tenant grain, so this
///             branch is live, not theoretical.
///         </item>
///     </list>
///     <para>
///         ⚠ This file is a <i>model</i>, not the package. <c>CyberCloud.Core</c> must not acquire
///         an Orleans dependency (docs/plan/03:226) and neither must its tests. The model is here so
///         that the properties <see cref="ResourceKey" /> relies on are asserted in CI rather than
///         asserted once in a scratch directory; the correspondence between the model and the
///         package was established by execution, as described above.
///     </para>
/// </remarks>
public class OrleansMultitenantEncodingTests
{
    [Theory]
    // tenant, keyWithinTenant, physical key — every row taken from the real-package run.
    [InlineData("t1", "sub/abc", "t1|sub/abc")]
    [InlineData("t1", "", "t1|")]
    [InlineData("t1", "|leading", "t1|~|leading")]
    [InlineData("t1", "~leading", "t1|~~leading")]
    [InlineData("t1", "|", "t1|~|")]
    [InlineData("t1", "~", "t1|~~")]
    [InlineData("t1", "~~", "t1|~~~")]
    [InlineData("t1", "a|b", "t1|a|b")]
    [InlineData("t1", "a||b", "t1|a||b")]
    [InlineData("t1", "a|", "t1|a|")]
    [InlineData("t1", "x~y", "t1|x~y")]
    [InlineData("t|x", "sub/abc", "t||x|sub/abc")]
    [InlineData("t|x", "|q", "t||x|~|q")]
    [InlineData("t|x", "t|x|forged", "t||x|t|x|forged")]
    [InlineData(null, "sub/abc", "sub/abc")]
    [InlineData(null, "a|b", "a||b")]
    [InlineData(null, "|lead", "||lead")]
    [InlineData(null, "~lead", "~lead")]
    public void TheModelMatchesTheShippedPackage(string? tenant, string inner, string expected)
    {
        OrleansMultitenantKeyModel.Qualify(tenant, inner).ShouldBe(expected);

        OrleansMultitenantKeyModel.ExtractTenant(expected).ShouldBe(tenant);
        OrleansMultitenantKeyModel.ExtractKey(expected).ShouldBe(inner);
    }

    [Fact]
    public void TheEncodingIsLosslessForArbitraryInnerKeys()
    {
        string?[] tenants = [null, "t", "t1", "t|x", "||", "~", "2b4a1c662e704a9d9d0a1f7ec1f1a4b3"];
        string[] inners =
        [
            "", "a", "|", "~", "||", "~~", "|~", "~|", "a|b", "a||b", "a|", "|a", "~a", "a~",
            "sub/abc", "res/0a1b2c3d", "t2|owned", "|t2|owned", "~|t2|owned", "\0", "\n", "/",
            "0/prod/CyberCloud.DBforPostgreSQL/servers/1"
        ];

        foreach (var tenant in tenants)
        {
            foreach (var inner in inners)
            {
                var physical = OrleansMultitenantKeyModel.Qualify(tenant, inner);

                OrleansMultitenantKeyModel.ExtractTenant(physical).ShouldBe(
                    tenant,
                    $"tenant '{Corpus.Printable(tenant ?? "<null>")}' + inner "
                    + $"'{Corpus.Printable(inner)}' -> '{Corpus.Printable(physical)}'");

                OrleansMultitenantKeyModel.ExtractKey(physical).ShouldBe(
                    inner,
                    $"tenant '{Corpus.Printable(tenant ?? "<null>")}' + inner "
                    + $"'{Corpus.Printable(inner)}' -> '{Corpus.Printable(physical)}'");
            }
        }
    }

    [Fact]
    public void NoInnerKeyCanForgeADifferentTenant()
    {
        // The property that makes ADR-002's design safe despite the inner key not being escaped.
        // It holds because the tenant id escapes its OWN pipes and terminates at the first
        // undoubled one, so the boundary is fixed before the inner key is ever appended.
        string[] attacks =
        [
            "t2|owned", "|t2|owned", "~|t2|owned", "||t2|owned", "a||b|c", "|", "||", "~|", "~~|",
            "\0t2|owned", "t2||owned"
        ];

        foreach (var attack in attacks)
        {
            var physical = OrleansMultitenantKeyModel.Qualify("t1", attack);

            OrleansMultitenantKeyModel.ExtractTenant(physical).ShouldBe(
                "t1",
                $"inner key '{Corpus.Printable(attack)}' produced "
                + $"'{Corpus.Printable(physical)}'");
        }
    }

    [Fact]
    public void ResourceKeysNeverTouchAnyOfTheInterestingBranches()
    {
        // This is the whole point of the exercise. Every key CyberCloud.Core produces is plain
        // enough that the encoding is a single concatenation — so a physical key in Redis, in a
        // log line or in a trace reads exactly as the key that was constructed.
        foreach (var id in Corpus.ResourceIds(2_000, seed: 7))
        {
            var inner = ResourceKey.From(id).ToKeyWithinTenant();
            var tenant = id.TenantId.ToString("N", System.Globalization.CultureInfo.InvariantCulture);

            OrleansMultitenantKeyModel.Qualify(tenant, inner).ShouldBe(tenant + "|" + inner);
            OrleansMultitenantKeyModel.ExtractKey(tenant + "|" + inner).ShouldBe(inner);
            OrleansMultitenantKeyModel.ExtractTenant(tenant + "|" + inner).ShouldBe(tenant);
        }
    }

    [Fact]
    public void ANullTenantKeyCanNeverBeMistakenForATenantedOne()
    {
        // docs/plan/06:87-93 keeps IClusterConnectionGrain in the null tenant. If a null-tenant
        // physical key could parse as tenanted, that grain's key space would overlap a tenant's.
        // It cannot: the null-tenant branch doubles every '|', so no undoubled one survives.
        string[] inners = ["cluster/0a1b2c3d", "a|b", "|lead", "t1|sub/abc", "||", "|"];

        foreach (var inner in inners)
        {
            var physical = OrleansMultitenantKeyModel.Qualify(null, inner);

            OrleansMultitenantKeyModel.ExtractTenant(physical).ShouldBeNull(
                $"'{Corpus.Printable(inner)}' -> '{Corpus.Printable(physical)}'");
        }
    }
}

/// <summary>
///     The model. Transcribed from <c>Orleans.Multitenant.Internal.TenantIdExtensions</c> — see the
///     provenance note on <see cref="OrleansMultitenantEncodingTests" />.
/// </summary>
static class OrleansMultitenantKeyModel
{
    /// <summary><c>AsTenantId</c> + <c>GetTenantQualifiedKey</c>.</summary>
    public static string Qualify(string? tenant, string keyWithinTenant)
    {
        if (tenant is null)
        {
            // Null tenant: no prefix, no '~' rule, the whole key is escaped.
            return keyWithinTenant.Replace("|", "||", StringComparison.Ordinal);
        }

        var escapedTenant = tenant.Replace("|", "||", StringComparison.Ordinal) + "|";

        var text = keyWithinTenant;
        if (text.Length > 0 && (text[0] == '|' || text[0] == '~'))
        {
            text = "~" + text;
        }

        return escapedTenant + text;
    }

    /// <summary><c>TryGetTenantId</c> + <c>TenantIdString</c>.</summary>
    public static string? ExtractTenant(string physicalKey)
    {
        var end = TenantEnd(physicalKey);
        return end < 0
            ? null
            : physicalKey[..(end - 1)].Replace("||", "|", StringComparison.Ordinal);
    }

    /// <summary><c>GetKey</c>.</summary>
    public static string ExtractKey(string physicalKey)
    {
        var end = TenantEnd(physicalKey);
        if (end < 0)
        {
            return physicalKey.Replace("||", "|", StringComparison.Ordinal);
        }

        var start = end;
        if (start + 2 <= physicalKey.Length
            && physicalKey[start] == '~'
            && (physicalKey[start + 1] == '|' || physicalKey[start + 1] == '~'))
        {
            start++;
        }

        return physicalKey[start..];
    }

    /// <summary>
    ///     The index one past the tenant terminator — the first <c>'|'</c> that is not part of a
    ///     <c>"||"</c> pair — or <c>-1</c> when there is no tenant.
    /// </summary>
    static int TenantEnd(string physicalKey)
    {
        for (var i = 0; i < physicalKey.Length; i++)
        {
            if (physicalKey[i] != '|')
            {
                continue;
            }

            i++;
            if (i == physicalKey.Length || physicalKey[i] != '|')
            {
                return i;
            }
        }

        return -1;
    }
}
