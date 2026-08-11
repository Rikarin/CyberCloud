using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Contracts.Generation;

/// <summary>One way a regenerated document breaks the one it replaces.</summary>
/// <param name="JsonPointer">Where, as an RFC 6901 pointer into the document.</param>
/// <param name="Rule">Which rule — one of <see cref="OpenApiCompatibility" />'s named constants.</param>
/// <param name="Detail">
///     What changed, naming both values. The message is read by whoever has to decide between
///     reverting the change and minting a new api-version, and "incompatible change at
///     /paths/…/schema" does not help them do that.
/// </param>
public readonly record struct BreakingChange(string JsonPointer, string Rule, string Detail) {
    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{JsonPointer} — [{Rule}] {Detail}");
}

/// <summary>
///     Diffs a regenerated document against the one checked in for the same api-version.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/21 § OpenAPI, in full: <i>"The diff rules are explicit — adding an optional field
///         is fine, removing anything or narrowing a type is not."</i> and <i>"a breaking change to a
///         published version fails CI"</i>. docs/plan/23 § The architecture gates lists it as its own
///         row, separate from the byte-identical one, because the two ask different questions: did
///         the generator drift from the registry, and did the registry drift from what was promised.
///     </para>
///     <para>
///         ⚠ <b>The rule is expressed as one property over JSON pointers rather than as a list of
///         OpenAPI-shaped special cases</b>, and that is what makes it complete. <i>Every pointer that
///         existed must still exist, and every value at it must be unchanged; anything new is an
///         addition and is fine.</i> A removed path, a removed operation, a removed property, a
///         removed parameter, a removed response and a removed error code are the same rule applied
///         at six depths, and a list of six checks is a list that grows to five when somebody adds a
///         seventh kind of thing to the document.
///     </para>
///     <para>
///         ⚠ <b>Any change to a scalar is breaking, including one that looks like a widening.</b>
///         Raising a <c>maxLength</c> is not a break in isolation, and it is still refused, because
///         docs/plan/08 § The provider registry makes an api-version <i>immutable</i> — the sanctioned
///         way to change a published shape is a new date, not an edit that happened to be safe. The
///         one exception is documentation: <c>description</c>, <c>summary</c>, <c>title</c>,
///         <c>example</c> and every <c>x-</c> extension may change freely, because prose is not a
///         contract and a typo fix should not need a new api-version.
///     </para>
///     <para>
///         ⚠ <b>This compares two documents. It cannot see a change that is not in either.</b> If a
///         published version's file is deleted rather than edited, the diff has nothing to compare and
///         reports nothing — that case belongs to the 12-month retirement gate
///         docs/plan/08 § The provider registry asks for and which is not written; see
///         <see cref="Removed" />'s remarks on where that seam is.
///     </para>
/// </remarks>
public static class OpenApiCompatibility {
    /// <summary>Something that was published is gone.</summary>
    /// <remarks>
    ///     ⚠ This is the rule that catches a removed api-version's <i>contents</i>, not a removed
    ///     api-version. A whole file disappearing is invisible here by construction and needs the
    ///     retirement gate docs/plan/08 § The provider registry asks for.
    /// </remarks>
    public const string Removed = "removed";

    /// <summary>A value that was published is different.</summary>
    public const string Changed = "changed";

    /// <summary>A property that was optional is now required.</summary>
    public const string RequiredAdded = "required-added";

    /// <summary>A value an enumeration accepted is gone.</summary>
    public const string EnumValueRemoved = "enum-value-removed";

    /// <summary>An object that accepted unknown members no longer does.</summary>
    public const string AdditionalPropertiesTightened = "additional-properties-tightened";

    /// <summary>A published document that no longer parses.</summary>
    public const string Unreadable = "unreadable";

    /// <summary>
    ///     Members whose value is prose. A change to one is not a change to the contract.
    /// </summary>
    static readonly ImmutableHashSet<string> Documentation =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "description",
            "summary",
            "title",
            "example",
            "examples",
            "externalDocs"
        );

    /// <summary>
    ///     Compares a published document against its replacement.
    /// </summary>
    /// <param name="published">The document currently checked in for this api-version.</param>
    /// <param name="regenerated">What the registry produces now.</param>
    /// <returns>Every breaking change, ordered by pointer. Empty means the change is compatible.</returns>
    public static ImmutableArray<BreakingChange> Diff(JsonNode? published, JsonNode? regenerated) {
        var found = new List<BreakingChange>();

        if (published is null) {
            // A version that was not published cannot be broken. This is the "new api-version" case
            // and it is always compatible.
            return [];
        }

        if (regenerated is null) {
            found.Add(new(
                "",
                Removed,
                "The regenerated document is empty and the published one is not."
            ));

            return [.. found];
        }

        Compare(published, regenerated, "", found);

        return [.. found.OrderBy(x => x.JsonPointer, StringComparer.Ordinal).ThenBy(x => x.Rule, StringComparer.Ordinal)];
    }

    static void Compare(JsonNode? published, JsonNode? regenerated, string pointer, List<BreakingChange> found) {
        switch (published) {
            case JsonObject before:
                CompareObjects(before, regenerated, pointer, found);
                break;

            case JsonArray before:
                CompareArrays(before, regenerated, pointer, found);
                break;

            default:
                CompareScalars(published, regenerated, pointer, found);
                break;
        }
    }

    static void CompareObjects(JsonObject before, JsonNode? regenerated, string pointer, List<BreakingChange> found) {
        if (regenerated is not JsonObject after) {
            found.Add(new(
                pointer,
                Changed,
                $"was an object and is now {Describe(regenerated)}."
            ));

            return;
        }

        foreach (var member in before) {
            var child = pointer + "/" + Escape(member.Key);

            if (IsDocumentation(member.Key)) {
                continue;
            }

            if (!after.ContainsKey(member.Key)) {
                found.Add(new(
                    child,
                    Removed,
                    IsConstraint(member.Key)
                        // ⚠ Dropping a constraint relaxes the shape rather than removing a feature, so
                        // this one is not "a caller breaks". It is still refused: docs/plan/08 § The
                        // provider registry makes a published api-version immutable, and "we only made
                        // it more permissive" is how a version stops meaning one thing.
                        ? $"the '{member.Key}' constraint was published here and is gone. A published "
                        + "api-version is immutable, so relaxing one needs a new date rather than an "
                        + "edit."
                        : $"'{member.Key}' was published here and is gone. Removing anything from a "
                        + "published api-version is a breaking change — docs/plan/21 § OpenAPI."
                ));

                continue;
            }

            Compare(member.Value, after[member.Key], child, found);
        }

        CheckRequired(before, after, pointer, found);
        CheckAdditionalProperties(before, after, pointer, found);
    }

    /// <summary>
    ///     A name that appears in the new <c>required</c> and not the old one narrows the request.
    /// </summary>
    /// <remarks>
    ///     ⚠ Handled here rather than by the array walk because <c>required</c> is a <i>set</i>: its
    ///     order carries no meaning, and an index-wise comparison would report a break every time a
    ///     new optional property sorted between two required ones.
    /// </remarks>
    static void CheckRequired(JsonObject before, JsonObject after, string pointer, List<BreakingChange> found) {
        var was = Strings(before["required"]);
        var now = Strings(after["required"]);

        foreach (var name in now.Except(was, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)) {
            found.Add(new(
                pointer + "/required",
                RequiredAdded,
                $"'{name}' was optional and is now required. Every request that was valid and omitted it "
                + "is now invalid — the sanctioned way to add a required property is a new api-version."
            ));
        }
    }

    static void CheckAdditionalProperties(
        JsonObject before,
        JsonObject after,
        string pointer,
        List<BreakingChange> found
    ) {
        // Absent means "true" in JSON Schema, so an object that said nothing and now says false has
        // tightened just as much as one that said true.
        var was = before["additionalProperties"] is not JsonValue value
                  || !value.TryGetValue<bool>(out var allowed)
                  || allowed;

        var now = after["additionalProperties"] is not JsonValue current
                  || !current.TryGetValue<bool>(out var stillAllowed)
                  || stillAllowed;

        if (was && !now) {
            found.Add(new(
                pointer + "/additionalProperties",
                AdditionalPropertiesTightened,
                "unknown members were accepted here and are now refused."
            ));
        }
    }

    static void CompareArrays(JsonArray before, JsonNode? regenerated, string pointer, List<BreakingChange> found) {
        if (regenerated is not JsonArray after) {
            found.Add(new(pointer, Changed, $"was an array and is now {Describe(regenerated)}."));
            return;
        }

        // ⚠ `required` is a set, and its parent object already compared it as one — see CheckRequired.
        // Falling through to the index-wise walk below would report a break every time a new optional
        // property sorted between two required ones, which is the exact change docs/plan/21 § OpenAPI
        // calls fine.
        if (pointer.EndsWith("/required", StringComparison.Ordinal)) {
            return;
        }

        // `enum` is a set too, and here removal is the breaking direction: a value the API accepted
        // no longer being accepted breaks the caller that sends it.
        if (pointer.EndsWith("/enum", StringComparison.Ordinal)) {
            var was = Strings(before);
            var now = Strings(after);

            foreach (var value in was.Except(now, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)) {
                found.Add(new(
                    pointer,
                    EnumValueRemoved,
                    $"'{value}' was an accepted value and is gone. A caller that sends it, or a client "
                    + "that has a member for it, now breaks."
                ));
            }

            return;
        }

        // A `parameters` array is keyed by what it refers to rather than by position, so inserting a
        // parameter does not read as "every parameter after it changed".
        if (pointer.EndsWith("/parameters", StringComparison.Ordinal)) {
            var now = after.Select(Key).ToHashSet(StringComparer.Ordinal);

            foreach (var entry in before) {
                var key = Key(entry);

                if (!now.Contains(key)) {
                    found.Add(new(
                        pointer,
                        Removed,
                        $"parameter '{key}' was published here and is gone."
                    ));
                }
            }

            return;
        }

        for (var i = 0; i < before.Count; i++) {
            var child = pointer + "/" + i.ToString(CultureInfo.InvariantCulture);

            if (i >= after.Count) {
                found.Add(new(child, Removed, "the array is shorter than the one published."));
                continue;
            }

            Compare(before[i], after[i], child, found);
        }
    }

    static void CompareScalars(JsonNode? published, JsonNode? regenerated, string pointer, List<BreakingChange> found) {
        var was = published?.ToJsonString() ?? "null";
        var now = regenerated?.ToJsonString() ?? "null";

        if (!string.Equals(was, now, StringComparison.Ordinal)) {
            found.Add(new(
                pointer,
                Changed,
                $"was {was} and is now {now}. An api-version is immutable — docs/plan/08 § The provider "
                + "registry. Mint a new date rather than editing a published one."
            ));
        }
    }

    /// <summary>
    ///     The identity of one entry in a <c>parameters</c> array.
    /// </summary>
    static string Key(JsonNode? parameter) {
        if (parameter is not JsonObject obj) {
            return parameter?.ToJsonString() ?? "null";
        }

        if (obj["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var target)) {
            return target;
        }

        var name = obj["name"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "?";
        var location = obj["in"] is JsonValue where && where.TryGetValue<string>(out var place) ? place : "?";
        return location + ":" + name;
    }

    static ImmutableHashSet<string> Strings(JsonNode? node) =>
        node is not JsonArray array
            ? []
            : [
                .. array
                    .Select(x => x is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
                    .Where(x => x is not null)
                    .Select(x => x!)
            ];

    /// <summary>
    ///     Whether a member's value is prose rather than contract. Every <c>x-</c> extension counts:
    ///     an extension is a hint for a generator, and a hint changing does not break a caller.
    /// </summary>
    static bool IsDocumentation(string key) =>
        Documentation.Contains(key) || key.StartsWith("x-", StringComparison.Ordinal);

    /// <summary>
    ///     Whether a member narrows the shape rather than being part of it, so that its disappearance
    ///     can be reported as the relaxation it is rather than as a lost feature.
    /// </summary>
    static bool IsConstraint(string key) =>
        string.Equals(key, "required", StringComparison.Ordinal)
        || string.Equals(key, "enum", StringComparison.Ordinal);

    static string Escape(string token) =>
        token.Contains('~', StringComparison.Ordinal) || token.Contains('/', StringComparison.Ordinal)
            ? token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)
            : token;

    static string Describe(JsonNode? node) =>
        node switch {
            null => "absent",
            JsonObject => "an object",
            JsonArray => "an array",
            _ => node.ToJsonString()
        };
}
