
namespace CyberCloud.Core.Contracts.Serialization;

/// <summary>
///     The wire form of <see cref="Result" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The default-value trap, and how this shape closes it.</b> Core's
///         <c>default(Result)</c> is a <i>failure</i> — see the remarks on
///         <c>CyberCloud.Core/Result.cs</c>. Orleans hands a converter
///         <c>default(TSurrogate)</c> whenever no field was written for the value (an older peer, a
///         truncated payload, a member that did not exist in version N), and
///         <c>default(ResultSurrogate).IsSuccess</c> is <see langword="false" />, so the
///         reconstructed <see cref="Result" /> is a failure too. The member numbering is what makes
///         that true: <see cref="IsSuccess" /> is <c>[Id(0)]</c> and it is a <c>bool</c> whose
///         default is the safe answer. <b>Do not invert this member into an <c>IsFailure</c> flag</b>
///         — the identical code would then read a missing field as success.
///     </para>
///     <para>
///         The complementary direction is covered too: a failed <see cref="Result" /> always
///         reports a non-null <c>Error</c> (Core substitutes its own <c>Uninitialized</c> when the
///         backing field is null), so <see cref="Error" /> below is only <see langword="null" /> on
///         the wire when a peer wrote nothing — and <see cref="WireErrors.MissingError" /> makes
///         that a failure rather than an exception.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Core.Result")]
public struct ResultSurrogate
{
    /// <summary>
    ///     Whether the operation succeeded. <c>[Id(0)]</c> and <c>false</c>-by-default on purpose —
    ///     see the remarks on the type.
    /// </summary>
    [Id(0)]
    public bool IsSuccess { get; set; }

    /// <summary>The error, or <see langword="null" /> when <see cref="IsSuccess" />.</summary>
    [Id(1)]
    public Error? Error { get; set; }
}

/// <summary>The <see cref="Result" /> ↔ <see cref="ResultSurrogate" /> converter.</summary>
[RegisterConverter]
public sealed class ResultSurrogateConverter : IConverter<Result, ResultSurrogate>
{
    /// <inheritdoc />
    public Result ConvertFromSurrogate(in ResultSurrogate surrogate) =>
        surrogate.IsSuccess
            ? Result.Success
            : Result.Failure(surrogate.Error ?? WireErrors.MissingError);

    /// <inheritdoc />
    public ResultSurrogate ConvertToSurrogate(in Result value) =>
        new() { IsSuccess = value.IsSuccess, Error = value.Error };
}

/// <summary>
///     The wire form of <see cref="Result{T}" />.
/// </summary>
/// <typeparam name="T">The success value's type. <c>notnull</c>, as on <see cref="Result{T}" />.</typeparam>
/// <remarks>
///     <para>
///         Same default-value discipline as <see cref="ResultSurrogate" />: <c>[Id(0)]</c> is
///         <see cref="IsSuccess" />, so an unwritten payload deserialises to a failure.
///     </para>
///     <para>
///         ⚠ <b><see cref="Value" /> is written even for a failure</b>, because a failed
///         <c>Result&lt;T&gt;</c> holds <c>default(T)</c> and Orleans serialises the field either
///         way. For a <c>T</c> that is itself a validating value type — <c>ResourceId</c>,
///         <c>ResourceTypeName</c> — that means <b>their</b> surrogates have to
///         round-trip <c>default(T)</c> without throwing, which is why each of them special-cases
///         it. A <c>Result&lt;ResourceId&gt;</c> failure is the exact payload that finds this, and
///         it is one of the round-trip tests.
///     </para>
///     <para>
///         The alias carries the arity (<c>`1</c>) because that is how Orleans names an open
///         generic in the type manifest; it is what makes the alias stable across a rename of the
///         CLR type (docs/plan/04:177).
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Core.Result`1")]
public struct ResultSurrogate<T>
    where T : notnull
{
    /// <summary>Whether the operation succeeded. See the remarks on <see cref="ResultSurrogate" />.</summary>
    [Id(0)]
    public bool IsSuccess { get; set; }

    /// <summary>The value, or <c>default</c> when this is a failure.</summary>
    [Id(1)]
    public T? Value { get; set; }

    /// <summary>The error, or <see langword="null" /> when <see cref="IsSuccess" />.</summary>
    [Id(2)]
    public Error? Error { get; set; }
}

/// <summary>The <see cref="Result{T}" /> ↔ <see cref="ResultSurrogate{T}" /> converter.</summary>
/// <typeparam name="T">The success value's type.</typeparam>
/// <remarks>
///     ⚠ An <b>open generic</b> converter. Orleans registers it once and instantiates it per
///     <c>T</c> at runtime, so <c>Result&lt;Whatever&gt;</c> works without this assembly ever
///     naming <c>Whatever</c> — which it could not, since the interesting <c>T</c>s live in provider
///     contract assemblies that reference this one. That this actually works, rather than being the
///     obvious thing that quietly does not, is asserted by
///     <c>ResultSerializationTests</c> round-tripping <c>Result&lt;ResourceId&gt;</c> and
///     <c>Result&lt;int&gt;</c> through a real <c>Serializer</c>.
/// </remarks>
[RegisterConverter]
public sealed class ResultSurrogateConverter<T> : IConverter<Result<T>, ResultSurrogate<T>>
    where T : notnull
{
    /// <inheritdoc />
    public Result<T> ConvertFromSurrogate(in ResultSurrogate<T> surrogate)
    {
        if (!surrogate.IsSuccess)
        {
            return Result<T>.Failure(surrogate.Error ?? WireErrors.MissingError);
        }

        // "Succeeded" with no value is not a state Result<T> can hold (T : notnull), so it becomes
        // a failure rather than an ArgumentNullException out of Result<T>.Success.
        return surrogate.Value is { } value
            ? Result<T>.Success(value)
            : Result<T>.Failure(WireErrors.MissingValue);
    }

    /// <inheritdoc />
    public ResultSurrogate<T> ConvertToSurrogate(in Result<T> value) =>
        new()
        {
            IsSuccess = value.IsSuccess,
            Value = value.ValueOrDefault,
            Error = value.Error,
        };
}
