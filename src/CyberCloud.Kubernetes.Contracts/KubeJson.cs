using System.Text.Json.Nodes;

namespace CyberCloud.Kubernetes.Contracts;

/// <summary>
///     The two ways an object read back out of a cluster differs from the object that was applied,
///     as predicates a comparison can be written against.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A comparison against a read-back object is never an equality test, and both
///         directions of that have cost a measured bug.</b> The API server <i>adds</i> — a CRD's
///         <c>+kubebuilder:default</c>, a <c>status</c> subresource, <c>managedFields</c>,
///         <c>creationTimestamp</c>, a defaulted <c>protocol</c> on every port — and it also
///         <i>removes</i>, because a field tagged <c>omitempty</c> on the Go type it deserialises into
///         is dropped when it is empty.
///     </para>
///     <para>
///         ⚠ <b>The removal direction generalises past the one provider that hit it.</b>
///         <c>NetworkPolicySpec.Ingress</c> carries <c>omitempty</c>, so the empty list that spells
///         "deny all ingress" comes back with <b>no key at all</b> —
///         <c>CyberCloud.Terminal/consoles</c> converged in the Docker-free harness and hung forever
///         against k3s. <i>Every</i> optional list and map on <i>every</i> built-in Kubernetes object
///         is tagged the same way. Eleven provider families have not hit it only because they render
///         custom resources, whose <c>x-kubernetes-preserve-unknown-fields</c> schemas round-trip an
///         empty array intact.
///     </para>
///     <para>
///         ⚠ <b>What these helpers do not model.</b> <c>omitempty</c> drops an empty
///         <see langword="string" />, a zero number and a <see langword="false" /> boolean as
///         readily as it drops an empty list, and which fields carry the tag lives in Go struct tags
///         this repository does not have. So a comparison on a scalar that happens to be zero-valued
///         is exposed to the same class of failure and nothing here will tell it so. Use these for
///         collections, where the rule is knowable, and read the built-in type's Go definition before
///         asserting a zero-valued scalar survives a round trip.
///     </para>
/// </remarks>
public static class KubeJson {
    /// <summary>
    ///     Whether a node means "no entries" — <see langword="null" />, absent, or present and empty.
    /// </summary>
    /// <param name="node">The node under the key, which may be <see langword="null" />.</param>
    /// <returns>
    ///     <see langword="true" /> when the collection is absent or empty, and
    ///     <see langword="false" /> when it holds at least one entry <b>or</b> is not a collection.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>This is the whole of <c>is not JsonArray { Count: &gt; 0 }</c>, written once.</b> The
    ///     shape it replaces is <c>is JsonArray { Count: 0 }</c>, which is true of what a provider
    ///     applies and false of what a real API server returns, and which therefore passes every
    ///     Docker-free suite and hangs against a real cluster. Accepting absent-or-empty is not a
    ///     weakening: for a collection, "not there" and "there and empty" are the same statement, and
    ///     this still refuses a list that grew an entry, which is the drift worth catching.
    /// </remarks>
    public static bool IsAbsentOrEmpty(JsonNode? node) =>
        node switch {
            null => true,
            JsonArray array => array.Count == 0,
            JsonObject map => map.Count == 0,
            _ => false
        };

    /// <summary>
    ///     Whether every member of <paramref name="expected" /> appears, with the same value, in
    ///     <paramref name="actual" /> — <b>containment, not equality</b>.
    /// </summary>
    /// <param name="actual">The object as the cluster returned it.</param>
    /// <param name="expected">The subtree the caller requires to be present.</param>
    /// <returns><see langword="true" /> when the cluster's object carries at least the subtree.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Equality here was a measured bug, not a style preference.</b> A real CRD's
    ///         <c>+kubebuilder:default</c> puts fields in the stored object that nobody applied, so an
    ///         equality comparison against a rendered spec fails against a real cluster and passes
    ///         everywhere else. It passed everywhere else because the Docker-free harness derives its
    ///         CRD stub from <c>ProviderConformanceCase.Objects</c> and a derived stub has no
    ///         defaults — an OpenSearch bug of exactly this shape left that suite 27 of 27 green and
    ///         was caught only by a hand-written unit test.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An empty expectation is satisfied by an absent key</b>, on the same argument as
    ///         <see cref="IsAbsentOrEmpty" />: a caller that requires an empty list requires something
    ///         a real API server will not return. Requiring a <i>non-empty</i> list still requires
    ///         every entry.
    ///     </para>
    /// </remarks>
    public static bool Contains(JsonNode? actual, JsonNode? expected) {
        if (expected is null) {
            return true;
        }

        switch (expected) {
            case JsonObject expectedMap:
                if (expectedMap.Count == 0) {
                    return IsAbsentOrEmpty(actual);
                }

                return actual is JsonObject actualMap
                    && expectedMap.All(pair => Contains(actualMap[pair.Key], pair.Value));

            case JsonArray expectedArray:
                if (expectedArray.Count == 0) {
                    return IsAbsentOrEmpty(actual);
                }

                // ⚠ Positional rather than set-wise, because the arrays this compares are rendered
                // lists whose order the provider chose and the API server preserves. A set-wise
                // match would also accept a rendered list that came back reordered, which for a
                // container's args or an ordered rule chain is a different program.
                return actual is JsonArray actualArray
                    && actualArray.Count >= expectedArray.Count
                    && expectedArray.Select((x, i) => Contains(actualArray[i], x)).All(x => x);

            default:
                return actual is not null
                    && JsonNode.DeepEquals(actual, expected);
        }
    }
}
