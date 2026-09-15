using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager;

/// <summary>
///     Projects a resource's stored superset down to the body one api-version declares — the
///     "projects down" of docs/plan/08 § The provider registry, and the function that decides what
///     <see cref="ResourceSnapshot.Body" /> holds.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The result is a whole document, not the inner <c>properties</c> slice.</b> Each
///         declared pointer is written at its full path, so a type declaring <c>/location</c> and
///         <c>/properties/message</c> projects <c>{"location":…,"properties":{"message":…}}</c> —
///         the same shape the published OpenAPI document gives the type's body, and the shape every
///         provider's conformance run compares against the body it wrote
///         (<c>ProviderConformanceTests.TheResourceReadsBackWithWhatWasWritten</c>). The gateway's
///         <c>ResponseBodies.WriteResource</c> once assumed it received the slice and wrapped it in a
///         second <c>properties</c> — issue #72 — and nothing saw it because the gateway suite's
///         substitute manager hand-wrote a snapshot in the shape the writer expected rather than the
///         shape this function produces.
///     </para>
///     <para>
///         ⚠ <b>Public, and for exactly one reason: so a test double can be built from it.</b>
///         <see cref="Grains.ResourceGrain" /> calls this from its own snapshot; the gateway suite's
///         <c>RecordingResourceManager</c> calls the same function over the same kind of document, so
///         the substitute's <see cref="ResourceSnapshot.Body" /> is the real projection rather than a
///         hand-written imitation of one. It is pure over its arguments — no registry, no clock, no
///         grain — which is what makes it callable from a suite that holds no silo.
///     </para>
///     <para>
///         ⚠ <b>An empty pointer list projects the whole superset, not nothing.</b> That is a
///         deliberate escape hatch for the paths that have no registry to consult — the delete path,
///         which reports a snapshot of a resource whose api-version may already be retired, and the
///         reconcile driver, which needs the state as stored. Every path that <i>does</i> have a
///         registry passes the real list, and
///         <c>WritePathTests.AReadAtAnOldVersionKeepsGettingTheShapeItWasWrittenAgainst</c> asserts an
///         old version never sees a newer one's field.
///     </para>
/// </remarks>
public static class ResourceProjection {
    /// <summary>Keeps only the pointers an api-version declares.</summary>
    /// <param name="superset">The stored superset — every api-version's fields together.</param>
    /// <param name="declaredPointers">
    ///     The RFC 6901 pointers the api-version declares, in declaration order. Empty projects the
    ///     whole superset — read the remarks before passing empty.
    /// </param>
    /// <returns>A new document holding the declared leaves at their full paths.</returns>
    public static JsonObject Project(JsonObject superset, ImmutableArray<string> declaredPointers) {
        ArgumentNullException.ThrowIfNull(superset);

        if (declaredPointers.IsDefaultOrEmpty) {
            return (JsonObject)superset.DeepClone();
        }

        var projected = new JsonObject();

        foreach (var pointer in declaredPointers) {
            var node = JsonPointer.Read(superset, pointer);
            if (node is null or JsonObject) {
                // A container is rebuilt by whichever of its leaves lands first; projecting it whole
                // would carry the undeclared members inside it straight through the filter.
                continue;
            }

            JsonPointer.Write(projected, pointer, node.DeepClone());
        }

        return projected;
    }
}
