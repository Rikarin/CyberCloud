using System.Diagnostics.CodeAnalysis;

namespace CyberCloud.Core;

/// <summary>
///     The outcome of an operation that produces no value.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/00 § Coding standards:
///         <i>
///             "Every grain interface method returns <c>Task&lt;Result&lt;T&gt;&gt;</c>
///             or <c>Task&lt;Result&gt;</c>, never throws for a domain outcome. Exceptions are for bugs and
///             infrastructure. The gateway maps <c>Result</c> to an Azure-shaped error body; a thrown
///             exception maps to <c>500</c> and pages someone."
///         </i>
///     </para>
///     <para>
///         ⚠ <b><c>default(Result)</c> is a failure, not a success.</b> A value type cannot forbid
///         its own default, so the default is aimed at the safe side: an uninitialised
///         <see cref="Result" /> reports <see cref="IsFailure" /> with
///         <see cref="ErrorCode.InternalError" />. The alternative — <c>default</c> meaning success
///         — turns "a field nobody assigned" into "the operation succeeded", which is the one
///         failure mode a result type must not have. <see cref="Success" /> is the way to say
///         success and it is a cached instance, so this costs nothing.
///     </para>
/// </remarks>
public readonly record struct Result {
    internal static readonly Error Uninitialized = new(
        ErrorCode.InternalError,
        "A Result was read before it was assigned. This is a bug: default(Result) is deliberately "
        + "a failure so that an unassigned field can never be mistaken for success. Use "
        + "Result.Success or Result.Failure(...)."
    );

    /// <summary>The successful outcome.</summary>
    public static Result Success { get; } = new(true, null);

    /// <summary>Whether the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Whether the operation failed. True for <c>default(Result)</c>.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>The error, or <see langword="null" /> when <see cref="IsSuccess" />.</summary>
    /// <remarks>
    ///     The <c>?? Uninitialized</c> is what makes <c>default(Result)</c> a failure with a real
    ///     error rather than a failure with a null one.
    /// </remarks>
    public Error? Error {
        get => IsSuccess ? null : field ?? Uninitialized;
        private init;
    }

    Result(bool isSuccess, Error? failure) {
        IsSuccess = isSuccess;
        Error = failure;
    }

    /// <summary>Creates a failed outcome.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="error" /> is null.</exception>
    public static Result Failure(Error error) {
        ArgumentNullException.ThrowIfNull(error);
        return new(false, error);
    }

    /// <summary>Creates a failed outcome from its parts. See <see cref="Error" />.</summary>
    public static Result Failure(ErrorCode code, string message, string? target = null) =>
        Failure(new(code, message, target));

    /// <summary>The failure-path idiom: <c>if (r.TryGetError(out var e)) return e;</c></summary>
    public bool TryGetError([NotNullWhen(true)] out Error? failure) {
        failure = Error;
        return failure is not null;
    }

    /// <inheritdoc />
    public override string ToString() => IsSuccess ? "Success" : $"Failure({Error})";
}

/// <summary>
///     The outcome of an operation that produces a <typeparamref name="T" />.
/// </summary>
/// <typeparam name="T">
///     The success value. Constrained <c>notnull</c> because "succeeded, and the answer is null" is
///     not a state this type models — use <c>Result&lt;Option&lt;T&gt;&gt;</c> or a sentinel if you
///     need one.
/// </typeparam>
/// <remarks>
///     ⚠ <c>default(Result&lt;T&gt;)</c> is a failure, for the reason given on <see cref="Result" />.
/// </remarks>
public readonly record struct Result<T>
    where T : notnull {
    readonly T? held;

    /// <summary>Whether the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Whether the operation failed. True for <c>default(Result&lt;T&gt;)</c>.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>The error, or <see langword="null" /> when <see cref="IsSuccess" />.</summary>
    public Error? Error {
        get => IsSuccess ? null : field ?? Result.Uninitialized;
        private init;
    }

    /// <summary>The value, or <see langword="default" /> when <see cref="IsFailure" />.</summary>
    public T? ValueOrDefault => IsSuccess ? held : default;

    Result(bool isSuccess, T? successValue, Error? failure) {
        IsSuccess = isSuccess;
        held = successValue;
        Error = failure;
    }

    /// <summary>Creates a successful outcome carrying <paramref name="value" />.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value" /> is null.</exception>
    public static Result<T> Success(T value) {
        ArgumentNullException.ThrowIfNull(value);
        return new(true, value, null);
    }

    /// <summary>Creates a failed outcome.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="failure" /> is null.</exception>
    public static Result<T> Failure(Error failure) {
        ArgumentNullException.ThrowIfNull(failure);
        return new(false, default, failure);
    }

    /// <summary>Creates a failed outcome from its parts. See <see cref="Error" />.</summary>
    public static Result<T> Failure(ErrorCode code, string message, string? target = null) =>
        Failure(new(code, message, target));

    /// <summary>The success-path idiom: <c>if (r.TryGetValue(out var v)) { … }</c></summary>
    /// <remarks>
    ///     ⚠ Returns <see cref="IsSuccess" />, <b>not</b> <c>result is not null</c>. Those differ
    ///     exactly when <typeparamref name="T" /> is a value type: <c>default(int)</c> is <c>0</c>,
    ///     which is not null, so the null-based form reports success on a failed
    ///     <c>Result&lt;int&gt;</c> and hands the caller a zero. Caught by
    ///     <c>ResultTests.DefaultGenericResultOverAValueTypeIsAlsoAFailure</c>.
    /// </remarks>
    public bool TryGetValue([NotNullWhen(true)] out T? result) {
        result = IsSuccess ? held : default;
        return IsSuccess;
    }

    /// <summary>The failure-path idiom: <c>if (r.TryGetError(out var e)) return e;</c></summary>
    public bool TryGetError([NotNullWhen(true)] out Error? failure) {
        failure = Error;
        return failure is not null;
    }

    /// <summary>
    ///     The value, or <see cref="InvalidOperationException" /> if this is a failure. For call
    ///     sites that have already checked, and for tests.
    /// </summary>
    /// <exception cref="InvalidOperationException">This is a failure.</exception>
    public T GetValueOrThrow() =>
        IsSuccess && held is not null
            ? held
            : throw new InvalidOperationException($"Result<{typeof(T).Name}> has no value: {Error}");

    /// <summary>Discards the value, keeping the outcome.</summary>
    public Result ToResult() => IsSuccess ? Result.Success : Result.Failure(Error!);

    /// <summary>The named alternate for <see cref="op_Implicit(T)" /> (CA2225).</summary>
    public static Result<T> FromValue(T value) => Success(value);

    /// <summary>The named alternate for <see cref="op_Implicit(Error)" /> (CA2225).</summary>
    public static Result<T> FromError(Error failure) => Failure(failure);

    /// <summary>Lifts a value into a successful result.</summary>
    public static implicit operator Result<T>(T value) => Success(value);

    /// <summary>Lifts an error into a failed result.</summary>
    public static implicit operator Result<T>(Error failure) => Failure(failure);

    /// <inheritdoc />
    public override string ToString() => IsSuccess ? $"Success({held})" : $"Failure({Error})";
}
