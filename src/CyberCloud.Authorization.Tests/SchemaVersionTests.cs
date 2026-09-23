using Shouldly;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.Authorization.Tests;

/// <summary>
///     Pins <see cref="CyberCloudSchema.SchemaVersion" /> to the schema it names, so one version
///     number can't cover two schemas.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The failure this guards is a merge, not an edit.</b> The version is what
///         invalidates every cached check and every membership-index slice (<c>CheckGrain</c>,
///         <c>MembershipIndexGrain</c>). Two branches that each change the schema and each bump
///         3 to 4 merge without a conflict: <c>SchemaVersion = 4</c> is the same line on both
///         sides. The merged schema is then a third schema under a version one of the branches has
///         already deployed, so a silo trusts a cache computed under the other. Found by the #30
///         review, whose branch made exactly that bump.
///     </para>
///     <para>
///         ⚠ <b>So the fingerprint sits beside the number, and two branches can't agree on it.</b>
///         Each branch edits the line below to its own schema's hash, so a merge of two conflicts
///         textually and somebody has to recount. An edit that changes the schema without bumping
///         the version fails here, and the message says to bump it.
///     </para>
/// </remarks>
public sealed class SchemaVersionTests {
    /// <summary>The version this tree's schema carries, and a hash of every member's rewrite.</summary>
    /// <remarks>
    ///     ⚠ Change both together, and only together. A new fingerprint under the same version is
    ///     the defect this file exists for.
    /// </remarks>
    static readonly (int Version, string Fingerprint) Pinned = (4, "af37848fde295aed");

    [Fact]
    public void TheSchemaVersionNamesExactlyOneSchema() {
        var fingerprint = Fingerprint(CyberCloudSchema.Instance);

        CyberCloudSchema.SchemaVersion.ShouldBe(
            Pinned.Version,
            "CyberCloudSchema.SchemaVersion moved. Pin the new version with this schema's fingerprint, "
            + $"{fingerprint}, in the same change."
        );

        fingerprint.ShouldBe(
            Pinned.Fingerprint,
            $"the schema changed and its version is still {Pinned.Version}. Bump CyberCloudSchema.SchemaVersion, "
            + "so every cached check and index slice computed under the old schema is discarded, and pin "
            + $"the new version with fingerprint {fingerprint}. If this is a merge, both sides changed the "
            + "schema under one number: recount, and take the next version for the combined schema."
        );
    }

    /// <summary>Hashes every type's members, their kind and their rewrite, in ordinal order.</summary>
    /// <param name="schema">The schema to hash.</param>
    static string Fingerprint(AuthorizationSchema schema) {
        var text = new StringBuilder();

        foreach (var type in schema.TypeNames.Order(StringComparer.Ordinal)) {
            foreach (var member in schema.Type(type)!.Members.OrderBy(static x => x.Name, StringComparer.Ordinal)) {
                var kind = member.IsPermission ? "permission" : member.IsRole ? "role" : "relation";
                text.Append(CultureInfo.InvariantCulture, $"{type}.{member.Name} {kind} = {member.Expression}\n");
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }
}
