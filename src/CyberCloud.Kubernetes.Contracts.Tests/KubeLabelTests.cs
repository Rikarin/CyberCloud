using CyberCloud.Core.Resources;
using Shouldly;
using System.Text.RegularExpressions;

namespace CyberCloud.Kubernetes.Contracts.Tests;

/// <summary>
///     The seven mandatory labels and two annotations of ADR-013, checked against Kubernetes' own
///     validation rules rather than against our reading of them.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The regular expressions below are the independent oracle, and that is the point of
///             writing them out.
///         </b>
///         Asserting <c>LabelSyntax.IsValidValue(x)</c> against
///         <c>LabelSyntax</c>'s own rules would be a tautology. These patterns are transcribed from
///         <c>k8s.io/apimachinery/pkg/util/validation</c> — <c>IsValidLabelValue</c>,
///         <c>IsQualifiedName</c> and <c>IsDNS1123Subdomain</c> — and are what the API server
///         actually applies at admission. A disagreement between the two implementations is exactly
///         the bug this file exists to catch.
///     </para>
///     <para>
///         docs/plan/09 § The command builder:
///         <i>
///             "Label values are limited to 63 characters and a
///             restricted alphabet. GUIDs in canonical form are 36 characters and legal. The path is not
///             — hence path as an annotation, id as a label … This is exactly the kind of detail that
///             becomes a two-day bug six months in, so it is decided here."
///         </i>
///     </para>
/// </remarks>
public sealed class KubeLabelTests {
    // ── Kubernetes' own rules, transcribed ─────────────────────────────────────────────────────

    /// <summary><c>IsValidLabelValue</c> — may be empty, ≤63, alphanumeric-bounded.</summary>
    static readonly Regex LabelValue =
        new(@"^(([A-Za-z0-9][-A-Za-z0-9_.]*)?[A-Za-z0-9])?$", RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary><c>IsQualifiedName</c>'s name part — non-empty, ≤63, alphanumeric-bounded.</summary>
    static readonly Regex QualifiedNamePart =
        new(@"^([A-Za-z0-9][-A-Za-z0-9_.]*)?[A-Za-z0-9]$", RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary><c>IsDNS1123Subdomain</c> — the key prefix. ⚠ Lower case only.</summary>
    static readonly Regex DnsSubdomain =
        new(
            @"^[a-z0-9]([-a-z0-9]*[a-z0-9])?(\.[a-z0-9]([-a-z0-9]*[a-z0-9])?)*$",
            RegexOptions.None,
            TimeSpan.FromSeconds(1)
        );

    // ── The label KEY rules — the half docs/plan/09 does not state ─────────────────────────────

    [Fact]
    public void TheSetIsExactlySevenKeys() {
        // ADR-013 lists seven. A drift in either direction is a change to the contract that billing,
        // orphan detection and the admission policy all depend on.
        KubeLabels.Mandatory.Length.ShouldBe(7);

        KubeLabels.Mandatory.ShouldBe(
            [
                "cybercloud.io/tenant-id",
                "cybercloud.io/subscription-id",
                "cybercloud.io/resource-group",
                "cybercloud.io/resource-id",
                "cybercloud.io/resource-type",
                "cybercloud.io/api-version",
                "cybercloud.io/managed-by"
            ]
        );
    }

    [Fact]
    public void EveryMandatoryKeyIsALegalKubernetesLabelKey() {
        foreach (var key in KubeLabels.Mandatory) {
            KubernetesAcceptsKey(key).ShouldBeTrue($"'{key}' must pass IsQualifiedName.");
            LabelSyntax.IsValidKey(key).ShouldBeTrue($"LabelSyntax disagrees about '{key}'.");
        }
    }

    [Fact]
    public void EveryMandatoryAnnotationKeyIsALegalKubernetesLabelKey() {
        // An annotation KEY obeys the same IsQualifiedName rule a label key does; only the VALUE
        // rules differ, which is the entire reason the resource path is an annotation.
        foreach (var key in KubeLabels.MandatoryAnnotations) {
            KubernetesAcceptsKey(key).ShouldBeTrue($"'{key}' must pass IsQualifiedName.");
            LabelSyntax.IsValidKey(key).ShouldBeTrue();
        }
    }

    [Fact]
    public void ThePrefixIsALowerCaseDnsSubdomainAndTheUpperCaseSpellingIsRejected() {
        // ⚠ The one-character difference between "works" and "every object in the platform fails
        // admission". A key's PREFIX is a DNS-1123 subdomain, which is lower-case only — while the
        // key's NAME part and every VALUE may contain upper case. Three rules, three alphabets.
        DnsSubdomain.IsMatch(KubeLabels.Prefix).ShouldBeTrue();

        KubernetesAcceptsKey("CyberCloud.io/tenant-id").ShouldBeFalse();
        LabelSyntax.IsValidKey("CyberCloud.io/tenant-id").ShouldBeFalse();

        LabelSyntax.ValidateKey("CyberCloud.io/tenant-id").Error!.Message
            .ShouldContain("lower-case");
    }

    [Fact]
    public void AKeyNamePartMayNotBeEmptyEvenThoughAValueMay() {
        // The single place the two rules diverge on emptiness, and the two are routinely got backwards.
        LabelSyntax.IsValidValue(string.Empty).ShouldBeTrue();
        KubernetesAcceptsValue(string.Empty).ShouldBeTrue();

        LabelSyntax.IsValidKey("cybercloud.io/").ShouldBeFalse();
        KubernetesAcceptsKey("cybercloud.io/").ShouldBeFalse();
    }

    [Fact]
    public void AKeyWithTwoSlashesIsNotAKey() {
        LabelSyntax.IsValidKey("cybercloud.io/a/b").ShouldBeFalse();
        KubernetesAcceptsKey("cybercloud.io/a/b").ShouldBeFalse();
    }

    [Fact]
    public void EverySevenLabelValueIsLegalForADeeplyNestedResourceType() {
        // ⚠ THE HEADLINE ASSERTION. `servers/databases` is deep enough that the '/' → '_' rule
        // matters, and every value is checked against Kubernetes' own regex rather than ours.
        var labels = EmittedLabels(NestedResource());

        labels.Count.ShouldBe(7);

        foreach (var (key, value) in labels) {
            KubernetesAcceptsKey(key).ShouldBeTrue($"key '{key}'");
            KubernetesAcceptsValue(value)
                .ShouldBeTrue(
                    $"the value of '{key}' is '{value}' ({value.Length} chars), which the API server "
                    + "would reject at admission."
                );
        }
    }

    [Fact]
    public void TheEmittedValuesAreExactlyWhatAdr013Specifies() {
        var labels = EmittedLabels(NestedResource());

        labels[KubeLabels.TenantId].ShouldBe("9f2c1b7e-3d4a-4f21-9c6b-0a1e2d3c4b5a");
        labels[KubeLabels.SubscriptionId].ShouldBe("77de4a10-1b2c-4d3e-8f90-a1b2c3d4e5f6");
        labels[KubeLabels.ResourceGroup].ShouldBe("prod");
        labels[KubeLabels.ResourceId].ShouldBe("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d");
        labels[KubeLabels.ApiVersion].ShouldBe("2026-08-01");
        labels[KubeLabels.ManagedBy].ShouldBe("cybercloud");

        // ⚠ FULLY LOWER-CASE, including the leading 'c'.
        //
        // docs/plan/09 § The command builder's worked example prints
        //   cybercloud.io/resource-type: cyberCloud.dbforpostgresql_servers
        // with a capital C in "cyberCloud", and its own prose two paragraphs later says "Resource
        // type is lowercased and '/' replaced by '_'". The two disagree; the prose is right and the
        // example is a typo. It matters because the label is a SELECTOR: a mixed-case value would
        // mean `kubectl get -l cybercloud.io/resource-type=cybercloud...` silently returns nothing.
        labels[KubeLabels.ResourceType].ShouldBe("cybercloud.dbforpostgresql_servers_databases");
    }

    [Fact]
    public void ACanonicalGuidIs36CharactersAndFitsWithRoomToSpare() {
        // docs/plan/09 § The command builder asserts this as the reason ids are labels; asserted
        // here so the claim is checked rather than believed.
        var value = KubeLabels.GuidValue(Guid.NewGuid());

        value.Length.ShouldBe(36);
        value.Length.ShouldBeLessThan(LabelSyntax.MaxValueLength);
        KubernetesAcceptsValue(value).ShouldBeTrue();
    }

    [Fact]
    public void TheResourcePathIsNotALegalLabelValueWhichIsWhyItIsAnAnnotation() {
        // ⚠ The load-bearing negative. If this ever became true, the path-as-annotation decision
        // would be arbitrary rather than forced — and if it silently became FALSE for a short path
        // somebody would "simplify" it into a label and break the long ones.
        var path = NestedResource().Path;

        path.Length.ShouldBeGreaterThan(LabelSyntax.MaxValueLength);
        path.ShouldContain("/");
        KubernetesAcceptsValue(path).ShouldBeFalse();

        // …and it is emitted as an annotation, in full, unmangled.
        var command = BuildCommand(NestedResource());
        command.Annotations[KubeLabels.ResourcePathAnnotation].ShouldBe(path);
    }

    [Fact]
    public void TheReconcileHashIsTooLongForALabelToo() {
        var hash = KubeLabels.ReconcileHash("""{"a":1}""");

        hash.ShouldStartWith("sha256:");
        hash.Length.ShouldBe(7 + 64);
        hash.Length.ShouldBeGreaterThan(LabelSyntax.MaxValueLength);
        KubernetesAcceptsValue(hash).ShouldBeFalse("':' is not in the label-value alphabet.");
    }

    [Fact]
    public void AResourceGroupNameIsAlwaysALegalLabelValueBecauseResourceNamingSaysSo() {
        // ResourceNaming.MaxLength is 63 "equal to the Kubernetes label-value cap, deliberately".
        // This is the assertion that keeps those two constants tied together.
        ResourceNaming.MaxLength.ShouldBe(LabelSyntax.MaxValueLength);

        var longest = new string('a', ResourceNaming.MaxLength);
        ResourceNaming.IsValid(longest).ShouldBeTrue();
        KubernetesAcceptsValue(longest).ShouldBeTrue();
    }

    // ── The unbounded one ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void AResourceTypeTooLongForALabelIsRejectedRatherThanTruncated() {
        // ⚠ THE GAP IN ADR-013's LABEL SET.
        //
        // ADR-013 bounds the GUIDs (36) and excludes the path (too long), and says nothing about the
        // resource type. ResourceTypeName permits a namespace of two or more 63-character segments
        // plus a type path of up to three, so `resource-type` is the one member of the seven that
        // can legally exceed 63 characters.
        //
        // Truncating would map two distinct resource types onto one label value, which silently
        // breaks orphan detection and billing attribution — the two things ADR-013 says the labels
        // exist for. So it fails, loudly, before the object ever reaches the API server.
        var longType = new ResourceTypeName(
            "CyberCloud." + new string('x', 50),
            new string('y', 40) + "/" + new string('z', 40)
        );

        var value = KubeLabels.ResourceTypeValue(longType);
        value.Length.ShouldBeGreaterThan(LabelSyntax.MaxValueLength);
        KubernetesAcceptsValue(value).ShouldBeFalse();

        var id = NestedResource() with { Type = longType };
        var built = Builder(id).TryBuild();

        built.IsFailure.ShouldBeTrue("a resource type that cannot be a label value must fail here, not at admission.");

        var message = built.Error!.Message;
        message.ShouldContain("63");
        message.ShouldContain("truncat");
        message.ShouldContain(KubeLabels.ResourceType);
    }

    [Fact]
    public void AnApiVersionThatIsNotALegalLabelValueIsRejected() {
        // api-version reaches the label straight from the request's query string (docs/plan/08
        // § Versioning), so it is the one of the seven whose value is most nearly caller-controlled.
        var built = Builder(NestedResource())
            .WithApiVersion("2026-08-01 preview")
            .TryBuild();

        built.IsFailure.ShouldBeTrue();
        built.Error!.Message.ShouldContain(KubeLabels.ApiVersion);
    }

    // ── Non-overridable ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ACallerCannotReplaceAMandatoryLabel() {
        // ADR-013: "injected and non-overridable". Loud rather than silent — a caller that believed
        // its override took effect would have detached its objects from billing and orphan
        // detection without knowing.
        foreach (var key in KubeLabels.Mandatory) {
            var thrown = Should.Throw<ArgumentException>(() => Builder(NestedResource()).WithLabels((key, "hijacked")));

            thrown.Message.ShouldContain("non-overridable");
        }
    }

    [Fact]
    public void ACallerCannotReplaceAMandatoryAnnotation() {
        foreach (var key in KubeLabels.MandatoryAnnotations) {
            Should.Throw<ArgumentException>(() => Builder(NestedResource()).WithAnnotations((key, "hijacked")));
        }
    }

    [Fact]
    public void ALabelInsideTheRenderedObjectCannotOverrideAMandatoryOneEither() {
        // The other override route: not a WithLabels call, but a label baked into the body a chart
        // or a provider rendered. Ours must win the merge.
        var command = KubeCommand.For(new NullConnection())
            .WithTenantId(NestedResource().TenantId)
            .WithResourceId(NestedResource())
            .WithKind(new() { Group = "apps", Version = "v1", Kind = "Deployment", Plural = "deployments" })
            .ObjectJson(
                """
                {"metadata":{"name":"main","labels":{"cybercloud.io/tenant-id":"00000000-0000-0000-0000-00000000dead","app":"mine"}}}
                """
            )
            .Build();

        command.Labels[KubeLabels.TenantId]
            .ShouldBe("9f2c1b7e-3d4a-4f21-9c6b-0a1e2d3c4b5a");
        command.Body.ShouldContain("9f2c1b7e-3d4a-4f21-9c6b-0a1e2d3c4b5a");
        command.Body.ShouldNotContain("00000000-0000-0000-0000-00000000dead");

        // …and a label the caller invented is preserved.
        command.Body.ShouldContain("\"app\":\"mine\"");
    }

    [Fact]
    public void AnExtraLabelWithIllegalSyntaxIsRejectedAtTheCallSite() {
        Should.Throw<ArgumentException>(() =>
            Builder(NestedResource()).WithLabels(("cybercloud.io/mine", "no spaces allowed"))
        );

        Should.Throw<ArgumentException>(() => Builder(NestedResource()).WithLabels(("Not A Key", "fine")));
    }

    static bool KubernetesAcceptsValue(string value) => value.Length <= 63 && LabelValue.IsMatch(value);

    static bool KubernetesAcceptsKey(string key) {
        var parts = key.Split('/');
        return parts.Length switch {
            1 => parts[0].Length is > 0 and <= 63 && QualifiedNamePart.IsMatch(parts[0]),
            2 => parts[0].Length is > 0 and <= 253
                && DnsSubdomain.IsMatch(parts[0])
                && parts[1].Length is > 0 and <= 63
                && QualifiedNamePart.IsMatch(parts[1]),
            _ => false
        };
    }

    // ── The label VALUE rules, per emitted label ───────────────────────────────────────────────

    /// <summary>
    ///     A resource id deep enough to be interesting — <c>servers/databases</c>, which is the
    ///     nested case docs/plan/08 § The provider registry shows and the one that exercises the
    ///     <c>/</c> → <c>_</c> rule.
    /// </summary>
    static ResourceId NestedResource() =>
        new(
            Guid.Parse("9f2c1b7e-3d4a-4f21-9c6b-0a1e2d3c4b5a"),
            Guid.Parse("77de4a10-1b2c-4d3e-8f90-a1b2c3d4e5f6"),
            "prod",
            new("CyberCloud.DBforPostgreSQL", "servers/databases"),
            "main",
            Guid.Parse("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d")
        );

    static IReadOnlyDictionary<string, string> EmittedLabels(ResourceId id, string apiVersion = "2026-08-01") {
        var command = KubeCommand.For(new NullConnection())
            .WithTenantId(id.TenantId)
            .WithResourceId(id)
            .WithKind(new() { Group = "apps", Version = "v1", Kind = "Deployment", Plural = "deployments" })
            .InNamespace("tenant-space")
            .WithApiVersion(apiVersion)
            .ObjectJson("""{"metadata":{"name":"main"}}""")
            .Build();

        return command.Labels;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    static IKubeCommandBuilder Builder(ResourceId id) =>
        KubeCommand.For(new NullConnection())
            .WithTenantId(id.TenantId)
            .WithResourceId(id)
            .WithKind(new() { Group = "apps", Version = "v1", Kind = "Deployment", Plural = "deployments" })
            .InNamespace("tenant-space")
            .ObjectJson("""{"metadata":{"name":"main"}}""");

    static KubeCommand BuildCommand(ResourceId id) => Builder(id).Build();
}

/// <summary>
///     A connection that is never called. These tests build commands; they do not send them.
/// </summary>
sealed class NullConnection : IKubeClusterConnection {
    public Guid ClusterId => Guid.Empty;

    public Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connection exists to be passed to KubeCommand.For, not used.");

    public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connection exists to be passed to KubeCommand.For, not used.");

    public Task<Result> DeleteAsync(
        KubeCommand command,
        CascadePolicy policy = CascadePolicy.Background,
        CancellationToken cancellationToken = default
    ) =>
        throw new NotSupportedException("This connection exists to be passed to KubeCommand.For, not used.");
}
