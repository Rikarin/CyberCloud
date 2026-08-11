using CyberCloud.Core.Contracts.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace CyberCloud.Core.Contracts.Tests;

/// <summary>
///     A real Orleans <see cref="Serializer" />, built the way a silo builds one.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Why this is not a hand-rolled round-trip.</b> Calling
///         <c>converter.ConvertToSurrogate</c> and then <c>ConvertFromSurrogate</c> proves the two
///         methods are inverses and proves nothing about serialization: it does not exercise the
///         generated codec, does not go through the type manifest, does not check that
///         <c>[RegisterConverter]</c> was discovered, and — the one that actually bites — does not
///         instantiate the <i>open generic</i> <c>ResultSurrogateConverter&lt;T&gt;</c>, which is
///         the part of this design most likely to be the thing that does not work. Everything here
///         goes through <see cref="Serializer.SerializeToArray{T}" /> and
///         <see cref="Serializer.Deserialize{T}(byte[])" />, i.e. bytes.
///     </para>
///     <para>
///         The assembly is named explicitly rather than left to ambient discovery so that the test
///         fails loudly if <c>CyberCloud.Core.Contracts</c> ever stops emitting a type manifest
///         (which is what would happen if somebody dropped <c>Microsoft.Orleans.Sdk</c> for
///         <c>Microsoft.Orleans.Core.Abstractions</c>).
///     </para>
/// </remarks>
public sealed class OrleansSerializerFixture : IDisposable
{
    readonly ServiceProvider provider;

    /// <summary>Builds the service provider and resolves the serializer.</summary>
    public OrleansSerializerFixture()
    {
        var services = new ServiceCollection();
        services.AddSerializer(builder =>
            builder.AddAssembly(typeof(ResultSurrogate).Assembly));

        provider = services.BuildServiceProvider();
        Serializer = provider.GetRequiredService<Serializer>();
    }

    /// <summary>Orleans' own serializer.</summary>
    public Serializer Serializer { get; }

    /// <summary>Serialises to bytes and back. The only round-trip helper these tests use.</summary>
    public T RoundTrip<T>(T value) => Serializer.Deserialize<T>(Serializer.SerializeToArray(value));

    /// <summary>The number of bytes <paramref name="value" /> occupies on the wire.</summary>
    public int WireLength<T>(T value) => Serializer.SerializeToArray(value).Length;

    /// <inheritdoc />
    public void Dispose() => provider.Dispose();
}
