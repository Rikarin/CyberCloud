using CyberCloud.Core.Resources;
using Shouldly;
using System.Collections.Immutable;

namespace CyberCloud.Core.Contracts.Tests;

/// <summary>
///     <see cref="Result" /> and <see cref="Result{T}" /> through Orleans' own serializer.
/// </summary>
/// <remarks>
///     The severity ordering of this file is deliberate: the <c>default</c> and failure cases come
///     first, because "a failed grain call reads as success" is the worst outcome available in this
///     assembly and the one a happy-path round-trip test would never find.
/// </remarks>
public sealed class ResultSerializationTests(OrleansSerializerFixture orleans)
    : IClassFixture<OrleansSerializerFixture> {
    static readonly ResourceTypeName PostgresServers =
        new("CyberCloud.DBforPostgreSQL", "servers");

    // ── The default-value trap ─────────────────────────────────────────────────────────────────

    [Fact]
    public void DefaultResultSurvivesAsAFailure() {
        // default(Result) is deliberately a failure in Core. The wire must not launder it.
        var round = orleans.RoundTrip(default(Result));

        round.IsFailure.ShouldBeTrue();
        round.IsSuccess.ShouldBeFalse();
        round.Error.ShouldNotBeNull();
        round.Error.Code.ShouldBe(ErrorCode.InternalError);
    }

    [Fact]
    public void DefaultResultKeepsItsErrorTextSoTheCauseIsNotLost() {
        // Core substitutes its Uninitialized error in the property getter, so what goes on the wire
        // is that error's text — not a placeholder invented by the surrogate. Asserting the text
        // survives is what distinguishes "still a failure" from "still a failure, and still says
        // why".
        orleans.RoundTrip(default(Result)).Error!.ShouldBe(default(Result).Error);
    }

    [Fact]
    public void DefaultResultIsNotStructurallyEqualToItsRoundTripAndThatIsExpected() {
        // ⚠ The one place the round trip is not an identity, recorded so nobody "fixes" it.
        //
        // Result is a record struct, so its synthesised equality compares the BACKING FIELD behind
        // the Error property, not the property. default(Result) has a null backing field; the
        // reconstructed value has the Uninitialized error the getter substituted. Observably they
        // are the same failure with the same error — asserted above — but `==` says otherwise.
        //
        // This is a property of Core's Result, not of the surrogate: `default(Result)` and
        // `Result.Failure(default(Result).Error!)` are already unequal without any serialization
        // involved. Asserting it here is what keeps somebody from adding a
        // `roundTrip.ShouldBe(original)` line to this file and then "fixing" the surrogate to make
        // it pass, which would mean reintroducing a null error.
        var round = orleans.RoundTrip(default(Result));

        round.ShouldNotBe(default);
        round.ShouldBe(Result.Failure(default(Result).Error!));
        round.IsFailure.ShouldBe(default(Result).IsFailure);
        round.Error.ShouldBe(default(Result).Error);
    }

    [Fact]
    public void DefaultGenericResultOverAValueTypeSurvivesAsAFailure() {
        // The nastiest shape: T is a value type, so a null-based "did it work" check would read the
        // zero as an answer. Result<int> failure must stay a failure and must not hand back 0.
        var round = orleans.RoundTrip(default(Result<int>));

        round.IsFailure.ShouldBeTrue();
        round.TryGetValue(out _).ShouldBeFalse();
        round.Error.ShouldNotBeNull();
    }

    [Fact]
    public void DefaultGenericResultOverAValidatingStructSurvivesAsAFailure() {
        // Result<ResourceId> writes default(ResourceId) into the value slot of a failure, and
        // ResourceId's constructor throws on the null name that implies. If ResourceIdSurrogate did
        // not special-case default, this test would throw rather than fail — which is why it is
        // separate from the Result<int> one.
        var round = orleans.RoundTrip(default(Result<ResourceId>));

        round.IsFailure.ShouldBeTrue();
        round.TryGetValue(out _).ShouldBeFalse();
    }

    // ── Failures ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AFailedResultRoundTripsWithItsWholeError() {
        var original = Result.Failure(
            ErrorCode.QuotaExceeded,
            "Subscription quota for 'vcpu' in region 'eu-central' would be exceeded "
            + "(requested 8, available 2).",
            "/properties/sku"
        );

        var round = orleans.RoundTrip(original);

        round.IsFailure.ShouldBeTrue();
        round.Error.ShouldBe(original.Error);
        round.Error!.Code.ShouldBeSameAs(ErrorCode.QuotaExceeded);
        round.Error.Target.ShouldBe("/properties/sku");
    }

    [Fact]
    public void AFailedGenericResultRoundTripsWithItsWholeError() {
        var original = Result<ResourceId>.Failure(
            ErrorCode.ResourceNotFound,
            "No resource with that path exists in this subscription."
        );

        var round = orleans.RoundTrip(original);

        round.IsFailure.ShouldBeTrue();
        round.Error.ShouldBe(original.Error);
        round.TryGetValue(out _).ShouldBeFalse();
    }

    [Fact]
    public void AFailedGenericResultIsEqualToItself() {
        // Both sides hold default(T) in the value slot, so record-struct equality is exact here.
        var original = Result<ResourceId>.Failure(
            ErrorCode.ResourceNotFound,
            "No resource with that path exists in this subscription."
        );

        orleans.RoundTrip(original).ShouldBe(original);
    }

    [Fact]
    public void NestedErrorDetailsRoundTrip() {
        var original = Result.Failure(
            new(
                ErrorCode.InvalidRequestBody,
                "The request body failed the type's JSON Schema.",
                "",
                [
                    new(ErrorCode.InvalidRequestBody, "'sku' is required.", "/properties/sku"),
                    new(
                        ErrorCode.InvalidRequestBody,
                        "'version' must be one of 15, 16, 17.",
                        "/properties/version",
                        [new(ErrorCode.InvalidRequestBody, "got '9.6'.", "/properties/version")]
                    )
                ]
            )
        );

        var round = orleans.RoundTrip(original);

        round.Error.ShouldBe(original.Error);
        round.Error!.Details.Length.ShouldBe(2);
        round.Error.Details[1].Details.Length.ShouldBe(1);
    }

    // ── Successes ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SuccessRoundTripsAndIsStillSuccess() {
        var round = orleans.RoundTrip(Result.Success);

        round.IsSuccess.ShouldBeTrue();
        round.Error.ShouldBeNull();
        round.ShouldBe(Result.Success);
    }

    [Fact]
    public void AGenericResultHoldingAResourceIdRoundTrips() {
        var original = Result<ResourceId>.Success(
            new(
                Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"),
                Guid.Parse("6f0f1f0e-1234-4c8b-9a3d-aabbccddeeff"),
                "prod",
                PostgresServers,
                "orders-db",
                Guid.Parse("11112222-3333-4444-5555-666677778888")
            )
        );

        var round = orleans.RoundTrip(original);

        round.IsSuccess.ShouldBeTrue();
        round.ShouldBe(original);
        round.GetValueOrThrow().Path.ShouldBe(original.GetValueOrThrow().Path);
    }

    [Fact]
    public void AGenericResultOverAPrimitiveRoundTrips() {
        // The open generic converter has to instantiate for a T this assembly never names.
        orleans.RoundTrip(Result<int>.Success(42)).ShouldBe(Result<int>.Success(42));
        orleans.RoundTrip(Result<string>.Success("ok")).ShouldBe(Result<string>.Success("ok"));
    }

    [Fact]
    public void AGenericResultOverACollectionRoundTrips() {
        // A T that is itself generic — the case that would fail if the converter were closed over a
        // fixed set of types rather than open.
        var original = Result<ImmutableArray<string>>.Success(["eu-central", "us-east"]);
        var round = orleans.RoundTrip(original);

        round.IsSuccess.ShouldBeTrue();
        round.GetValueOrThrow().ShouldBe(original.GetValueOrThrow());
    }

    [Fact]
    public void ANestedResultRoundTrips() {
        // Result<Result<T>> is not a shape anybody should write, but it is the shape that proves the
        // open generic converter recurses through itself rather than through a special case.
        var inner = Result<int>.Failure(ErrorCode.Conflict, "lost a race");
        var round = orleans.RoundTrip(Result<Result<int>>.Success(inner));

        round.IsSuccess.ShouldBeTrue();
        round.GetValueOrThrow().IsFailure.ShouldBeTrue();
        round.GetValueOrThrow().Error!.Code.ShouldBe(ErrorCode.Conflict);
    }
}
