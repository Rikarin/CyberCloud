using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CyberCloud.ResourceManager.Contracts.Registry;

/// <summary>
///     One property a resource type's body may carry, at one api-version.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="JsonPointer" /> is an RFC 6901 JSON Pointer and doubles as
///         <see cref="Error.Target" />.</b> docs/plan/08 § Errors: <i>"<c>target</c> is a JSON Pointer
///         into the request body so the portal can highlight the field."</i> Storing the pointer
///         rather than a dotted path means a validation failure already holds the exact string the
///         error body needs, so there is no second place that could format it differently.
///     </para>
///     <para>
///         <b>Nested objects are separate properties with deeper pointers</b>, not a recursive tree.
///         <c>/properties/sku</c> and <c>/properties/sku/name</c> are two entries. That keeps the
///         model flat — a list an emitter can walk in one pass and a validator can index — and it
///         makes the api-version projection a set membership test rather than a tree walk.
///     </para>
///     <para>
///         ⚠ <b><see cref="Secret" /> labels a property; it does not protect one.</b> Nothing on the
///         write path swaps a secret value for a <see cref="SecretRef" /> before durable state is
///         written. The body travels from <c>ResourceManagerService.WriteAsync</c> to
///         <c>ResourceGrain.SubmitDesiredAsync</c> untransformed — the policy engine is the only hook
///         that may rewrite it and it is secret-blind — so a <c>Secret</c> property lands in the
///         grain's superset in plaintext, and docs/plan/05 § What is deliberately not in either tier
///         says what that costs: <i>"Grain state is JSON in Postgres and in backups. A secret there is
///         a secret in every backup forever."</i> <c>CC1005</c> cannot catch it either — the analyzer
///         matches C# member names, and this value rides inside a JSON string. docs/plan/02 § ADR-010
///         records the gap and puts it on the resource manager rather than on any provider.
///     </para>
///     <para>
///         What the flag does buy is the generated surfaces — an OpenAPI <c>writeOnly</c> with
///         <c>format: password</c>, a masked portal control, a chart annotation — and a real drop on
///         the read path: <c>ResourceManagerService.ReadablePointers</c> withholds the pointer, so a
///         <c>GET</c> and a write's own response never carry the value back. ⚠ That is a projection
///         filter and not confidentiality — the value is still in the superset, and
///         <c>OperationSpec.Desired</c> holds a second copy. <b>So until the substitution exists, don't
///         mark a body property <c>Secret</c>.</b> Take a
///         <see cref="SecretRef" /> from the caller instead, or keep the value out of the API and let
///         the reconciler mint it into Vault — which is what <c>CyberCloud.DBforPostgreSQL/servers</c>
///         does with its bootstrap password, with <c>PostgresSecretTests</c> holding the line for that
///         type.
///     </para>
/// </remarks>
/// <param name="JsonPointer">
///     The RFC 6901 pointer to this property, for example <c>/properties/sku/name</c>. ⚠ Must start
///     with <c>/</c>; the empty pointer would be the whole document, which is not a property.
/// </param>
/// <param name="Kind">What the value must be.</param>
/// <param name="Required">
///     Whether the body must carry it. ⚠ Required means required <i>on a <c>PUT</c></i>. A
///     <c>PATCH</c> is a merge and validates the merged result, not the patch — see
///     <see cref="ResourceSchema.Validate" />.
/// </param>
/// <param name="ReadOnly">
///     Whether the server owns it. A body that sets a read-only property is refused rather than
///     silently ignored, because "I set it and it did not take" is the bug report nobody can act on.
/// </param>
/// <param name="Secret">
///     Whether the value is meant to be treated as a secret. ⚠ It masks the property on the generated
///     surfaces and does nothing else — the write path stores the value in plaintext. Read the remarks
///     before setting it.
/// </param>
/// <param name="Description">
///     What the property means, for the generated OpenAPI, CLI help and portal form. ADR-012's
///     emitters read this; the registry being the single source is what makes the generated help and
///     the runtime validation impossible to disagree.
/// </param>
/// <seealso cref="AllowedValues" />
/// <seealso cref="Nullable" />
/// <seealso cref="Format" />
/// <seealso cref="Widget" />
public readonly record struct SchemaProperty(
    string JsonPointer,
    SchemaKind Kind,
    bool Required = false,
    bool ReadOnly = false,
    bool Secret = false,
    string Description = ""
) {
    /// <summary>The pointer, validated on construction and on <c>with</c>.</summary>
    public string JsonPointer {
        get;
        init => field = EnsurePointer(value);
    } = EnsurePointer(JsonPointer);

    // ── Expressiveness beyond "what JSON kind is it" ───────────────────────────────────────────
    //
    // ⚠ Every member below is init-only rather than a positional parameter, and that is deliberate:
    // a fourteen-parameter positional record is a call site nobody reads, and the six that are
    // positional are the six every property has an opinion about. The rest are opt-in and default to
    // "unconstrained", so a schema written before these existed means exactly what it meant.
    //
    // ⚠ Every one of them is enforced by Validate AND emitted by the generators. A constraint that
    // only reached the document would be documentation; one that only reached the write path would be
    // an undocumented refusal. docs/plan/08 § The provider registry: "the same registry that generates
    // the CLI is the one that validates the request body."

    /// <summary>
    ///     The closed set of values this property accepts, or empty for an open one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The <c>sku.name</c> case: four values, and before this the schema could say only
    ///         "a string". Every generated surface needed it — an OpenAPI <c>enum</c>, a CLI flag whose
    ///         completion is the four, an SDK member typed as a closed set, and docs/plan/20's
    ///         <c>@xui/select</c> — and <see cref="ResourceSchema.Validate" /> could not refuse a fifth
    ///         value, so the API accepted what no reconciler could honour.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Values are compared ordinally and are legal on
    ///         <see cref="SchemaKind.Text" /> only.</b> A numeric enumeration is expressible as
    ///         <see cref="Minimum" />/<see cref="Maximum" /> or is a modelling mistake; comparing
    ///         <c>1.0</c> and <c>1</c> for membership is a question with no answer that survives a
    ///         round trip through a JSON parser.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Removing a value from a published version is a breaking change</b> and
    ///         <c>OpenApiCompatibility.EnumValueRemoved</c> is the gate that says so. Adding one is
    ///         also refused, because an api-version is immutable — a caller's generated client has a
    ///         member per value and a new one is a value it cannot represent.
    ///     </para>
    /// </remarks>
    public ImmutableArray<string> AllowedValues {
        // Normalised on read because `default(SchemaProperty)` skips initialisers, and a default
        // ImmutableArray throws on enumeration rather than reading as empty.
        get => field.IsDefault ? [] : field;
        init => field = value.IsDefault ? [] : value;
    } = [];

    /// <summary>
    ///     What one element of a <see cref="SchemaKind.Array" /> is.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Required on an array and refused on anything else.</b> Before this, an array reached
    ///     the OpenAPI document as <c>items: {}</c>, which an SDK generator renders as
    ///     <c>object[]</c> and a CLI cannot type a repeated flag from. It is a scalar kind: see the
    ///     remarks on <see cref="SchemaKind.Array" /> for why an array of objects is refused outright
    ///     rather than modelled badly.
    ///     <para>
    ///         <see cref="AllowedValues" />, <see cref="Format" />, <see cref="Pattern" /> and the
    ///         bounds apply to <i>each element</i> when the property is an array. That is the only
    ///         reading that makes them useful there and it is what
    ///         <see cref="ResourceSchema.Validate" /> implements.
    ///     </para>
    /// </remarks>
    public SchemaKind ElementKind { get; init; } = SchemaKind.Unknown;

    /// <summary>
    ///     Whether <c>null</c> is an accepted value. Defaults to <see langword="false" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The decision, written down for the first time: every property is non-nullable
    ///         unless it says otherwise, and a nullable property should be rare.</b> The behaviour was
    ///         already this — <c>Matches</c> refused <see cref="JsonValueKind.Null" /> for every kind
    ///         — but it was an accident of a <c>switch</c> rather than a rule anyone had stated, so
    ///         there was no way to opt out and no reason on record for why not.
    ///     </para>
    ///     <para>
    ///         <b>Why opt-in is the right default here specifically.</b> docs/plan/08 § The write path,
    ///         end to end makes <c>PATCH</c> an RFC 7386 merge patch, and in a merge patch
    ///         <c>null</c> is not a value — it is the instruction <i>remove this member</i>. So on a
    ///         <c>PATCH</c> a nullable property's null and a deletion are the same bytes and cannot be
    ///         told apart. Making nullability opt-in keeps that ambiguity confined to the properties
    ///         whose author accepted it, instead of putting it on every property in the platform.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A required property may be nullable</b> — "must be present" and "may be null" are
    ///         different statements, and JSON Schema spells them separately for that reason.
    ///     </para>
    /// </remarks>
    public bool Nullable { get; init; }

    /// <summary>The semantic refinement of a string. <see cref="SchemaFormat.None" /> for none.</summary>
    /// <remarks>⚠ Legal on <see cref="SchemaKind.Text" /> and on an array of text, and nowhere else.</remarks>
    public SchemaFormat Format { get; init; } = SchemaFormat.None;

    /// <summary>The smallest accepted number, inclusive. Numeric kinds only.</summary>
    public double? Minimum { get; init; }

    /// <summary>The largest accepted number, inclusive. Numeric kinds only.</summary>
    public double? Maximum { get; init; }

    /// <summary>The shortest accepted string. Text kinds only.</summary>
    public int? MinLength { get; init; }

    /// <summary>The longest accepted string. Text kinds only.</summary>
    public int? MaxLength { get; init; }

    /// <summary>
    ///     A regular expression the whole string must match, or <c>""</c> for none. Text kinds only.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Anchored implicitly.</b> The pattern is applied to the whole value, so
    ///         <c>[a-z]+</c> means <c>^[a-z]+$</c> — JSON Schema's <c>pattern</c> is a <i>search</i>
    ///         and this is not, because a provider that wrote an unanchored pattern got a validator
    ///         that accepted anything containing a match. The emitted document anchors it explicitly so
    ///         the two readings agree, and <see cref="Matcher" /> is the one place the anchoring is
    ///         spelled.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And matched by the non-backtracking engine, which is a correctness property and
    ///         not a performance one.</b> A declared pattern runs against a caller-supplied string on
    ///         the request path, so it is the classic place a crafted value turns one regular
    ///         expression into a request thread that never returns.
    ///         <see cref="RegexOptions.NonBacktracking" /> is linear in the input by construction, so
    ///         that cannot happen and no time budget is set — the same argument, for the same reason,
    ///         as <c>CyberCloud.Core.Security.SecretShapedText</c>, which runs over attacker-influenced
    ///         log text. The cost is stated rather than hidden: the engine refuses lookarounds,
    ///         backreferences and atomic groups, so a pattern reaching for one is refused by
    ///         <see cref="Incoherences" /> at silo start with a message rather than accepted and then
    ///         run. See <see cref="Matcher" /> for why the budget that used to be here was removed
    ///         (#76).
    ///     </para>
    /// </remarks>
    public string Pattern {
        // Normalised for the same reason AllowedValues is: `default(SchemaProperty)` skips
        // initialisers, and Incoherences reads `.Length` before anything else.
        get => field ?? string.Empty;
        init => field = value ?? string.Empty;
    } = "";

    /// <summary>Which control docs/plan/20's renderer draws. Never a constraint.</summary>
    public WidgetHint Widget { get; init; } = WidgetHint.None;

    /// <summary>
    ///     Whether the value may not change after create — docs/plan/20's <c>x-immutable</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A declaration with no enforcement <i>here</i>, and it says so rather than implying
    ///     otherwise.</b> Enforcing it needs the previous body, which <see cref="ResourceSchema" />
    ///     does not have — <see cref="ResourceSchema.Validate" /> sees one document. It is honoured by
    ///     the portal (disabled after create, with a tooltip) and is what the update path will consult
    ///     when it grows the comparison. It is <i>not</i> <see cref="ReadOnly" />: read-only means the
    ///     server owns it and a caller may never send it; immutable means the caller sets it once.
    /// </remarks>
    public bool Immutable { get; init; }

    /// <summary>
    ///     The server's default, as a JSON literal, or <c>""</c> for none — <c>"32"</c>, <c>"false"</c>,
    ///     <c>"\"eu-central\""</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Raw JSON text rather than a <c>JsonNode</c>, because a <c>JsonNode</c> is mutable
    ///         and this type is a value.</b> Two properties holding the same node would alias, and
    ///         record equality over a mutable reference is equality that changes under you.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Documentation and generator input; the write path does not apply it.</b> A default
    ///         the validator substituted would make the stored body differ from the body sent, and the
    ///         resource grain's desired state is what the reconciler and the drift hash read. What
    ///         <i>is</i> checked is that the default satisfies this property's own constraints — a
    ///         default the schema would reject is a bug that ships as a form nobody can submit, and the
    ///         emitters refuse it.
    ///     </para>
    /// </remarks>
    public string DefaultJson {
        get => field ?? string.Empty;
        init => field = value ?? string.Empty;
    } = "";

    /// <summary>An illustrative value, as a JSON literal, or <c>""</c> for none.</summary>
    /// <remarks>
    ///     Unlike <see cref="DefaultJson" /> an example carries no promise, so it is checked for
    ///     parseability and for kind, and the compatibility diff treats it as prose.
    /// </remarks>
    public string ExampleJson {
        get => field ?? string.Empty;
        init => field = value ?? string.Empty;
    } = "";

    /// <summary>The last segment of <see cref="JsonPointer" /> — the property's own name.</summary>
    public string Name {
        get {
            var slash = JsonPointer.LastIndexOf('/');
            return Unescape(JsonPointer[(slash + 1)..]);
        }
    }

    /// <summary>The pointer to the object this property sits in, or <c>""</c> for a root property.</summary>
    public string ParentPointer {
        get {
            var slash = JsonPointer.LastIndexOf('/');
            return slash <= 0 ? string.Empty : JsonPointer[..slash];
        }
    }

    /// <summary>
    ///     Checks that this property's own declaration is coherent, before any body is validated.
    /// </summary>
    /// <returns>Every problem, empty when the declaration is sound.</returns>
    /// <remarks>
    ///     ⚠ <b>Run at construction, so a nonsense declaration fails at silo start rather than at the
    ///     first request that happens to reach it.</b> "A <see cref="Minimum" /> on a boolean" and
    ///     "an array with no <see cref="ElementKind" />" are provider bugs whose symptom is otherwise a
    ///     constraint that silently does nothing — which is the worst of the three possible outcomes,
    ///     because the document promises it and the write path does not deliver it.
    /// </remarks>
    public ImmutableArray<string> Incoherences() {
        var problems = ImmutableArray.CreateBuilder<string>();
        var value = Kind is SchemaKind.Array ? ElementKind : Kind;

        if (Kind is SchemaKind.Array) {
            switch (ElementKind) {
                case SchemaKind.Unknown:
                    problems.Add(
                        $"'{JsonPointer}' is an array and declares no ElementKind. An array whose element "
                        + "shape is unknown reaches an SDK as 'object[]' and a CLI cannot type a repeated "
                        + "flag from it."
                    );

                    break;

                case SchemaKind.Nested or SchemaKind.Array:
                    problems.Add(
                        $"'{JsonPointer}' declares ElementKind.{ElementKind}, and an array element is a "
                        + "scalar. A nested or nested-array element would need its own pointer space, "
                        + "which is the flat-list property ResourceSchema is built on — see the remarks "
                        + "on SchemaKind.Array."
                    );

                    break;

                default:
                    break;
            }
        } else if (ElementKind is not SchemaKind.Unknown) {
            problems.Add(
                $"'{JsonPointer}' declares ElementKind.{ElementKind} and is a {Describe(Kind)} rather "
                + "than an array. An element kind on a non-array is a constraint nothing would ever "
                + "consult."
            );
        }

        if (!AllowedValues.IsEmpty) {
            if (value is not SchemaKind.Text) {
                problems.Add(
                    $"'{JsonPointer}' declares AllowedValues and is a {Describe(value)}. A closed set is "
                    + "expressible for strings only — comparing 1.0 and 1 for membership has no answer "
                    + "that survives a JSON round trip, and a numeric range is Minimum/Maximum."
                );
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var allowed in AllowedValues) {
                if (!seen.Add(allowed)) {
                    problems.Add(
                        $"'{JsonPointer}' lists the allowed value '{allowed}' twice. The emitted enum "
                        + "would carry it twice and a generated client would declare the member twice."
                    );
                }
            }
        }

        if (Format is not SchemaFormat.None && value is not SchemaKind.Text) {
            problems.Add(
                $"'{JsonPointer}' declares SchemaFormat.{Format} and is a {Describe(value)}. A format "
                + "refines a string."
            );
        }

        if ((Minimum is not null || Maximum is not null)
            && value is not (SchemaKind.Number or SchemaKind.WholeNumber)) {
            problems.Add(
                $"'{JsonPointer}' declares a numeric bound and is a {Describe(value)}."
            );
        }

        if (Minimum is { } low && Maximum is { } high && low > high) {
            problems.Add(
                $"'{JsonPointer}' declares Minimum {Number(low)} above Maximum {Number(high)}, so no "
                + "value satisfies it."
            );
        }

        if ((MinLength is not null || MaxLength is not null || Pattern.Length > 0)
            && value is not SchemaKind.Text) {
            problems.Add($"'{JsonPointer}' declares a string constraint and is a {Describe(value)}.");
        }

        if (MinLength is { } shortest && shortest < 0) {
            problems.Add($"'{JsonPointer}' declares a negative MinLength.");
        }

        if (MinLength is { } floorLength && MaxLength is { } ceilingLength && floorLength > ceilingLength) {
            problems.Add(
                $"'{JsonPointer}' declares MinLength {Int(floorLength)} above MaxLength "
                + $"{Int(ceilingLength)}, so no value satisfies it."
            );
        }

        if (Pattern.Length > 0) {
            // ⚠ The probe builds the SAME matcher the request path runs and keeps it — see Matcher.
            // Compiling the bare pattern here, as this did, checked a different expression from the
            // anchored one Validate builds, and checked it with a wall clock; both are gone.
            try {
                _ = Matcher(Pattern);
            } catch (ArgumentException malformed) {
                problems.Add($"'{JsonPointer}' declares the pattern '{Pattern}', which does not compile: {malformed.Message}");
            } catch (NotSupportedException unsupported) {
                // The non-backtracking engine's refusal, and it is deliberately a declaration-time
                // problem rather than a fallback to the backtracking one. A lookaround, a
                // backreference or an atomic group is exactly the construct whose cost is not linear
                // in the input, and this property is applied to caller-supplied strings — so the
                // answer is "express it without one", not "run it and hope". Every such construct has
                // a boring equivalent for the shapes a schema pattern is actually for; where it does
                // not, the check belongs in the provider's own validation, next to the parser, the way
                // NetworkAddressing.ProblemWith decides what V4Pattern deliberately does not.
                problems.Add(
                    $"'{JsonPointer}' declares the pattern '{Pattern}', which the non-backtracking "
                    + $"engine refuses: {unsupported.Message} A schema pattern runs against a "
                    + "caller-supplied string, so it must be linear in the input — lookarounds, "
                    + "backreferences and atomic groups are not expressible here."
                );
            }
        }

        if (Secret && ReadOnly) {
            problems.Add(
                $"'{JsonPointer}' is both Secret and ReadOnly. Secret means the caller sends it and it "
                + "never comes back; ReadOnly means the caller may not send it. Together they describe a "
                + "property nothing can ever set or read."
            );
        }

        CheckLiteral(nameof(DefaultJson), DefaultJson, problems);
        CheckLiteral(nameof(ExampleJson), ExampleJson, problems);

        return problems.ToImmutable();
    }

    /// <summary>
    ///     Checks a declared literal: that it parses, that it is of this property's kind, and — for a
    ///     default — that this property's own constraints accept it.
    /// </summary>
    void CheckLiteral(string member, string literal, ImmutableArray<string>.Builder problems) {
        if (literal.Length == 0) {
            return;
        }

        JsonElement parsed;
        try {
            using var document = JsonDocument.Parse(literal);
            parsed = document.RootElement.Clone();
        } catch (JsonException malformed) {
            problems.Add(
                $"'{JsonPointer}' declares {member} '{literal}', which is not JSON: {malformed.Message} "
                + "A literal is JSON text — a string default is spelled with its quotes."
            );

            return;
        }

        foreach (var problem in ValueProblems(this, parsed, JsonPointer)) {
            problems.Add(
                $"'{JsonPointer}' declares a {member} this property's own schema rejects: {problem.Message}"
            );
        }
    }

    /// <summary>
    ///     The compiled whole-value matcher for a declared <see cref="Pattern" /> — the one place a
    ///     schema pattern becomes a <see cref="Regex" />.
    /// </summary>
    /// <param name="pattern">A declared <see cref="Pattern" />, unanchored, as the provider wrote it.</param>
    /// <exception cref="ArgumentException">The pattern does not compile.</exception>
    /// <exception cref="NotSupportedException">
    ///     The pattern uses a construct the non-backtracking engine refuses.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THIS REPLACED A 100 ms MATCH TIMEOUT, AND THE REPLACEMENT IS THE POINT (#76).</b>
    ///         The timeout was set on every match — the request-path one <i>and</i> the ones
    ///         <see cref="Incoherences" /> runs while a schema is being <i>declared</i>: the probe
    ///         above, and the check that a declared <c>DefaultJson</c> or <c>ExampleJson</c> satisfies
    ///         its own property. A .NET match timeout is wall-clock, so on a loaded machine a
    ///         trivially-matching pattern can exceed it because the thread was descheduled, not
    ///         because the expression is slow. That made
    ///         <c>ResourceSchema.Of</c> — which providers call from static initialisers and which
    ///         every fixture builder in the test suite calls — a construction that fails on machine
    ///         load. It did:
    ///         <c>ChartAnnotationTests.APointerNoTypeDeclaresAsPlacementIsStillAnOrdinaryChartRow</c>
    ///         went red once with "could not be checked against this property's pattern within the
    ///         time budget" and green on re-run, which is the container-parallelism flake's family
    ///         (#67) — a red that tells you about the host and therefore a red people learn to re-run
    ///         rather than read.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Raising the budget would have been the wrong repair, because a budget was the
    ///         wrong instrument.</b> A time budget is a guess about a hostile input, and a schema is
    ///         <i>declared</i> from text this repository compiled — a provider's own pattern against a
    ///         provider's own default. There is no adversary at declaration time, so the budget bought
    ///         nothing there and cost a flake. Where there <i>is</i> an adversary — a caller's string
    ///         on the request path — the budget was the weaker of the two available answers: it stops
    ///         a catastrophic backtrack after it has already burned 100 ms of a request thread, and it
    ///         does so per request, so an attacker who has found the pattern still gets to buy 100 ms
    ///         of CPU per call. <see cref="RegexOptions.NonBacktracking" /> removes the hazard instead
    ///         of bounding it: the engine is linear in the input by construction, so there is nothing
    ///         to time out and no timeout is set. That is the same instrument, chosen for the same
    ///         reason, as <c>CyberCloud.Core.Security.SecretShapedText</c>, whose patterns run over
    ///         attacker-influenced log text — and the argument that the log canary needed it while a
    ///         schema pattern did not never held: both run over untrusted text.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Cached, and the cache is what makes "it compiled at silo start" mean "it runs at
    ///         request time".</b> <see cref="Incoherences" /> calls this while the schema is being
    ///         declared, so every registered pattern is built and kept before the first request — and
    ///         a pattern the engine refuses fails the process that would have served it rather than
    ///         the first caller who reached the property, which is what
    ///         <see cref="ResourceSchema.Of" /> exists to do. It also keeps the non-backtracking
    ///         engine's construction cost, which is higher than the interpreted engine's, off the
    ///         request path entirely; the static <see cref="Regex" /> overloads this used to call keep
    ///         only <see cref="Regex.CacheSize" /> (15) entries and the registry has more distinct
    ///         patterns than that. ⚠ The key space is bounded because it is provider-authored: a
    ///         <see cref="Pattern" /> comes from a registration compiled into an assembly and never
    ///         from a request, so this dictionary cannot be grown by a caller.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What would make this stale.</b> Checked 2026-09-05 against every
    ///         <see cref="Pattern" /> then registered — character classes, alternation and bounded
    ///         quantifiers throughout, and the non-backtracking engine took all of them. Two things
    ///         would reopen the question. One: a provider with a genuine need for a lookaround or a
    ///         backreference, which is now a hard refusal at silo start rather than a slow match — the
    ///         answer is a check in that provider's own validation next to its parser, and only if
    ///         that is impossible is the engine choice worth revisiting, because the alternative is a
    ///         time budget over caller-supplied text again. Two: a runtime in which
    ///         <see cref="RegexOptions.NonBacktracking" /> stops being linear in the input, at which
    ///         point the argument above is void and this member is where the replacement goes.
    ///     </para>
    /// </remarks>
    internal static Regex Matcher(string pattern) =>
        Matchers.GetOrAdd(
            pattern,
            // Anchored, because a declared pattern is a whole-value match and not a search — see the
            // remarks on Pattern. ⚠ The emitters write their own `^(?:…)$` into the documents they
            // produce (OpenApiEmitter, ChartAnnotationEmitter); this is the runtime's copy of that one
            // decision, and the two are kept honest by SchemaExpressivenessTests and the emitter
            // suites asserting the same anchoring rather than by sharing a string.
            static x => new Regex("^(?:" + x + ")$", RegexOptions.NonBacktracking)
        );

    static readonly ConcurrentDictionary<string, Regex> Matchers = new(StringComparer.Ordinal);

    static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    static string Describe(SchemaKind kind) => ResourceSchema.Describe(kind);

    static IEnumerable<Error> ValueProblems(SchemaProperty property, JsonElement value, string pointer) =>
        ResourceSchema.ValueProblems(property, value, pointer);

    static string EnsurePointer(string jsonPointer) {
        if (string.IsNullOrEmpty(jsonPointer) || jsonPointer[0] != '/') {
            throw new ArgumentException(
                $"'{jsonPointer}' is not a property pointer. A schema property is addressed by an RFC "
                + "6901 JSON Pointer beginning with '/', for example '/properties/sku/name'. The "
                + "empty pointer addresses the whole document, which is not a property — "
                + "docs/plan/08 § Errors.",
                nameof(jsonPointer)
            );
        }

        return jsonPointer;
    }

    static string Unescape(string token) =>
        token.Contains('~', StringComparison.Ordinal)
            ? token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)
            : token;
}

/// <summary>
///     One resource type's body shape at one api-version — the thing docs/plan/08 § The provider
///     registry calls <c>schema:</c>, and the thing ADR-012's emitters read.
/// </summary>
/// <remarks>
///     <para>
///         <b>This object validates and generates, and that identity is the point.</b>
///         docs/plan/08 § The provider registry:
///         <i>
///             "This object is the source for the five generated surfaces (ADR-012) <b>and</b> for
///             the runtime write path — the same registry that generates the CLI is the one that
///             validates the request body. That identity is what makes drift impossible rather than
///             merely detectable."
///         </i>
///         A schema stored as opaque JSON Schema text would satisfy the first half and not the
///         second: an emitter would have to re-parse and re-interpret it, and a second interpretation
///         is exactly where drift comes back.
///     </para>
///     <para>
///         ⚠ <b>All five of ADR-012's surfaces read this type, and every member on
///         <see cref="SchemaProperty" /> reaches the first four.</b> A property's pointer, kind,
///         requiredness, closed set, element kind, nullability, format, bounds, widget hint, default
///         and example become an OpenAPI schema
///         (<c>CyberCloud.ResourceManager.Contracts.Generation.OpenApiEmitter</c>), a <c>cyc</c> flag
///         (<c>CliEmitter</c>), an SDK member (<c>SdkEmitter</c>) and a portal control
///         (<c>FormsEmitter</c>). Adding a member here without teaching those to read it is the
///         failure to watch for: a registry field nothing reads is not a closed gap, it is a field.
///     </para>
///     <para>
///         ⚠ <b>The fifth surface reaches <i>most</i> of them, and refuses the rest out loud.</b>
///         <c>ChartAnnotationEmitter</c> writes a managed chart's <c>@param</c> block, and the chart
///         annotation vocabulary has no syntax for <see cref="SchemaProperty.Format" />,
///         <see cref="SchemaProperty.Pattern" />, <see cref="SchemaProperty.MinLength" />,
///         <see cref="SchemaProperty.MaxLength" />, <see cref="SchemaProperty.ExampleJson" />,
///         <see cref="SchemaProperty.Nullable" /> or a non-text
///         <see cref="SchemaProperty.ElementKind" /> — docs/plan/02 § ADR-010 names those as the gap
///         that runs registry-to-chart. Each is a generation failure naming the property rather than a
///         silent drop, which is the one outcome that would put the failure this paragraph warns about
///         back in play.
///     </para>
///     <para>
///         ⚠ <b>The projection is the other half of the immutable-date rule, and it does not live
///         here.</b> The grain's state is a superset of every version's properties; a read at an old
///         version keeps exactly the properties that version declared and drops the rest, which is what
///         stops an SDK generated against <c>2026-08-01</c> from receiving a field it has no member
///         for. <c>ResourceGrain.Project</c> runs it, over the pointer list
///         <c>ResourceManagerService</c> hands down — this type supplies
///         <see cref="Properties" /> and <see cref="Declares" /> and nothing that walks a document.
///     </para>
/// </remarks>
public sealed record ResourceSchema {
    /// <summary>A schema with no properties — every body validates, nothing projects through.</summary>
    /// <remarks>
    ///     ⚠ Useful only for a type whose body is genuinely empty. A type that has not had its schema
    ///     written yet should not use this: an empty schema accepts anything and projects nothing, so
    ///     a read at that version comes back blank rather than failing loudly.
    /// </remarks>
    public static ResourceSchema Empty { get; } = new();

    /// <summary>The properties, in declaration order.</summary>
    public ImmutableArray<SchemaProperty> Properties { get; init; } = [];

    /// <summary>
    ///     Whether a property the schema does not declare is refused. Defaults to refusing.
    /// </summary>
    /// <remarks>
    ///     ⚠ Refusing is the right default and it is the opposite of what most JSON Schemas do.
    ///     An unknown property is nearly always a typo (<c>storageGB</c> for <c>storageGb</c>) or an
    ///     SDK from a newer version talking to an older one. Silently dropping it produces a resource
    ///     that is not what the caller asked for and reports success, which is the failure mode a
    ///     control plane can least afford. Set this to <c>false</c> only for a type that deliberately
    ///     carries a free-form bag.
    /// </remarks>
    public bool RejectsUnknownProperties { get; init; } = true;

    /// <summary>Builds a schema from a property list.</summary>
    /// <param name="properties">
    ///     The properties. Duplicate pointers are a bug and throw, as is a property whose own
    ///     declaration does not cohere — see <see cref="SchemaProperty.Incoherences" />.
    /// </param>
    /// <exception cref="ArgumentException">
    ///     Two properties share a pointer, a property declares <see cref="TagRules.JsonPointer" />, or a
    ///     property's constraints contradict its kind.
    /// </exception>
    /// <remarks>
    ///     ⚠ <b>Every check here runs at silo start, because <c>Describe</c> runs at silo start.</b> A
    ///     provider whose schema is nonsense fails the process that would have served it rather than
    ///     the first request that reached the property — which is the difference between a deploy that
    ///     does not roll out and a tenant finding it.
    /// </remarks>
    public static ResourceSchema Of(params ImmutableArray<SchemaProperty> properties) {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var property in properties) {
            if (!seen.Add(property.JsonPointer)) {
                throw new ArgumentException(
                    $"'{property.JsonPointer}' is declared twice. One pointer names one property, and a "
                    + "duplicate would make validation depend on which entry the validator reached "
                    + "first.",
                    nameof(properties)
                );
            }

            // ⚠ The tag bag belongs to the platform, not to a provider. Its shape is TagRules', it is
            // emitted for every type that declares SupportsTags, and a provider that declared its own
            // would produce two descriptions of one property — the drift ADR-012 exists to remove.
            if (property.JsonPointer.Equals(TagRules.JsonPointer, StringComparison.Ordinal)
                || property.JsonPointer.StartsWith(TagRules.JsonPointer + "/", StringComparison.Ordinal)) {
                throw new ArgumentException(
                    $"'{property.JsonPointer}' is under '{TagRules.JsonPointer}', which is the platform's tag "
                    + "bag and not a provider's property. Declare IResourceTypeBuilder.SupportsTags() "
                    + "instead — docs/plan/06 § Tags, locks — and the shape is emitted and validated for "
                    + "you.",
                    nameof(properties)
                );
            }

            problems.AddRange(property.Incoherences());
        }

        if (problems.Count > 0) {
            throw new ArgumentException(
                $"{problems.Count.ToString(CultureInfo.InvariantCulture)} incoherent property "
                + "declaration(s): " + string.Join(" ", problems),
                nameof(properties)
            );
        }

        return new() { Properties = properties };
    }

    /// <summary>
    ///     Validates a body. Step 2 of docs/plan/08 § The write path, end to end.
    /// </summary>
    /// <param name="body">The parsed request body.</param>
    /// <param name="requireRequired">
    ///     Whether missing required properties are a failure. <c>true</c> for a <c>PUT</c>, which is a
    ///     full replacement; <c>false</c> when validating a <c>PATCH</c> document on its own — a merge
    ///     patch legitimately omits everything it is not changing, and the merged result is validated
    ///     with <c>true</c> afterwards.
    /// </param>
    /// <param name="allowTags">
    ///     Whether a root-level <c>tags</c> object is accepted. Pass the type's
    ///     <c>SupportsTags</c> — see the remarks.
    /// </param>
    /// <returns>
    ///     Success, or an <see cref="ErrorCode.InvalidRequestBody" /> whose
    ///     <see cref="Error.Target" /> is the offending property's pointer and whose
    ///     <see cref="Error.Details" /> carries every other problem found.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every problem is reported, not just the first.</b> A portal form that has to be
    ///         fixed one field per round trip is a form nobody finishes. The first problem is the
    ///         top-level error so the response still has one <c>target</c> to highlight; the rest are
    ///         <see cref="Error.Details" />, which is what that field is for (docs/plan/08 § Errors).
    ///     </para>
    ///     <para>
    ///         ⚠ <b><paramref name="allowTags" /> exists because <c>SupportsTags</c> was otherwise
    ///         undeliverable, and the first provider found that out.</b>
    ///         <c>IResourceTypeBuilder.SupportsTags</c> declares that a type carries tags, and the
    ///         write path reads them out of a root-level <c>tags</c> object — but a schema declares
    ///         only the type's own properties, and <see cref="RejectsUnknownProperties" /> defaults to
    ///         refusing, so <c>/tags</c> was rejected as an unknown property before the write path ever
    ///         looked at it. Every declaring provider would have had to add <c>/tags</c> to <i>every</i>
    ///         api-version's schema by hand: platform boilerplate copied twenty times, which is exactly
    ///         the failure docs/plan/25 § R1 describes.
    ///     </para>
    ///     <para>
    ///         The flag rather than an unconditional exemption, because
    ///         <c>IResourceTypeBuilder.SupportsTags</c>' own remarks require the other half: <i>"A type
    ///         that does not declare this refuses a body with tags rather than accepting and dropping
    ///         them."</i> Both branches are now real.
    ///     </para>
    /// </remarks>
    public Result Validate(JsonElement body, bool requireRequired = true, bool allowTags = false) {
        var problems = new List<Error>();

        if (body.ValueKind != JsonValueKind.Object) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"The request body is a JSON {Describe(body.ValueKind)} and a resource body is a JSON "
                + "object.",
                ""
            );
        }

        foreach (var property in Properties) {
            var found = TryResolve(body, property.JsonPointer, out var value);

            if (!found) {
                if (requireRequired && property.Required) {
                    problems.Add(
                        new(
                            ErrorCode.InvalidRequestBody,
                            $"'{property.JsonPointer}' is required and is missing.",
                            property.JsonPointer
                        )
                    );
                }

                continue;
            }

            if (property.ReadOnly) {
                problems.Add(
                    new(
                        ErrorCode.InvalidRequestBody,
                        $"'{property.JsonPointer}' is set by the server and cannot be written. It is "
                        + "refused rather than ignored, so that a caller never gets a success for a "
                        + "value that did not take.",
                        property.JsonPointer
                    )
                );

                continue;
            }

            problems.AddRange(ValueProblems(property, value, property.JsonPointer));
        }

        if (allowTags) {
            problems.AddRange(TagProblems(body));
        }

        if (RejectsUnknownProperties) {
            problems.AddRange(UnknownProperties(body, allowTags));
        }

        if (problems.Count == 0) {
            return Result.Success;
        }

        var head = problems[0];
        return Result.Failure(
            problems.Count == 1
                ? head
                : head.WithDetails([.. problems.Skip(1)])
        );
    }

    // ⚠ THERE IS NO Project HERE, AND THAT IS THE DECISION RATHER THAN AN OMISSION.
    //
    // A schema-shaped Project(JsonObject) lived here and nothing but its own tests ever called it: the
    // projection a real GET runs is ResourceGrain.Project, which takes a flat pointer list because the
    // grain must not depend on the registry (see ResourceWriteSubmission.DeclaredPointers). Two
    // implementations of one rule is how the secret drop came to exist in the one nobody reached, so
    // the reachable one is now the only one. ResourceManagerService.ReadablePointers is what decides
    // which pointers it gets, and WritePathTests is where the behaviour is asserted.

    /// <summary>Whether this schema declares <paramref name="jsonPointer" />.</summary>
    /// <param name="jsonPointer">An RFC 6901 pointer.</param>
    public bool Declares(string jsonPointer) {
        foreach (var property in Properties) {
            if (string.Equals(property.JsonPointer, jsonPointer, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    // ── Pointer machinery ──────────────────────────────────────────────────────────────────────

    /// <summary>Reads a pointer out of a <see cref="JsonElement" />.</summary>
    static bool TryResolve(JsonElement root, string jsonPointer, out JsonElement value) {
        value = root;

        foreach (var token in Tokens(jsonPointer)) {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(token, out var next)) {
                value = default;
                return false;
            }

            value = next;
        }

        return true;
    }

    /// <summary>
    ///     The reference tokens of an RFC 6901 pointer, with <c>~1</c> and <c>~0</c> unescaped.
    /// </summary>
    static IEnumerable<string> Tokens(string jsonPointer) {
        foreach (var raw in jsonPointer.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            yield return raw.Contains('~', StringComparison.Ordinal)
                ? raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)
                : raw;
        }
    }

    /// <summary>Escapes one property name into a reference token.</summary>
    static string Escape(string name) =>
        name.Contains('~', StringComparison.Ordinal) || name.Contains('/', StringComparison.Ordinal)
            ? name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)
            : name;

    /// <summary>
    ///     Walks the body and reports every leaf whose pointer this schema does not declare.
    /// </summary>
    /// <remarks>
    ///     ⚠ An <i>object</i> that is not declared is reported once, at the object, rather than once
    ///     per leaf inside it. A caller who sent a whole unrecognised section wants one message
    ///     naming it, not forty naming its contents.
    /// </remarks>
    List<Error> UnknownProperties(JsonElement body, bool allowTags) {
        var problems = new List<Error>();
        Walk(body, string.Empty, problems, allowTags);
        return problems;
    }

    /// <summary>The root-level <c>tags</c> object — docs/plan/06 § Tags, locks.</summary>
    /// <remarks>
    ///     ⚠ <see cref="TagRules" /> now owns this, and owns the emitted shape with it, so the
    ///     validator and the generated document cannot describe different bags.
    /// </remarks>
    const string TagsPointer = TagRules.JsonPointer;

    /// <summary>
    ///     Checks the shape of a tag bag, for a type that declares <c>SupportsTags</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Shape only. The cap of 50 pairs and the key and value rules are the resource grain's, and
    ///     they stay there because they apply to the <i>merged</i> tag set rather than to one request's
    ///     — a <c>PATCH</c> adding one tag to forty-nine is the request that crosses the cap.
    /// </remarks>
    static List<Error> TagProblems(JsonElement body) {
        var problems = new List<Error>();

        if (!body.TryGetProperty("tags", out var tags)) {
            return problems;
        }

        if (tags.ValueKind != JsonValueKind.Object) {
            problems.Add(
                new(
                    ErrorCode.InvalidRequestBody,
                    $"'{TagsPointer}' must be an object of string values and is a {Describe(tags.ValueKind)}.",
                    TagsPointer
                )
            );

            return problems;
        }

        foreach (var tag in tags.EnumerateObject()) {
            if (tag.Value.ValueKind != JsonValueKind.String) {
                problems.Add(
                    new(
                        ErrorCode.InvalidRequestBody,
                        // ⚠ Refused rather than coerced. The write path keeps only string values, so a
                        // number here would be silently dropped — and "I set that tag and it did not
                        // take" is the bug report nobody can act on.
                        $"'{TagsPointer}/{tag.Name}' must be a string and is a {Describe(tag.Value.ValueKind)}.",
                        TagsPointer + "/" + tag.Name
                    )
                );
            }
        }

        return problems;
    }

    void Walk(JsonElement node, string prefix, List<Error> problems, bool allowTags) {
        if (node.ValueKind != JsonValueKind.Object) {
            return;
        }

        foreach (var member in node.EnumerateObject()) {
            var jsonPointer = prefix + "/" + Escape(member.Name);

            // The tag bag is free-form by design, so neither it nor its members are walked.
            if (allowTags && string.Equals(jsonPointer, TagsPointer, StringComparison.Ordinal)) {
                continue;
            }

            if (!Declares(jsonPointer)) {
                problems.Add(
                    new(
                        ErrorCode.InvalidRequestBody,
                        $"'{jsonPointer}' is not a property of this resource type at this api-version. "
                        + "An unknown property is refused rather than dropped: silently ignoring it "
                        + "produces a resource that is not what was asked for and reports success. "
                        + "docs/plan/08 § The provider registry.",
                        jsonPointer
                    )
                );

                continue;
            }

            Walk(member.Value, jsonPointer, problems, allowTags);
        }
    }

    // ── Checking one value against one property ────────────────────────────────────────────────

    /// <summary>
    ///     Everything wrong with one value: its kind, its nullability, and every constraint the
    ///     property declares.
    /// </summary>
    /// <param name="property">The declaring property.</param>
    /// <param name="value">The value found at its pointer.</param>
    /// <param name="target">
    ///     The pointer to report. ⚠ Not always <c>property.JsonPointer</c> — an array's elements are
    ///     reported at <c>…/0</c>, <c>…/1</c>, which is what lets a portal highlight the failing row.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>This is the one place a value is judged, and it is shared with
    ///     <see cref="SchemaProperty.Incoherences" />.</b> A declared default is checked by running it
    ///     through this method, so "what the write path refuses" and "what a default may be" are the
    ///     same predicate by construction rather than by two lists that agree today.
    /// </remarks>
    internal static IEnumerable<Error> ValueProblems(SchemaProperty property, JsonElement value, string target) {
        if (value.ValueKind is JsonValueKind.Null) {
            if (!property.Nullable) {
                yield return new(
                    ErrorCode.InvalidRequestBody,
                    $"'{target}' is null and is not nullable. Every property is non-nullable unless it "
                    + "declares otherwise — see the remarks on SchemaProperty.Nullable, which also "
                    + "explain why a merge patch makes null mean 'remove this member'.",
                    target
                );
            }

            // A null that is allowed is a null: no constraint below applies to it.
            yield break;
        }

        if (!Matches(property.Kind, value)) {
            yield return new(
                ErrorCode.InvalidRequestBody,
                $"'{target}' must be a {Describe(property.Kind)} and is a {Describe(value.ValueKind)}.",
                target
            );

            yield break;
        }

        if (property.Kind is SchemaKind.Array) {
            var index = 0;

            foreach (var element in value.EnumerateArray()) {
                // The element carries the array's constraints and the element's own kind. ⚠ Nullability
                // is NOT inherited: `Nullable` on an array says the array may be null, and it was
                // already consumed above. `[null]` is a null element, which is a separate declaration
                // this model does not have — so it is refused, which is the safe direction.
                var elementProperty = property with {
                    Kind = property.ElementKind,
                    ElementKind = SchemaKind.Unknown,
                    Nullable = false
                };

                foreach (var problem in ValueProblems(
                             elementProperty,
                             element,
                             target + "/" + index.ToString(CultureInfo.InvariantCulture)
                         )) {
                    yield return problem;
                }

                index++;
            }

            yield break;
        }

        foreach (var problem in ConstraintProblems(property, value, target)) {
            yield return problem;
        }
    }

    static IEnumerable<Error> ConstraintProblems(SchemaProperty property, JsonElement value, string target) {
        if (value.ValueKind is JsonValueKind.String) {
            var text = value.GetString() ?? string.Empty;

            if (!property.AllowedValues.IsEmpty && !property.AllowedValues.Contains(text, StringComparer.Ordinal)) {
                yield return new(
                    ErrorCode.InvalidRequestBody,
                    // The list is in the message rather than only in the document: docs/plan/08 § Errors
                    // requires a message that "names the actual numbers", and a caller who guessed a sku
                    // needs the four, not a "no".
                    $"'{target}' is '{text}' and this property accepts only "
                    + $"[{string.Join(", ", property.AllowedValues)}].",
                    target
                );
            }

            if (property.MinLength is { } shortest && text.Length < shortest) {
                yield return new(
                    ErrorCode.InvalidRequestBody,
                    $"'{target}' is {text.Length.ToString(CultureInfo.InvariantCulture)} character(s) long "
                    + $"and the minimum is {shortest.ToString(CultureInfo.InvariantCulture)}.",
                    target
                );
            }

            if (property.MaxLength is { } longest && text.Length > longest) {
                yield return new(
                    ErrorCode.InvalidRequestBody,
                    $"'{target}' is {text.Length.ToString(CultureInfo.InvariantCulture)} character(s) long "
                    + $"and the maximum is {longest.ToString(CultureInfo.InvariantCulture)}.",
                    target
                );
            }

            if (property.Pattern.Length > 0 && PatternProblem(property.Pattern, text) is { } mismatch) {
                yield return new(ErrorCode.InvalidRequestBody, $"'{target}' {mismatch}", target);
            }

            if (FormatProblem(property.Format, text) is { } explanation) {
                yield return new(
                    ErrorCode.InvalidRequestBody,
                    $"'{target}' is not a valid {SchemaVocabulary.Of(property.Format)}: {explanation}",
                    target
                );
            }

            yield break;
        }

        if (value.ValueKind is not JsonValueKind.Number) {
            yield break;
        }

        var number = value.GetDouble();

        if (property.Minimum is { } low && number < low) {
            yield return new(
                ErrorCode.InvalidRequestBody,
                $"'{target}' is {number.ToString("R", CultureInfo.InvariantCulture)} and the minimum is "
                + $"{low.ToString("R", CultureInfo.InvariantCulture)}.",
                target
            );
        }

        if (property.Maximum is { } high && number > high) {
            yield return new(
                ErrorCode.InvalidRequestBody,
                $"'{target}' is {number.ToString("R", CultureInfo.InvariantCulture)} and the maximum is "
                + $"{high.ToString("R", CultureInfo.InvariantCulture)}.",
                target
            );
        }
    }

    /// <summary>
    ///     What is wrong with a string under a declared pattern, or <see langword="null" /> when
    ///     nothing is.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the request-path match — <paramref name="value" /> came out of a caller's
    ///         body — and it has no time budget because it no longer needs one (#76).</b>
    ///         <see cref="SchemaProperty.Matcher" /> builds the matcher with
    ///         <see cref="RegexOptions.NonBacktracking" />, which is linear in the input by
    ///         construction; the catastrophic backtrack the old 100 ms budget existed to cut short
    ///         cannot happen, so there is nothing to cut short. The argument for the engine, and why
    ///         it is a better answer than the budget was, is on <see cref="SchemaProperty.Matcher" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And therefore there is no "we could not tell" branch left.</b> The old one refused
    ///         the value on a timeout — correctly, since the alternative would have turned one crafted
    ///         string into a way past every pattern in the platform — but it was reachable by a busy
    ///         host as well as by a hostile string, so a tenant's valid value could be refused because
    ///         the silo was under load. The answer is now the same one on every machine.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing is caught here.</b> A pattern that does not compile, or that the engine
    ///         refuses, is a problem <see cref="SchemaProperty.Incoherences" /> reports and
    ///         <see cref="ResourceSchema.Of" /> throws on at silo start. Reaching this method with one
    ///         means a schema was built past <c>Of</c> — an object initialiser in a test — and that is
    ///         a bug in this tree, which announces itself by throwing rather than by quietly becoming
    ///         a <c>400</c> aimed at a caller who did nothing wrong.
    ///     </para>
    /// </remarks>
    static string? PatternProblem(string pattern, string value) =>
        SchemaProperty.Matcher(pattern).IsMatch(value)
            ? null
            : $"does not match '{pattern}', which is applied to the whole value.";

    /// <summary>
    ///     What is wrong with a string under a format, or <see langword="null" /> when nothing is.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Each check delegates to the parser the rest of the platform uses</b> rather than to a
    ///     regular expression written here. A GUID is checked with the <c>D</c>-form rule
    ///     <see cref="GuidFormat" /> fixes, and a resource id with
    ///     <see cref="ResourceId.TryParsePath" /> — the same one the gateway parses paths with. A
    ///     second implementation of either would be a second opinion about what the platform accepts.
    /// </remarks>
    static string? FormatProblem(SchemaFormat format, string value) =>
        format switch {
            SchemaFormat.None => null,
            SchemaFormat.Uuid => Guid.TryParseExact(value, "D", out _)
                ? null
                : "a uuid is the hyphenated 'D' form, so that one resource has exactly one spelling.",
            SchemaFormat.DateTime => DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _
            )
                ? null
                : "a timestamp is RFC 3339 with an offset; a local time has no meaning on the wire.",
            SchemaFormat.Uri => IsAbsoluteUri(value)
                ? null
                : "a uri is absolute and carries its scheme. A relative one is refused because a "
                + "request body is not resolved against a base.",
            SchemaFormat.Email => IsEmail(value)
                ? null
                : "an address is 'local@domain.tld'. Shape only — deliverability is not checked here.",
            SchemaFormat.Region => ResourceNaming.IsValid(value)
                ? null
                : "a region is spelled like every other platform name — lower-case letters, digits and "
                + "hyphens, for example 'eu-central'.",
            SchemaFormat.ResourceId => ResourceId.TryParsePath(value, out _)
                ? null
                : "a resource id is the full path, parsed by the same reader the gateway uses — "
                + "docs/plan/06 § Identifiers.",
            _ => null
        };

    /// <summary>
    ///     Whether a string is an absolute URI that <i>says</i> its scheme.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><see cref="Uri.TryCreate(string,UriKind,out Uri)" /> alone is not this check, and the
    ///     difference is platform-dependent.</b> On Unix it parses <c>/relative</c> as an absolute
    ///     <c>file:</c> URI and returns <see langword="true" />, so a validator built on it would
    ///     accept a relative path on Linux and refuse it on Windows — the same body, two answers,
    ///     depending on which silo took the request. Requiring the original string to begin with the
    ///     scheme it parsed to removes the implicit one.
    /// </remarks>
    static bool IsAbsoluteUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && value.StartsWith(uri.Scheme + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Shape only, and deliberately not RFC 5322. An address that parses is not an address that
    ///     exists, and the only check worth anything is a delivered message — docs/plan/17.
    /// </summary>
    static bool IsEmail(string value) {
        var at = value.IndexOf('@', StringComparison.Ordinal);

        if (at <= 0 || at != value.LastIndexOf('@') || at == value.Length - 1) {
            return false;
        }

        var domain = value[(at + 1)..];
        return domain.Contains('.', StringComparison.Ordinal)
               && domain[0] != '.'
               && domain[^1] != '.'
               && !value.Contains(' ', StringComparison.Ordinal);
    }

    static bool Matches(SchemaKind kind, JsonElement value) =>
        kind switch {
            SchemaKind.Text => value.ValueKind is JsonValueKind.String,
            SchemaKind.Number => value.ValueKind is JsonValueKind.Number,
            SchemaKind.WholeNumber => value.ValueKind is JsonValueKind.Number && value.TryGetInt64(out _),
            SchemaKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            SchemaKind.Nested => value.ValueKind is JsonValueKind.Object,
            SchemaKind.Array => value.ValueKind is JsonValueKind.Array,
            _ => false
        };

    internal static string Describe(SchemaKind kind) =>
        kind switch {
            SchemaKind.Text => "string",
            SchemaKind.Number => "number",
            SchemaKind.WholeNumber => "integer",
            SchemaKind.Boolean => "boolean",
            SchemaKind.Nested => "object",
            SchemaKind.Array => "array",
            _ => kind.ToString().ToLowerInvariant()
        };

    static string Describe(JsonValueKind kind) =>
        kind switch {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.Null => "null",
            _ => kind.ToString().ToLowerInvariant()
        };
}
