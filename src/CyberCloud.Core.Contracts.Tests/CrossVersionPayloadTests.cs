using CyberCloud.Core.Contracts.Serialization;
using Shouldly;

namespace CyberCloud.Core.Contracts.Tests;

/// <summary>
///     The payloads a rolling upgrade produces and a single-version test suite never sees.
/// </summary>
/// <remarks>
///     <para>
///         These go through the converters directly rather than through
///         <see cref="OrleansSerializerFixture" />, because the point is to hand the converter a
///         surrogate that <i>this</i> build cannot produce — a member a peer did not write, an
///         error code only a newer silo knows. Orleans hands the converter exactly this shape when
///         a field is absent from the payload, so constructing it by hand is the same input, not an
///         approximation of it.
///     </para>
///     <para>
///         docs/plan/04:179-181 asks for the real version of this: a CI gate that loads the previous
///         three releases' contract assemblies and round-trips every wire type through both. That
///         gate cannot exist until there is a previous release. This file is the part of it that can
///         be written on day one.
///     </para>
/// </remarks>
public sealed class CrossVersionPayloadTests {
    [Fact]
    public void AnUnwrittenResultSurrogateIsAFailure() {
        // The shape Orleans produces when a peer wrote no fields at all. If [Id(0)] were an
        // IsFailure flag instead of IsSuccess, this same code would read it as a success.
        var round = new ResultSurrogateConverter().ConvertFromSurrogate(default);

        round.IsFailure.ShouldBeTrue();
        round.Error.ShouldNotBeNull();
        round.Error.Code.ShouldBe(ErrorCode.InternalError);
    }

    [Fact]
    public void AnUnwrittenGenericResultSurrogateIsAFailure() {
        var round = new ResultSurrogateConverter<int>().ConvertFromSurrogate(default);

        round.IsFailure.ShouldBeTrue();
        round.TryGetValue(out _).ShouldBeFalse();
    }

    [Fact]
    public void AFailureWithNoErrorIsStillAFailure() {
        var round = new ResultSurrogateConverter()
            .ConvertFromSurrogate(new() { IsSuccess = false, Error = null });

        round.IsFailure.ShouldBeTrue();
        round.Error!.Message.ShouldContain("no error attached");
    }

    [Fact]
    public void ASuccessWithNoValueIsDowngradedToAFailureRatherThanThrowing() {
        // Result<T> is constrained T : notnull, so Result<T>.Success(null) throws. A peer that
        // claims success and sends nothing must not take the silo's thread with it.
        var round = new ResultSurrogateConverter<string>()
            .ConvertFromSurrogate(new() { IsSuccess = true, Value = null });

        round.IsFailure.ShouldBeTrue();
        round.Error!.Code.ShouldBe(ErrorCode.InternalError);
    }

    [Fact]
    public void AnUnknownErrorCodeBecomesInternalErrorAndKeepsItsToken() {
        // ErrorCode is a closed registry with a private constructor, so a code from a newer silo
        // cannot be manufactured. Dropping it silently would make an upgrade read as an outage in
        // the logs; preserving it in the message keeps the same grep working.
        var round = new ErrorSurrogateConverter().ConvertFromSurrogate(
            new() {
                Code = "SomethingVersionNPlusOneKnows",
                Message = "the quota meter 'gpu' is not provisioned in this region."
            }
        );

        round.Code.ShouldBe(ErrorCode.InternalError);
        round.Message.ShouldContain("SomethingVersionNPlusOneKnows");
        round.Message.ShouldContain("the quota meter 'gpu' is not provisioned in this region.");
    }

    [Fact]
    public void AnErrorWithNoMessageGetsOneRatherThanThrowing() {
        // Core's Error constructor rejects a blank message, so the value has to come from somewhere.
        var round = new ErrorSurrogateConverter().ConvertFromSurrogate(new() { Code = ErrorCode.Conflict.Value });

        round.Code.ShouldBe(ErrorCode.Conflict);
        round.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AnErrorWithNoDetailsGetsAnEmptyArrayNotADefaultOne() {
        // ImmutableArray<T>.IsDefault is the trap here: a default array throws on enumeration, and
        // Core's Error normalises it on construction. Assert the normalisation survives the wire.
        var round = new ErrorSurrogateConverter().ConvertFromSurrogate(
            new() { Code = ErrorCode.Conflict.Value, Message = "x", Details = null }
        );

        round.Details.IsDefault.ShouldBeFalse();
        round.Details.Length.ShouldBe(0);
    }

    [Fact]
    public void AMalformedResourceTypeNameThrowsRatherThanBeingAccepted() {
        // The one deliberate exception to "substitute, do not throw": a type path whose segments
        // are not what the grammar allows shifts the type/name boundary in ResourceId.TryParsePath,
        // so the receiver would treat a value the sender did not write as an address.
        Should.Throw<ArgumentException>(() =>
            new ResourceTypeNameSurrogateConverter().ConvertFromSurrogate(
                new() { Namespace = "NoDotHere", Type = "servers" }
            )
        );

        Should.Throw<ArgumentException>(() =>
            new ResourceTypeNameSurrogateConverter().ConvertFromSurrogate(
                new() { Namespace = "CyberCloud.Compute", Type = "a/b/c/d/e" }
            )
        );
    }

    [Fact]
    public void AHalfWrittenResourceIdIsTheEmptyIdRatherThanAThrow() {
        // A peer that wrote the GUIDs and not the names. There is no valid ResourceId to build, and
        // default(ResourceId) is what Core already uses for "no address".
        var round = new ResourceIdSurrogateConverter().ConvertFromSurrogate(
            new() { TenantId = Guid.NewGuid(), SubscriptionId = Guid.NewGuid() }
        );

        round.ShouldBe(default);
    }

    [Fact]
    public void AMalformedResourceGroupNameInAResourceIdThrows() {
        // The complement of the test above: the payload is complete but the value breaks the naming
        // rules, which is the separator-injection defence ResourceId's remarks describe. Accepting
        // it would produce a ResourceId whose Path re-parses as a different id.
        Should.Throw<ArgumentException>(() =>
            new ResourceIdSurrogateConverter().ConvertFromSurrogate(
                new() {
                    ResourceGroup = "prod/../other", Type = new("CyberCloud.Compute", "virtualMachines"), Name = "vm1"
                }
            )
        );
    }
}
