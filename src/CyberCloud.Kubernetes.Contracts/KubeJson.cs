using System.Text.Json.Nodes;

namespace CyberCloud.Kubernetes.Contracts;

/// <summary>
///     The two ways an object read back out of a cluster differs from the object that was applied,
///     as predicates a comparison can be written against.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             A comparison against a read-back object is never an equality test, and both
///             directions of that have cost a measured bug.
///         </b> The API server <i>adds</i> — a CRD's
///         <c>+kubebuilder:default</c>, a <c>status</c> subresource, <c>managedFields</c>,
///         <c>creationTimestamp</c>, a defaulted <c>protocol</c> on every port — and it also
///         <i>removes</i>, because a field tagged <c>omitempty</c> on the Go type it deserialises into
///         is dropped when it is empty.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The removal direction generalises past the one provider that hit it — across
///             built-in objects.
///         </b> <c>NetworkPolicySpec.Ingress</c> carries <c>omitempty</c>, so the
///         empty list that spells "deny all ingress" comes back with <b>no key at all</b> —
///         <c>CyberCloud.Terminal/consoles</c> converged in the Docker-free harness and hung forever
///         against k3s. <i>Every</i> optional list and map on <i>every</i> built-in Kubernetes object
///         is tagged the same way, so use <see cref="IsAbsentOrEmpty" /> for all of them.
///     </para>
///     <para>
///         ⚠
///         <b>
///             A custom resource is the opposite case, and reaching for
///             <see cref="IsAbsentOrEmpty" /> on one would discard a real distinction.
///         </b> A CRD has no
///         <c>omitempty</c>: its stored JSON keeps what was applied, so absent and present-but-empty
///         are <i>different</i>, and three families rely on that — Strimzi's
///         <c>spec.cruiseControl = {}</c> means "run Cruise Control", and Cluster API's
///         <c>bridge = {}</c> and kube-ovn's <c>pod = {}</c> are the same idiom. This is the right
///         tool for a built-in and the wrong one for a presence flag.
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
    ///     <para>
    ///         ⚠ <b>This is the whole of <c>is not JsonArray { Count: &gt; 0 }</c>, written once.</b>
    ///         The shape it replaces is <c>is JsonArray { Count: 0 }</c>, which is true of what a
    ///         provider applies to a built-in object and false of what a real API server returns, and
    ///         which therefore passes a Docker-free suite and hangs against a real cluster. On a
    ///         built-in, accepting absent-or-empty is not a weakening: "not there" and "there and
    ///         empty" are the same statement, and this still refuses a list that grew an entry, which
    ///         is the drift worth catching.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Do not reach for it on a custom resource whose empty object is a presence
    ///             flag
    ///         </b> — see the remarks on the type. There the two are not the same statement, and
    ///         this would erase the difference.
    ///     </para>
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
    ///         equality comparison against a rendered spec fails against a real cluster and passed
    ///         everywhere else. It passed everywhere else because, until issue #91, the Docker-free
    ///         harness derived its CRD stub from <c>ProviderConformanceCase.Objects</c> and a derived
    ///         stub has no defaults — an OpenSearch bug of exactly this shape left that suite 27 of
    ///         27 green and was caught only by a hand-written unit test. The fake applies the
    ///         committed definition's defaults now (<c>FakeKubeCluster.Admit</c>), so that shape is
    ///         red in the Docker-free suite too; the rule stands because the server adds more than
    ///         defaults.
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

    /// <summary>The object's <c>metadata.uid</c>, or empty when the document carries none.</summary>
    /// <param name="document">An object as the API server returned it.</param>
    /// <remarks>
    ///     ⚠ Empty rather than a throw, because the one document that legitimately has no uid is the
    ///     one a provider rendered and has not applied yet — and a caller comparing uids treats empty
    ///     as "not identified", which is the safe reading.
    /// </remarks>
    public static string UidOf(JsonNode? document) =>
        (document as JsonObject)?["metadata"] is JsonObject metadata
        && metadata["uid"] is JsonValue value
        && value.TryGetValue<string>(out var uid)
            ? uid
            : string.Empty;

    /// <summary>
    ///     The owner reference marked <c>controller: true</c>, or <see langword="null" /> when the
    ///     object has no controller.
    /// </summary>
    /// <param name="document">An object as the API server returned it.</param>
    /// <remarks>
    ///     <para>
    ///         Kubernetes allows at most one controller per object and any number of plain owners.
    ///         This reads the controller only, because it is the one an operator indexes on —
    ///         CloudNativePG's <c>getManagedPVCs</c> lists claims by
    ///         <c>metav1.GetControllerOf</c>, so a claim whose controller entry is gone is a claim
    ///         the operator no longer sees, whatever other owners it keeps.
    ///     </para>
    ///     <para>
    ///         ⚠ An entry with no <c>uid</c> is not a controller. The garbage collector treats such
    ///         a reference as pointing at nothing, and a caller that took it for an identity would
    ///         be comparing against the empty string.
    ///     </para>
    /// </remarks>
    public static OwnerRef? ControllerOf(JsonNode? document) {
        if ((document as JsonObject)?["metadata"] is not JsonObject metadata
            || metadata["ownerReferences"] is not JsonArray owners) {
            return null;
        }

        foreach (var owner in owners.OfType<JsonObject>()) {
            if (owner["controller"] is not JsonValue flag || !flag.TryGetValue<bool>(out var controller) || !controller) {
                continue;
            }

            var reference = new OwnerRef {
                ApiVersion = Text(owner["apiVersion"]),
                Kind = Text(owner["kind"]),
                Name = Text(owner["name"]),
                Uid = Text(owner["uid"])
            };

            return reference.IsComplete ? reference : null;
        }

        return null;
    }

    /// <summary>
    ///     Whether the object names any owner at all — controller or not.
    /// </summary>
    /// <param name="document">An object as the API server returned it.</param>
    /// <remarks>
    ///     The garbage collector follows every entry, not only the controller, so "may this object
    ///     outlive a delete" is this question rather than <see cref="ControllerOf" />'s.
    /// </remarks>
    public static bool HasOwners(JsonNode? document) =>
        (document as JsonObject)?["metadata"] is JsonObject metadata
        && metadata["ownerReferences"] is JsonArray { Count: > 0 };

    /// <summary>The <c>metadata.ownerReferences</c> entry an <see cref="OwnerRef" /> renders as.</summary>
    /// <param name="owner">The owner.</param>
    public static JsonObject OwnerReference(OwnerRef owner) {
        ArgumentNullException.ThrowIfNull(owner);

        return new JsonObject {
            ["apiVersion"] = owner.ApiVersion,
            ["kind"] = owner.Kind,
            ["name"] = owner.Name,
            ["uid"] = owner.Uid,
            ["controller"] = true
        };
    }

    static string Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;
}
