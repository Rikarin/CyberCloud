using CyberCloud.Core.Time;
using CyberCloud.Gateway.Host.Hubs;
using CyberCloud.Gateway.Host.Operations;
using CyberCloud.ResourceManager.Contracts.Registry;
using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace CyberCloud.Gateway.Host.Tests.Infrastructure;

/// <summary>A clock a test moves by hand.</summary>
sealed class FakeClock : IClock {
    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Moves time forward.</summary>
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>A registry with one type, one api-version and one action.</summary>
sealed class OneTypeRegistry : IProviderRegistry {
    /// <summary>The type every test uses.</summary>
    public static ResourceTypeName TheType { get; } = new("CyberCloud.DBforPostgreSQL", "servers");

    /// <summary>The api-version every test uses.</summary>
    public const string TheVersion = "2026-08-01";

    /// <summary>A version that is registered but older, to prove an old client keeps working.</summary>
    public const string OlderVersion = "2025-11-01";

    /// <inheritdoc />
    public ImmutableArray<ResourceTypeRegistration> Types { get; } = [
        new() {
            Type = new("CyberCloud.DBforPostgreSQL", "servers"),
            ApiVersions = [
                new(ApiVersion.Parse(OlderVersion), ResourceSchema.Empty),
                new(ApiVersion.Parse(TheVersion), ResourceSchema.Empty)
            ],
            Actions = [new("restart", ActionKind.Post, "write", false)]
        }
    ];

    /// <inheritdoc />
    public ImmutableArray<string> Namespaces { get; } = ["CyberCloud.DBforPostgreSQL"];

    /// <inheritdoc />
    public bool TryGetType(ResourceTypeName type, out ResourceTypeRegistration registration) {
        registration = Types[0];
        return type == TheType;
    }

    /// <inheritdoc />
    public Result<TypeResolution> Resolve(ResourceTypeName type, string? apiVersion) {
        if (type != TheType) {
            return Result<TypeResolution>.Failure(ErrorCode.InvalidResourceType, $"'{type}' is unknown.");
        }

        var version = ApiVersion.Parse(apiVersion is { Length: > 0 } supplied ? supplied : TheVersion);
        return Result<TypeResolution>.Success(new(Types[0], version, ResourceSchema.Empty));
    }
}

/// <summary>
///     A resource manager that records every path it was asked about and answers from a script.
/// </summary>
/// <remarks>
///     ⚠ <b><see cref="Paths" /> is what the tenant test asserts against.</b> "No grain call was made
///     against tenant B" is checked two ways: this list must contain no path naming B, and the
///     substitute <c>IGrainFactory</c> the harness holds must have received no calls at all.
/// </remarks>
sealed class RecordingResourceManager : IResourceManager {
    readonly ConcurrentQueue<string> paths = new();

    /// <summary>Every resource path this manager was asked about, in order.</summary>
    public IReadOnlyCollection<string> Paths => paths;

    /// <summary>What <see cref="ReadAsync" /> answers. Default: a resource that exists.</summary>
    public Func<WriteRequest, Result<ResourceSnapshot>> OnRead { get; set; } =
        request => Result<ResourceSnapshot>.Success(new() { Path = request.Path, Name = "main" });

    /// <summary>What the three write paths answer. Default: a <c>202</c>.</summary>
    public Func<WriteRequest, Result<WriteAccepted>> OnWrite { get; set; } =
        request => Result<WriteAccepted>.Success(new() {
            OperationId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            RetryAfterSeconds = 10,
            Resource = new() { Path = request.Path, Name = "main" }
        });

    /// <inheritdoc />
    public Task<Result<WriteAccepted>> WriteAsync(WriteRequest request, CancellationToken cancellationToken = default) =>
        Record(request, OnWrite);

    /// <inheritdoc />
    public Task<Result<ResourceSnapshot>> ReadAsync(WriteRequest request, CancellationToken cancellationToken = default) =>
        Record(request, OnRead);

    /// <inheritdoc />
    public Task<Result<WriteAccepted>> DeleteAsync(WriteRequest request, CancellationToken cancellationToken = default) =>
        Record(request, OnWrite);

    /// <inheritdoc />
    public Task<Result<WriteAccepted>> ActionAsync(WriteRequest request, CancellationToken cancellationToken = default) =>
        Record(request, OnWrite);

    Task<Result<T>> Record<T>(WriteRequest request, Func<WriteRequest, Result<T>> answer)
        where T : notnull {
        ArgumentNullException.ThrowIfNull(request);
        paths.Enqueue(request.Path);
        return Task.FromResult(answer(request));
    }
}

/// <summary>An interest authorizer a test flips at will. Stands in for the enforcement seam.</summary>
sealed class ScriptedInterestAuthorizer : IInterestAuthorizer {
    /// <summary>Which paths are readable. Everything else answers the canonical <c>404</c>.</summary>
    public HashSet<string> Readable { get; } = new(StringComparer.Ordinal);

    /// <summary>How many times the seam was asked.</summary>
    public int Asked { get; private set; }

    /// <inheritdoc />
    public Task<Result> CanReadAsync(
        CallerContext caller,
        string resourcePath,
        CancellationToken cancellationToken = default
    ) {
        Asked++;

        return Task.FromResult(
            Readable.Contains(resourcePath)
                ? Result.Success
                : Result.Failure(ErrorCode.ResourceNotFound, $"'{resourcePath}' does not exist.")
        );
    }
}

/// <summary>
///     An operation reader a test scripts. Stands in for <c>TenantScopedOperationReader</c>, whose
///     own two boundaries — <c>ForTenant</c> and the resource-manager read — are asserted separately.
/// </summary>
/// <remarks>
///     ⚠ Hand-written rather than an NSubstitute proxy, because <c>IOperationReader</c> is
///     <c>internal</c> and Castle cannot proxy an internal interface without
///     <c>InternalsVisibleTo("DynamicProxyGenAssembly2")</c> on the gateway. Adding that attribute to
///     make a test framework happy would widen the production assembly's surface, which is the wrong
///     trade for eight lines.
/// </remarks>
sealed class ScriptedOperationReader : IOperationReader {
    /// <summary>What every read answers.</summary>
    public Func<Guid, Result<OperationStatus>> OnRead { get; set; } =
        id => Result<OperationStatus>.Failure(ErrorCode.ResourceNotFound, $"'/operations/{id:D}' does not exist.");

    /// <summary>Which operations were asked for.</summary>
    public List<Guid> Read { get; } = [];

    /// <inheritdoc />
    public Task<Result<OperationStatus>> ReadAsync(
        CallerContext caller,
        Guid operationId,
        CancellationToken cancellationToken = default
    ) {
        Read.Add(operationId);
        return Task.FromResult(OnRead(operationId));
    }
}
