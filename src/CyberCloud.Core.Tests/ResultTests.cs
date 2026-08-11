using CyberCloud.Core.Resources;
using Shouldly;

namespace CyberCloud.Core.Tests;

/// <summary><see cref="Result" /> and <see cref="Result{T}" /> — docs/plan/00 § Coding standards.</summary>
public class ResultTests {
    [Fact]
    public void SuccessIsSuccessful() {
        Result.Success.IsSuccess.ShouldBeTrue();
        Result.Success.IsFailure.ShouldBeFalse();
        Result.Success.Error.ShouldBeNull();
        Result.Success.TryGetError(out _).ShouldBeFalse();
    }

    [Fact]
    public void FailureCarriesTheError() {
        var result = Result.Failure(ErrorCode.QuotaExceeded, "no vcpu left", "/properties/sku");

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCode.QuotaExceeded);
        result.Error.Target.ShouldBe("/properties/sku");
        result.TryGetError(out var error).ShouldBeTrue();
        error.Message.ShouldBe("no vcpu left");
    }

    // ── The default hazard ─────────────────────────────────────────────────────────────────────
    //
    // A value type cannot forbid its own default, so the default is aimed at the safe side. If
    // default were success, a field nobody assigned would read as "the operation succeeded".

    [Fact]
    public void DefaultResultIsAFailureNotASuccess() {
        var result = default(Result);

        result.IsSuccess.ShouldBeFalse();
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldNotBeNull();
        result.Error!.Code.ShouldBe(ErrorCode.InternalError);
        result.Error.Message.ShouldContain("read before it was assigned");
    }

    [Fact]
    public void DefaultGenericResultIsAFailureNotASuccess() {
        var result = default(Result<string>);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCode.InternalError);
        result.ValueOrDefault.ShouldBeNull();
        result.TryGetValue(out _).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => result.GetValueOrThrow());
    }

    [Fact]
    public void DefaultGenericResultOverAValueTypeIsAlsoAFailure() {
        // The subtle one: T = int means `default(T)` is 0, not null, so a naive implementation
        // would report "succeeded, and the answer is 0".
        var result = default(Result<int>);

        result.IsFailure.ShouldBeTrue();
        result.TryGetValue(out _).ShouldBeFalse();
        result.ValueOrDefault.ShouldBe(0);
        Should.Throw<InvalidOperationException>(() => result.GetValueOrThrow());
    }

    // ── Result<T> ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GenericSuccessCarriesTheValue() {
        var result = Result<int>.Success(42);

        result.IsSuccess.ShouldBeTrue();
        result.TryGetValue(out var value).ShouldBeTrue();
        value.ShouldBe(42);
        result.GetValueOrThrow().ShouldBe(42);
        result.Error.ShouldBeNull();
    }

    [Fact]
    public void GenericFailureCarriesTheError() {
        var result = Result<int>.Failure(ErrorCode.ResourceNotFound, "no such thing");

        result.IsFailure.ShouldBeTrue();
        result.TryGetValue(out _).ShouldBeFalse();
        result.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public void ImplicitConversionsLiftValuesAndErrors() {
        Result<int> fromValue = 7;
        Result<int> fromError = new Error(ErrorCode.Conflict, "taken");

        fromValue.GetValueOrThrow().ShouldBe(7);
        fromError.Error!.Code.ShouldBe(ErrorCode.Conflict);

        Result<int>.FromValue(7).ShouldBe(fromValue);
        Result<int>.FromError(new(ErrorCode.Conflict, "taken")).ShouldBe(fromError);
    }

    [Fact]
    public void ToResultDiscardsTheValueAndKeepsTheOutcome() {
        Result<int>.Success(1).ToResult().IsSuccess.ShouldBeTrue();

        var failed = Result<int>.Failure(ErrorCode.ScopeLocked, "locked").ToResult();
        failed.IsFailure.ShouldBeTrue();
        failed.Error!.Code.ShouldBe(ErrorCode.ScopeLocked);
    }

    [Fact]
    public void NullArgumentsAreProgrammerErrorsAndThrow() {
        Should.Throw<ArgumentNullException>(() => Result.Failure(null!));
        Should.Throw<ArgumentNullException>(() => Result<string>.Failure(null!));
        Should.Throw<ArgumentNullException>(() => Result<string>.Success(null!));
    }

    [Fact]
    public void ResultsAreValuesAndCompareByContent() {
        var a = Result<ResourceTypeName>.Success(new("CyberCloud.Data", "servers"));
        var b = Result<ResourceTypeName>.Success(new("cybercloud.data", "SERVERS"));

        a.ShouldBe(b);

        Result.Failure(ErrorCode.Conflict, "x").ShouldBe(Result.Failure(ErrorCode.Conflict, "x"));
        Result.Failure(ErrorCode.Conflict, "x").ShouldNotBe(Result.Failure(ErrorCode.Conflict, "y"));
    }

    [Fact]
    public void ToStringIsUsefulInAnAssertionMessage() {
        Result.Success.ToString().ShouldBe("Success");
        Result.Failure(ErrorCode.Conflict, "taken")
            .ToString()
            .ShouldBe("Failure(Conflict: taken)");
        Result<int>.Success(3).ToString().ShouldBe("Success(3)");
    }
}
