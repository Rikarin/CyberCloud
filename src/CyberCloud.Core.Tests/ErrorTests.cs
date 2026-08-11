using System.Collections.Immutable;
using Shouldly;

namespace CyberCloud.Core.Tests;

/// <summary><see cref="Error" /> — the one error shape, docs/plan/08 § Errors.</summary>
public class ErrorTests
{
    [Fact]
    public void TheWorkedExampleFromTheDocumentIsRepresentable()
    {
        var error = new Error(
            ErrorCode.QuotaExceeded,
            "Subscription quota for 'vcpu' in region 'eu-central' would be exceeded "
            + "(requested 8, available 2).",
            "/properties/sku");

        error.Code.Value.ShouldBe("QuotaExceeded");
        error.Target.ShouldBe("/properties/sku");
        error.Details.ShouldBeEmpty();
        error.Message.ShouldContain("requested 8, available 2");
    }

    [Fact]
    public void DetailsDefaultToEmptyRatherThanToADefaultImmutableArray()
    {
        // A default ImmutableArray<T> throws on almost every member. Normalising it at the door
        // means no consumer has to remember IsDefaultOrEmpty.
        var error = new Error(ErrorCode.InternalError, "x");

        error.Details.IsDefault.ShouldBeFalse();
        error.Details.ShouldBeEmpty();
        Should.NotThrow(() => error.Details.Length);
    }

    [Fact]
    public void DetailsNest()
    {
        var child = new Error(ErrorCode.InvalidResourceName, "bad name");
        var parent = new Error(ErrorCode.InvalidRequestBody, "body is wrong", null, [child]);

        parent.Details.Length.ShouldBe(1);
        parent.Details[0].ShouldBe(child);
        parent.WithDetails(child).Details.Length.ShouldBe(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/properties")]
    [InlineData("/properties/sku")]
    [InlineData("/properties/0/name")]
    [InlineData("/a~0b")]
    [InlineData("/a~1b")]
    public void AValidJsonPointerIsAccepted(string target) =>
        new Error(ErrorCode.InternalError, "x", target).Target.ShouldBe(target);

    [Theory]
    [InlineData("properties.sku")]
    [InlineData("properties/sku")]
    [InlineData("/a~b")]
    [InlineData("/a~")]
    [InlineData("~")]
    public void AnInvalidJsonPointerThrows(string target) =>
        Should.Throw<ArgumentException>(() => new Error(ErrorCode.InternalError, "x", target));

    [Fact]
    public void ANullTargetIsFineBecauseNotEveryErrorPointsAtAField() =>
        new Error(ErrorCode.InternalError, "x").Target.ShouldBeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankMessageThrowsBecauseAnErrorWithoutOneIsUseless(string? message) =>
        Should.Throw<ArgumentException>(() => new Error(ErrorCode.InternalError, message!));

    [Fact]
    public void ANullCodeThrows() =>
        Should.Throw<ArgumentNullException>(() => new Error(null!, "x"));

    [Fact]
    public void ThereIsNoPlaceToPutAStackTrace()
    {
        // docs/plan/08:190 — "No exception details, ever." Asserted structurally rather than by
        // review: if someone adds an Exception-shaped member, this fails.
        var members = typeof(Error)
            .GetProperties()
            .Select(x => x.Name)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();

        members.ShouldBe(["Code", "Details", "Message", "Target"]);

        typeof(Error).GetProperties()
            .ShouldAllBe(x => !typeof(Exception).IsAssignableFrom(x.PropertyType));
    }

    [Fact]
    public void EqualityComparesDetailsByContentNotByArrayReference()
    {
        var a = new Error(ErrorCode.Conflict, "x", "/a", [new Error(ErrorCode.InternalError, "d")]);
        var b = new Error(ErrorCode.Conflict, "x", "/a", [new Error(ErrorCode.InternalError, "d")]);

        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());

        a.ShouldNotBe(new Error(ErrorCode.Conflict, "x", "/a"));
    }

    [Fact]
    public void WithTargetKeepsEverythingElse()
    {
        var error = new Error(ErrorCode.Conflict, "x", "/a");

        error.WithTarget("/b").ShouldBe(new Error(ErrorCode.Conflict, "x", "/b"));
        error.WithTarget(null).Target.ShouldBeNull();
    }

    [Fact]
    public void ToStringIsUsefulInAnAssertionMessage()
    {
        new Error(ErrorCode.Conflict, "taken").ToString().ShouldBe("Conflict: taken");
        new Error(ErrorCode.Conflict, "taken", "/a").ToString().ShouldBe("Conflict (/a): taken");
    }
}
