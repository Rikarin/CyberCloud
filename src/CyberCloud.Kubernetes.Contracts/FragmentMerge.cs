using System.Text.Json.Nodes;

namespace CyberCloud.Kubernetes.Contracts;

/// <summary>
///     Folds the fragments several co-writers have applied onto one object into the single body
///     their shared field manager applies — <see cref="IKubeCommandBuilder.CoWriting" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Three rules, and they are the whole of the merge.</b> Two objects merge member by
///         member, recursively. Two arrays concatenate, in co-writer order, because the arrays a
///         co-writer reaches for are lists of entries each co-writer contributes some of —
///         <c>Vpc.spec.vpcPeerings</c> is one entry per peering. Two scalars must be equal, and two
///         that are not is a refusal naming the path and both co-writers: a scalar has one value and
///         picking either would let one resource's desired state silently overwrite another's, which
///         is the failure the co-owned mode exists to make impossible.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Co-writer order is the ordinal order of their GUIDs, and it has to be some fixed
///             order.
///         </b> The merged array is what the API server stores, and the reconcile hash each
///         co-writer compares is over its own fragment rather than the union — so order does not move
///         a hash. But the union is applied as one atomic value, and a union whose order depended on
///         which co-writer applied last would make every co-writer's apply an <c>Updated</c> that
///         changed nothing a tenant asked for.
///     </para>
///     <para>
///         ⚠ <b>Kind mismatch is a refusal, not a coercion.</b> One fragment's object against
///         another's array at the same path is two resources disagreeing about the object's schema,
///         and no merge is right.
///     </para>
/// </remarks>
static class FragmentMerge {
    /// <summary>Merges the fragments, ordered by co-writer, into one document.</summary>
    /// <param name="fragments">Each co-writer's fragment, with the co-writer's GUID for the message.</param>
    /// <returns>The union, or the failure that names the path two fragments disagree on.</returns>
    public static Result<JsonObject> Merge(IEnumerable<(Guid Writer, JsonObject Fragment)> fragments) {
        ArgumentNullException.ThrowIfNull(fragments);

        var result = new JsonObject();
        var writers = new Dictionary<string, Guid>(StringComparer.Ordinal);

        foreach (var (writer, fragment) in fragments.OrderBy(
                     static x => KubeLabels.GuidValue(x.Writer),
                     StringComparer.Ordinal
                 )) {
            var merged = MergeInto(result, fragment, writer, writers, string.Empty);
            if (merged.TryGetError(out var error)) {
                return Result<JsonObject>.Failure(error);
            }
        }

        return Result<JsonObject>.Success(result);
    }

    static Result MergeInto(
        JsonObject target,
        JsonObject fragment,
        Guid writer,
        Dictionary<string, Guid> writers,
        string path
    ) {
        foreach (var (key, value) in fragment) {
            var here = path + "/" + key;

            if (!target.TryGetPropertyValue(key, out var existing) || existing is null) {
                target[key] = value?.DeepClone();
                Record(value, here, writer, writers);
                continue;
            }

            switch (existing, value) {
                case (JsonObject left, JsonObject right): {
                    var nested = MergeInto(left, right, writer, writers, here);
                    if (nested.TryGetError(out _)) {
                        return nested;
                    }

                    break;
                }

                case (JsonArray left, JsonArray right):
                    foreach (var item in right) {
                        left.Add(item?.DeepClone());
                    }

                    break;

                case (JsonValue left, JsonValue right) when JsonNode.DeepEquals(left, right):
                    break;

                case (JsonValue, JsonValue):
                    return Disagree(here, writers, writer, "set it to different values");

                default:
                    return Disagree(here, writers, writer, "give it different shapes");
            }
        }

        return Result.Success;
    }

    /// <summary>Remembers who first wrote every path under a subtree, so a later disagreement can name them.</summary>
    static void Record(JsonNode? node, string path, Guid writer, Dictionary<string, Guid> writers) {
        writers[path] = writer;

        if (node is JsonObject obj) {
            foreach (var (key, value) in obj) {
                Record(value, path + "/" + key, writer, writers);
            }
        }
    }

    static Result Disagree(string path, Dictionary<string, Guid> writers, Guid writer, string how) =>
        Result.Failure(
            ErrorCode.InvalidRequestBody,
            $"The Kubernetes command cannot be built: two co-writers' fragments disagree at '{path}'. "
            + $"Resource {KubeLabels.GuidValue(writers.GetValueOrDefault(path, Guid.Empty))} and resource "
            + $"{KubeLabels.GuidValue(writer)} {how}, and a scalar has one value: applying either would let "
            + "one resource's desired state overwrite another's silently, which is what the co-owned mode "
            + "exists to prevent. Fragments may share objects and add to arrays; they may not both set one "
            + "scalar."
        );
}
