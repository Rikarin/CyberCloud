using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Contracts.Tests;

/// <summary>
///     api-versions are immutable dates, and a read at an old version projects down from a superset.
///     docs/plan/08 § The provider registry.
/// </summary>
public sealed class ApiVersionTests {
    [Theory]
    [InlineData("2026-08-01")]
    [InlineData("2026-12-31")]
    [InlineData("2027-01-01")]
    public void ADateParses(string value) {
        ApiVersion.TryParse(value, out var version).ShouldBeTrue();
        version.Value.ShouldBe(value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v1")]
    [InlineData("2026-8-1")]
    [InlineData("08/01/2026")]
    [InlineData(" 2026-08-01")]
    [InlineData("2026-08-01 ")]
    [InlineData("2026-08-01-preview")]
    [InlineData("2026-13-01")]
    [InlineData("2026-02-30")]
    public void AnythingElseIsRefused(string? value) {
        // ⚠ Strict on purpose. DateOnly.TryParse would accept four of these, which is four more
        // spellings of one version — and the registry keys on the string, so three of them miss.
        // "latest" is refused for the sharper reason: a client that says "latest" has asked to be
        // broken by the next release.
        ApiVersion.TryParse(value, out _).ShouldBeFalse();
    }

    [Fact]
    public void TheFailureNamesTheFormatAndSaysThereIsNoLatest() {
        var created = ApiVersion.Create("latest");

        created.IsFailure.ShouldBeTrue();
        created.Error!.Code.ShouldBe(ErrorCode.InvalidApiVersion);
        created.Error.Message.ShouldContain("yyyy-MM-dd");
        created.Error.Message.ShouldContain("no 'latest'");
    }

    [Fact]
    public void OrderingIsChronologicalAndLexicographicAtOnce() {
        // The registry sorts versions to find the newest. A format where sorting and chronology
        // disagreed would make "the newest schema" quietly wrong.
        var older = ApiVersion.Parse("2026-08-01");
        var newer = ApiVersion.Parse("2027-01-01");

        (older < newer).ShouldBeTrue();
        string.CompareOrdinal(older.Value, newer.Value).ShouldBeLessThan(0);
    }

    [Fact]
    public void DefaultIsEmptyAndIsNotAVersion() {
        default(ApiVersion).IsEmpty.ShouldBeTrue();
        default(ApiVersion).Value.ShouldBeEmpty();
    }
}

/// <summary>
///     Schema validation is step 2, and the same object is what an emitter would read.
/// </summary>
public sealed class ResourceSchemaTests {
    static ResourceSchema Sku2026 =>
        ResourceSchema.Of(
            [
                new("/location", SchemaKind.Text, Required: true),
                new("/properties", SchemaKind.Nested),
                new("/properties/version", SchemaKind.Text, Required: true),
                new("/properties/storageGb", SchemaKind.WholeNumber)
            ]
        );

    [Fact]
    public void AValidBodyPasses() {
        Validate(Sku2026, """{"location":"eu-central","properties":{"version":"17","storageGb":100}}""")
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void AMissingRequiredPropertyIsRefusedAndTheTargetIsItsPointer() {
        // docs/plan/08 § Errors: "target is a JSON Pointer into the request body so the portal can
        // highlight the field."
        var result = Validate(Sku2026, """{"location":"eu-central","properties":{}}""");

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        result.Error.Target.ShouldBe("/properties/version");
    }

    [Fact]
    public void AWrongTypeNamesBothTheExpectedAndTheActualKind() {
        // "message is for a human and names the actual numbers" — docs/plan/08 § Errors.
        var result = Validate(
            Sku2026,
            """{"location":"eu-central","properties":{"version":"17","storageGb":"lots"}}"""
        );

        result.IsFailure.ShouldBeTrue();
        result.Error!.Message.ShouldContain("integer");
        result.Error.Message.ShouldContain("string");
        result.Error.Target.ShouldBe("/properties/storageGb");
    }

    [Fact]
    public void AnUnknownPropertyIsRefusedRatherThanDropped() {
        // ⚠ The opposite of what most JSON Schemas do, and deliberately: silently dropping produces a
        // resource that is not what was asked for and reports success.
        var result = Validate(
            Sku2026,
            """{"location":"eu-central","properties":{"version":"17","storageGB":100}}"""
        );

        result.IsFailure.ShouldBeTrue();
        result.Error!.Target.ShouldBe("/properties/storageGB");
        result.Error.Message.ShouldContain("refused rather than dropped");
    }

    [Fact]
    public void EveryProblemIsReportedAndTheFirstIsTheTopLevelError() {
        // A portal form fixed one field per round trip is a form nobody finishes. The rest go in
        // Error.Details, which is what docs/plan/08 § Errors has that field for.
        var result = Validate(Sku2026, """{"properties":{"storageGb":"lots"}}""");

        result.IsFailure.ShouldBeTrue();
        result.Error!.Details.Length.ShouldBeGreaterThanOrEqualTo(1);

        var everything = new[] { result.Error }.Concat(result.Error.Details).Select(x => x.Target).ToArray();
        everything.ShouldContain("/location");
        everything.ShouldContain("/properties/version");
        everything.ShouldContain("/properties/storageGb");
    }

    [Fact]
    public void APatchDocumentValidatesWithoutItsRequiredProperties() {
        // ⚠ THE VERB ASYMMETRY. A merge patch omits everything it is not changing; the MERGED result
        // is what must satisfy requiredness, and the grain is where the merge happens.
        var asPut = Validate(Sku2026, """{"properties":{"storageGb":200}}""", requireRequired: true);
        var asPatch = Validate(Sku2026, """{"properties":{"storageGb":200}}""", requireRequired: false);

        asPut.IsFailure.ShouldBeTrue();
        asPatch.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void AReadOnlyPropertyIsRefusedRatherThanIgnored() {
        var schema = ResourceSchema.Of([new("/etag", SchemaKind.Text, ReadOnly: true)]);
        var result = Validate(schema, """{"etag":"abc"}""");

        result.IsFailure.ShouldBeTrue();
        result.Error!.Target.ShouldBe("/etag");
        result.Error.Message.ShouldContain("refused rather than ignored");
    }

    [Fact]
    public void ANonObjectBodyIsRefusedWithTheWholeDocumentAsTheTarget() {
        var result = Validate(Sku2026, "[1,2,3]");

        result.IsFailure.ShouldBeTrue();
        result.Error!.Target.ShouldBe("");
    }

    [Fact]
    public void ADuplicatePointerIsABuildFailure() {
        Should.Throw<ArgumentException>(
            () => ResourceSchema.Of([new("/a", SchemaKind.Text), new("/a", SchemaKind.Number)])
        );
    }

    [Fact]
    public void APropertyPointerMustBeginWithASlash() {
        Should.Throw<ArgumentException>(() => new SchemaProperty("properties/version", SchemaKind.Text));
        Should.Throw<ArgumentException>(() => new SchemaProperty("", SchemaKind.Text));
    }

    // ── The api-version projection — the other half of the immutable-date rule ──────────────────
    //
    // ⚠ THE PROJECTION IS NOT TESTED HERE, BECAUSE IT NO LONGER LIVES HERE. ResourceSchema used to
    // carry a Project(JsonObject) that only these tests ever called; the projection a real GET runs is
    // ResourceGrain.Project, over the pointer list ResourceManagerService hands down. Testing the
    // unreachable copy is how its secret drop came to look like a platform guarantee for as long as it
    // did. The three cases that were here — a read at an old version, a container not carrying its
    // undeclared members, and a Secret property withheld — are now
    // CyberCloud.ResourceManager.Tests.WritePathTests.AReadAtAnOldVersionKeepsGettingTheShapeItWasWrittenAgainst
    // and .ASecretPropertyIsNeverProjectedBackToTheCaller, against the code a request reaches.
    //
    // What this file still owns is the SCHEMA: what it declares, what it validates, and what it
    // refuses.
    //
    // ⚠ An all-Secret schema is NOT refused here, and that is deliberate: a schema does not know what
    // it is for. An action's response is legitimately all-secret — `listKeys` — while a resource body
    // that shape would project its whole superset on a read. ProviderBuilder.ApiVersion is where a
    // schema becomes a body and is where the refusal lives;
    // CyberCloud.ResourceManager.Tests.RegistryDeclarationTests asserts it.

    [Fact]
    public void DeclaresIsExactAndIsNotAPrefixMatch() {
        Sku2026.Declares("/properties/version").ShouldBeTrue();
        Sku2026.Declares("/properties/versions").ShouldBeFalse();
        Sku2026.Declares("/propertie").ShouldBeFalse();
    }

    [Fact]
    public void SchemaPropertyNameIsTheLastSegmentUnescaped() {
        new SchemaProperty("/properties/sku~1name", SchemaKind.Text).Name.ShouldBe("sku/name");
        new SchemaProperty("/properties/version", SchemaKind.Text).Name.ShouldBe("version");
    }

    static Result Validate(ResourceSchema schema, string json, bool requireRequired = true) {
        using var document = JsonDocument.Parse(json);
        return schema.Validate(document.RootElement, requireRequired);
    }
}

/// <summary>Everything the write path records about which steps ran, and in which order.</summary>
public sealed class WriteTraceTests {
    [Fact]
    public void TheCanonicalOrderIsTheDocumentsTwelveSteps() {
        // ⚠ Pinned as a literal so that reordering the enum does not quietly reorder the assertion.
        WriteTrace.Canonical.ShouldBe(
            [
                WriteStep.ResolveRegistration,
                WriteStep.ValidateBody,
                WriteStep.AuthorizationCheck,
                WriteStep.Locks,
                WriteStep.Policy,
                WriteStep.Quota,
                WriteStep.IndexClaim,
                WriteStep.LinkParent,
                WriteStep.SubmitDesired,
                WriteStep.StartOperation,
                WriteStep.EmitChanged,
                WriteStep.Accepted
            ]
        );

        WriteTrace.Canonical.Length.ShouldBe(12);
    }

    [Fact]
    public void TheCheckIsStepThreeAndQuotaIsStepSixAndTheClaimIsStepSeven() {
        // ⚠ THE SECURITY PROPERTY, AS AN ARITHMETIC FACT. docs/plan/08 § The write path, end to end:
        // "A provider that could skip step 3 is a provider that eventually will." These three ordinals
        // are what the write path's trace is checked against.
        ((int)WriteStep.AuthorizationCheck).ShouldBe(3);
        ((int)WriteStep.Quota).ShouldBe(6);
        ((int)WriteStep.IndexClaim).ShouldBe(7);

        // ⚠ AND THE PARENT EDGE IS STEP 8, WHICH IS BEFORE THE DURABLE WRITE AND AFTER THE CLAIM.
        // Both bounds are the decision: after the durable write there is a window in which a resource
        // exists and its own creator cannot read it, and before the claim a lost name race would leave
        // a tuple for a resource that never existed.
        ((int)WriteStep.LinkParent).ShouldBe(8);

        (WriteStep.AuthorizationCheck < WriteStep.Quota).ShouldBeTrue();
        (WriteStep.Quota < WriteStep.IndexClaim).ShouldBeTrue();
        (WriteStep.IndexClaim < WriteStep.LinkParent).ShouldBeTrue();
        (WriteStep.LinkParent < WriteStep.SubmitDesired).ShouldBeTrue();
    }

    [Fact]
    public void APrefixOfTheCanonicalOrderIsCanonical() {
        new WriteTrace { Reached = [WriteStep.ResolveRegistration, WriteStep.ValidateBody] }
            .IsCanonicalPrefix()
            .ShouldBeTrue();
    }

    [Fact]
    public void ATraceThatSkippedAStepIsNotCanonical() {
        new WriteTrace { Reached = [WriteStep.ResolveRegistration, WriteStep.AuthorizationCheck] }
            .IsCanonicalPrefix()
            .ShouldBeFalse();
    }

    [Fact]
    public void ATraceThatReorderedTwoStepsIsNotCanonical() {
        new WriteTrace { Reached = [WriteStep.ValidateBody, WriteStep.ResolveRegistration] }
            .IsCanonicalPrefix()
            .ShouldBeFalse();
    }

    [Fact]
    public void WriteStepZeroIsNotAStep() {
        // A default value must not name a real step, or an unrecorded trace would read as "step 1 ran".
        ((int)WriteStep.None).ShouldBe(0);
        WriteTrace.Canonical.ShouldNotContain(WriteStep.None);
        new WriteTrace().StoppedAt.ShouldBe(WriteStep.None);
    }
}

/// <summary>The three cases, and the fact that there is no fourth.</summary>
public sealed class ReconcileOutcomeTests {
    [Fact]
    public void ThereAreExactlyThreeKindsAndNoUnknown() {
        // docs/plan/08 § The reconcile loop: "Converged, InProgress(reason, retryAfter),
        // Failed(error, retryable). Nothing else."
        Enum.GetValues<ReconcileOutcomeKind>()
            .ShouldBe(
                [
                    ReconcileOutcomeKind.Converged,
                    ReconcileOutcomeKind.InProgress,
                    ReconcileOutcomeKind.Failed
                ],
                ignoreOrder: true
            );
    }

    [Fact]
    public void TheOnlyWaysToBuildOneAreTheThreeFactories() {
        // ⚠ Closed by construction rather than by convention: every constructor is private, so a
        // fourth case cannot be added from outside and a partially-filled outcome cannot exist.
        typeof(ReconcileOutcome)
            .GetConstructors()
            .ShouldBeEmpty();
    }

    [Fact]
    public void ConvergedIsCachedAndCarriesNothing() {
        ReconcileOutcome.Converged.Kind.ShouldBe(ReconcileOutcomeKind.Converged);
        ReconcileOutcome.Converged.Reason.ShouldBeEmpty();
        ReconcileOutcome.Converged.Error.ShouldBeNull();
        ReconcileOutcome.Converged.IsConverged.ShouldBeTrue();
        ReconcileOutcome.Converged.ShouldBeSameAs(ReconcileOutcome.Converged);
    }

    [Fact]
    public void InProgressCarriesAReasonAndARetryAfter() {
        var outcome = ReconcileOutcome.InProgress("2 of 3 replicas ready", TimeSpan.FromSeconds(15));

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldBe("2 of 3 replicas ready");
        outcome.RetryAfter.ShouldBe(TimeSpan.FromSeconds(15));
        outcome.IsTerminal.ShouldBeFalse();
    }

    [Fact]
    public void AnInProgressWithNoReasonIsRefused() {
        // A reconciler that wants to say nothing is a spinner, which is the thing the progress model
        // exists to replace.
        Should.Throw<ArgumentException>(() => ReconcileOutcome.InProgress("  "));
    }

    [Fact]
    public void ANegativeRetryAfterIsRefusedRatherThanClamped() {
        Should.Throw<ArgumentOutOfRangeException>(
            () => ReconcileOutcome.InProgress("waiting", TimeSpan.FromSeconds(-1))
        );
    }

    [Fact]
    public void FailedCarriesTheErrorAndWhetherRetryingCouldHelp() {
        var outcome = ReconcileOutcome.Failed(ErrorCode.ProvisioningFailed, "the manifest was rejected");

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Code.ShouldBe(ErrorCode.ProvisioningFailed);
        outcome.Retryable.ShouldBeFalse();
        outcome.IsTerminal.ShouldBeTrue();

        ReconcileOutcome.Failed(ErrorCode.ProvisioningFailed, "the api server timed out", retryable: true)
            .IsTerminal.ShouldBeFalse();
    }

    [Theory]
    [InlineData(nameof(ErrorCode.PolicyViolation))]
    [InlineData(nameof(ErrorCode.InvalidRequestBody))]
    [InlineData(nameof(ErrorCode.InvalidResourceType))]
    [InlineData(nameof(ErrorCode.AuthorizationFailed))]
    public void TheFourRefusalsAreTerminal(string code) {
        // ⚠ These four are the whole list, and it is one list because it used to be none: every
        // reconciler passed `retryable: true` for every failed apply, so an admission rejection was
        // retried on the 10s/30s/2min/10min ladder for sixty minutes and then reported as an
        // OperationTimeout — throwing away the policy's own message, which arrived on the first pass.
        ErrorCode.TryFromValue(code, out var refusal).ShouldBeTrue();

        ReconcileOutcome.IsRetryable(refusal).ShouldBeFalse();

        var outcome = ReconcileOutcome.FromFailure(new Error(refusal, "the cluster said no"));

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Retryable.ShouldBeFalse();
        outcome.IsTerminal.ShouldBeTrue();
        outcome.Error!.Code.ShouldBe(refusal);
    }

    [Theory]
    [InlineData(nameof(ErrorCode.InternalError))]
    [InlineData(nameof(ErrorCode.ResourceNotFound))]
    [InlineData(nameof(ErrorCode.Conflict))]
    [InlineData(nameof(ErrorCode.PreconditionFailed))]
    public void EverythingElseStaysRetryable(string code) {
        // ⚠ InternalError is the one that matters: it is what "the cluster did not answer" arrives as,
        // and ending an operation on a dropped connection is the mirror-image bug. The other three are
        // states a later pass really does find changed — an object appears, another writer lets a field
        // go, a resourceVersion moves on — which is why KubeFailures.MeansTheClusterAnswered listing
        // them is not a reason to list them here. That predicate answers a different question.
        ErrorCode.TryFromValue(code, out var transient).ShouldBeTrue();

        ReconcileOutcome.IsRetryable(transient).ShouldBeTrue();
        ReconcileOutcome.FromFailure(new Error(transient, "not now")).IsTerminal.ShouldBeFalse();
    }

    [Fact]
    public void FromFailureRefusesANullError() {
        Should.Throw<ArgumentNullException>(() => ReconcileOutcome.FromFailure(null!));
    }
}

/// <summary>The one error shape, Azure's. docs/plan/08 § Errors.</summary>
public sealed class ErrorShapeTests {
    [Fact]
    public void TheShapeIsCodeMessageTargetDetails() {
        // { "error": { "code": …, "message": …, "target": …, "details": [ … ] } }
        var detail = new Error(ErrorCode.InvalidRequestBody, "'/properties/sku' is required and is missing.", "/properties/sku");
        var error = new Error(
            ErrorCode.QuotaExceeded,
            "Subscription quota for 'vcpu' in region 'eu-central' would be exceeded (requested 8, available 2).",
            "/properties/sku",
            [detail]
        );

        error.Code.Value.ShouldBe("QuotaExceeded");
        error.Message.ShouldContain("requested 8");
        error.Message.ShouldContain("available 2");
        error.Target.ShouldBe("/properties/sku");
        error.Details.Length.ShouldBe(1);
    }

    [Fact]
    public void ATargetMustBeAJsonPointer() {
        Should.Throw<ArgumentException>(() => new Error(ErrorCode.InvalidRequestBody, "x", "properties.sku"));
    }

    [Fact]
    public void EveryCodeTheResourceManagerReturnsIsInTheCheckedInRegistry() {
        // docs/plan/08 § Errors: "code is a stable, documented, greppable identifier. It is part of the
        // API contract; changing one is a breaking change. There is a checked-in registry."
        ImmutableArray<ErrorCode> used = [
            ErrorCode.ResourceNotFound,
            ErrorCode.AuthorizationFailed,
            ErrorCode.InvalidRequestBody,
            ErrorCode.InvalidApiVersion,
            ErrorCode.InvalidResourceType,
            ErrorCode.InvalidResourceId,
            ErrorCode.ResourceAlreadyExists,
            ErrorCode.QuotaExceeded,
            ErrorCode.ScopeLocked,
            ErrorCode.PolicyViolation,
            ErrorCode.PreconditionFailed,
            ErrorCode.OperationInProgress,
            ErrorCode.OperationTimeout,
            ErrorCode.OperationCanceled,
            ErrorCode.ProvisioningFailed,
            ErrorCode.Conflict,
            ErrorCode.InternalError
        ];

        foreach (var code in used) {
            ErrorCode.All.ShouldContain(code, $"{code.Value} must be in the checked-in registry.");
            ErrorCode.TryFromValue(code.Value, out var round).ShouldBeTrue();
            round.ShouldBeSameAs(code);
        }
    }
}
