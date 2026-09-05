using System.Collections.Immutable;
using System.Text.Json;

namespace CyberCloud.ResourceManager.Contracts.Tests;

/// <summary>
///     Every fact the registry gained, checked at the <i>write path</i>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This file exists because expressiveness without enforcement is documentation.</b>
///         docs/plan/08 § The provider registry: <i>"the same registry that generates the CLI is the
///         one that validates the request body. That identity is what makes drift impossible rather
///         than merely detectable."</i> A declaration the emitter renders and
///         <see cref="ResourceSchema.Validate" /> ignores would satisfy the first half and break the
///         claim — the API would publish a constraint it does not apply, which is worse than
///         publishing none.
///     </para>
///     <para>
///         The other half — that each of these reaches the emitted document — is
///         <c>Generation.OpenApiEmitterTests</c>. Both halves, for every member, is the whole point.
///     </para>
/// </remarks>
public sealed class SchemaExpressivenessTests {
    // ⚠ ImmutableArray rather than an array, and it matters at the call site: with `params T[]` the
    // compiler targets `new(…)` at the array type and every `{ AllowedValues = … }` fails to bind.
    static ResourceSchema Schema(params ImmutableArray<SchemaProperty> properties) =>
        ResourceSchema.Of(properties);

    static Result Validate(ResourceSchema schema, string body) {
        using var document = JsonDocument.Parse(body);
        return schema.Validate(document.RootElement);
    }

    // ── 1. Enumerations ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADeclaredValueIsAccepted() {
        var schema = Schema(new SchemaProperty("/sku", SchemaKind.Text) { AllowedValues = ["small", "large"] });

        Validate(schema, """{"sku":"large"}""").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void AValueOutsideTheEnumerationIsRefusedAndTheMessageNamesTheSet() {
        var schema = Schema(new SchemaProperty("/sku", SchemaKind.Text) { AllowedValues = ["small", "large"] });

        var validated = Validate(schema, """{"sku":"enormous"}""");

        validated.IsFailure.ShouldBeTrue();
        validated.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        validated.Error.Target.ShouldBe("/sku");
        // docs/plan/08 § Errors: a message "names the actual numbers". A caller who guessed a sku
        // needs the list, not a "no".
        validated.Error.Message.ShouldContain("small");
        validated.Error.Message.ShouldContain("large");
    }

    [Fact]
    public void AnEnumerationIsOrdinalSoCaseMatters() =>
        Validate(
            Schema(new SchemaProperty("/sku", SchemaKind.Text) { AllowedValues = ["small"] }),
            """{"sku":"Small"}"""
        ).IsFailure.ShouldBeTrue();

    [Fact]
    public void AnEnumerationOnANonStringIsRefusedAtDeclarationTime() =>
        // ⚠ At silo start, not at the first request: Describe runs once and a nonsense declaration
        // should fail the process that would have served it.
        Should.Throw<ArgumentException>(
            () => Schema(new SchemaProperty("/count", SchemaKind.WholeNumber) { AllowedValues = ["1", "2"] })
        ).Message.ShouldContain("AllowedValues");

    // ── 2. Array element shape ─────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryElementMustBeTheDeclaredKind() {
        var schema = Schema(new SchemaProperty("/ports", SchemaKind.Array) { ElementKind = SchemaKind.WholeNumber });

        Validate(schema, """{"ports":[1,2,3]}""").IsSuccess.ShouldBeTrue();

        var validated = Validate(schema, """{"ports":[1,"two"]}""");
        validated.IsFailure.ShouldBeTrue();
        // ⚠ The failing element, not the array. A portal that has to highlight a row needs the index.
        validated.Error!.Target.ShouldBe("/ports/1");
    }

    [Fact]
    public void AnElementCarriesTheArraysOwnConstraints() {
        var schema = Schema(new SchemaProperty("/tiers", SchemaKind.Array) {
            ElementKind = SchemaKind.Text,
            AllowedValues = ["a", "b"]
        });

        Validate(schema, """{"tiers":["a","b","a"]}""").IsSuccess.ShouldBeTrue();
        Validate(schema, """{"tiers":["a","c"]}""").Error!.Target.ShouldBe("/tiers/1");
    }

    [Fact]
    public void AnArrayWithNoElementKindIsRefusedAtDeclarationTime() =>
        Should.Throw<ArgumentException>(() => Schema(new SchemaProperty("/ports", SchemaKind.Array)))
            .Message.ShouldContain("ElementKind");

    [Fact]
    public void AnArrayOfObjectsIsRefusedRatherThanHalfModelled() =>
        // See the remarks on SchemaKind.Array: an element schema needs its own pointer space, and the
        // flat list is what makes Validate an index rather than a tree walk.
        Should.Throw<ArgumentException>(
            () => Schema(new SchemaProperty("/rules", SchemaKind.Array) { ElementKind = SchemaKind.Nested })
        ).Message.ShouldContain("scalar");

    // ── 3. Nullability ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void APropertyIsNonNullableUnlessItSaysOtherwise() {
        var validated = Validate(Schema(new SchemaProperty("/note", SchemaKind.Text)), """{"note":null}""");

        validated.IsFailure.ShouldBeTrue();
        validated.Error!.Message.ShouldContain("not nullable");
    }

    [Fact]
    public void ADeclaredNullableAcceptsNull() =>
        Validate(
            Schema(new SchemaProperty("/note", SchemaKind.Text) { Nullable = true }),
            """{"note":null}"""
        ).IsSuccess.ShouldBeTrue();

    [Fact]
    public void ANullableValueSkipsTheConstraintsRatherThanFailingThem() =>
        // A null is a null: a minLength cannot be applied to the absence of a string, and reporting
        // "null is shorter than 5" would be a message nobody can act on.
        Validate(
            Schema(new SchemaProperty("/note", SchemaKind.Text) { Nullable = true, MinLength = 5 }),
            """{"note":null}"""
        ).IsSuccess.ShouldBeTrue();

    [Fact]
    public void ANullableArrayDoesNotMakeItsElementsNullable() =>
        // `Nullable` on an array says the array may be null, and that was already consumed. A null
        // element is a declaration this model does not have, so it is refused — the safe direction.
        Validate(
            Schema(new SchemaProperty("/ports", SchemaKind.Array) { ElementKind = SchemaKind.WholeNumber, Nullable = true }),
            """{"ports":[1,null]}"""
        ).Error!.Target.ShouldBe("/ports/1");

    [Fact]
    public void ARequiredPropertyMayAlsoBeNullable() =>
        // "Must be present" and "may be null" are different statements, and JSON Schema spells them
        // separately for that reason.
        Validate(
            Schema(new SchemaProperty("/note", SchemaKind.Text, Required: true) { Nullable = true }),
            """{"note":null}"""
        ).IsSuccess.ShouldBeTrue();

    // ── 4. Formats ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SchemaFormat.Uuid, "\"7b6a5c4d-0000-4000-8000-000000000001\"", true)]
    [InlineData(SchemaFormat.Uuid, "\"{7b6a5c4d-0000-4000-8000-000000000001}\"", false)]
    [InlineData(SchemaFormat.Uuid, "\"not-a-guid\"", false)]
    [InlineData(SchemaFormat.DateTime, "\"2026-08-01T12:00:00Z\"", true)]
    [InlineData(SchemaFormat.DateTime, "\"yesterday\"", false)]
    [InlineData(SchemaFormat.Uri, "\"https://example.test/a\"", true)]
    [InlineData(SchemaFormat.Uri, "\"/relative\"", false)]
    [InlineData(SchemaFormat.Email, "\"a@example.test\"", true)]
    [InlineData(SchemaFormat.Email, "\"a@@example.test\"", false)]
    [InlineData(SchemaFormat.Email, "\"no-at-sign\"", false)]
    [InlineData(SchemaFormat.Region, "\"eu-central\"", true)]
    [InlineData(SchemaFormat.Region, "\"EU Central\"", false)]
    public void AFormatIsCheckedRatherThanAnnotated(SchemaFormat format, string value, bool accepted) {
        var validated = Validate(Schema(new SchemaProperty("/v", SchemaKind.Text) { Format = format }), $$"""{"v":{{value}}}""");

        validated.IsSuccess.ShouldBe(accepted);
    }

    [Fact]
    public void AResourceIdFormatUsesTheGatewaysOwnParser() {
        var schema = Schema(new SchemaProperty("/target", SchemaKind.Text) { Format = SchemaFormat.ResourceId });

        // ⚠ A second implementation of the id grammar would be a second opinion about what the
        // platform accepts, so this delegates to ResourceId.TryParsePath.
        Validate(schema, """{"target":"/tenants/x/subscriptions/y"}""").IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void AFormatOnANonStringIsRefusedAtDeclarationTime() =>
        Should.Throw<ArgumentException>(
            () => Schema(new SchemaProperty("/n", SchemaKind.Number) { Format = SchemaFormat.Uuid })
        ).Message.ShouldContain("refines a string");

    // ── 5. Bounds ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("9", true)]
    [InlineData("10", false)]
    public void ANumericBoundIsInclusive(string value, bool accepted) =>
        Validate(
            Schema(new SchemaProperty("/n", SchemaKind.WholeNumber) { Minimum = 1, Maximum = 9 }),
            $$"""{"n":{{value}}}"""
        ).IsSuccess.ShouldBe(accepted);

    [Theory]
    [InlineData("\"\"", false)]
    [InlineData("\"ab\"", true)]
    [InlineData("\"abcd\"", false)]
    public void AStringLengthIsInclusive(string value, bool accepted) =>
        Validate(
            Schema(new SchemaProperty("/s", SchemaKind.Text) { MinLength = 1, MaxLength = 3 }),
            $$"""{"s":{{value}}}"""
        ).IsSuccess.ShouldBe(accepted);

    [Fact]
    public void APatternIsAppliedToTheWholeValueRatherThanSearchedFor() {
        var schema = Schema(new SchemaProperty("/s", SchemaKind.Text) { Pattern = "[a-z]+" });

        Validate(schema, """{"s":"abc"}""").IsSuccess.ShouldBeTrue();
        // ⚠ The trap the anchoring exists for. JSON Schema's `pattern` is a search, so an unanchored
        // one accepts anything *containing* a match — a provider who wrote `[a-z]+` and got a
        // validator that took "123abc456" has a rule that does nothing.
        Validate(schema, """{"s":"123abc456"}""").IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void AnImpossibleBoundIsRefusedAtDeclarationTime() =>
        Should.Throw<ArgumentException>(
            () => Schema(new SchemaProperty("/n", SchemaKind.Number) { Minimum = 10, Maximum = 1 })
        ).Message.ShouldContain("no value satisfies it");

    [Fact]
    public void AMalformedPatternIsRefusedAtDeclarationTime() =>
        Should.Throw<ArgumentException>(() => Schema(new SchemaProperty("/s", SchemaKind.Text) { Pattern = "[a-" }))
            .Message.ShouldContain("does not compile");

    /// <summary>
    ///     The pattern that used to need a stopwatch, answered on its merits instead (#76).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The assertion that matters is the <i>message</i>, not the elapsed time.</b> Under
    ///         the 100 ms match timeout this replaced, this input was also refused inside a second —
    ///         but refused with "could not be checked against this property's pattern within the time
    ///         budget", which is the validator saying it gave up. <c>SchemaProperty.Matcher</c> is
    ///         <c>RegexOptions.NonBacktracking</c>, so the engine is linear in the input by
    ///         construction and the answer is the real one: this string does not match. A refusal that
    ///         depends on how long the match took is a refusal that depends on how busy the silo is,
    ///         and a tenant's valid value must not be rejected because a neighbouring request was
    ///         expensive.
    ///     </para>
    ///     <para>
    ///         <c>(a+)+b</c> against a run of <c>a</c>s ending in something else is the canonical
    ///         exponential blow-up; the same shape, for the same reason, as
    ///         <c>CyberCloud.Core.Tests.SecretShapedTextTests.ALongHostileStringIsAnsweredInLinearTimeRatherThanEventually</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And there is deliberately no elapsed-time bound, which this test carried for one
    ///         review cycle and should not have.</b> A wall-clock assertion is the exact instrument
    ///         #76 removes everywhere else: it turns a busy or oversubscribed agent into a red about
    ///         the host rather than about the tree, which is the flake family (#67) this issue exists
    ///         to stop growing. It also asserted nothing the message assertion does not already:
    ///         restore the old budget and the message becomes the timeout's, which is exactly what
    ///         the assertion below refuses; drop to the backtracking engine with no budget at all and
    ///         the run never finishes, which the test host reports on its own. Neither outcome needs
    ///         a stopwatch in the test body.
    ///     </para>
    /// </remarks>
    [Fact]
    public void APatternThatWouldBacktrackCatastrophicallyIsAnsweredRatherThanTimedOut() {
        var schema = Schema(new SchemaProperty("/s", SchemaKind.Text) { Pattern = "(a+)+b" });
        var hostile = new string('a', 60) + "!";

        var validated = Validate(schema, $$"""{"s":"{{hostile}}"}""");

        validated.IsFailure.ShouldBeTrue();
        validated.Error!.Message.ShouldContain(
            "does not match",
            Case.Sensitive,
            "the value was refused because the validator ran out of time rather than because it does "
            + "not satisfy the pattern"
        );
    }

    /// <summary>
    ///     And the same at declaration time, which is where the flake actually lived (#76).
    /// </summary>
    /// <remarks>
    ///     <c>SchemaProperty.Incoherences</c> checks a declared <c>DefaultJson</c> against its own
    ///     property's constraints, so before this change a schema was <i>constructed</i> under a wall
    ///     clock — and <c>ResourceSchema.Of</c> is called from provider static initialisers and from
    ///     every fixture builder in the suite.
    ///     <c>ChartAnnotationTests.APointerNoTypeDeclaresAsPlacementIsStillAnOrdinaryChartRow</c> went
    ///     red once on the time budget and green on re-run, which is a red about the host rather than
    ///     about the tree. There is no way to assert "no wall clock was consulted" directly, so this
    ///     asserts the observable consequence: the refusal names the mismatch a reader can act on and
    ///     never a budget, on the worst pattern-and-literal pair a declaration can hold.
    /// </remarks>
    [Fact]
    public void ADeclarationIsCheckedOnItsMeritsRatherThanAgainstAClock() =>
        Should.Throw<ArgumentException>(
                () => Schema(
                    new SchemaProperty("/s", SchemaKind.Text) {
                        Pattern = "(a+)+b",
                        DefaultJson = "\"" + new string('a', 60) + "!\""
                    }
                )
            )
            .Message.ShouldContain("does not match", Case.Sensitive, "the declaration was judged by a stopwatch");

    /// <summary>
    ///     ⚠ <b>The price of the non-backtracking engine, charged where a provider can see it.</b>
    /// </summary>
    /// <remarks>
    ///     A lookaround, a backreference or an atomic group is exactly the construct whose cost is not
    ///     linear in the input, and a <c>Pattern</c> is applied to a caller-supplied string. So one is
    ///     refused at declaration time with a message naming the rule, rather than accepted here and
    ///     thrown from <c>Regex</c> at the first request that reached the property — the same
    ///     "fail the process that would have served it" argument <c>ResourceSchema.Of</c> is built on.
    /// </remarks>
    [Fact]
    public void APatternTheLinearEngineCannotRunIsRefusedAtDeclarationTime() =>
        Should.Throw<ArgumentException>(
                () => Schema(new SchemaProperty("/s", SchemaKind.Text) { Pattern = "(?=[a-z])[a-z0-9]+" })
            )
            .Message.ShouldContain("non-backtracking");

    /// <summary>
    ///     And the refusal survives the property ALSO declaring a literal, which is the normal shape.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The case above misses this by construction, and the miss was a real escape.</b>
    ///         <c>SchemaProperty.Incoherences</c> guards the compile probe and then, a few lines
    ///         later, checks the declared <c>DefaultJson</c> by running it through the ordinary
    ///         request-path validation — which builds the same matcher again, in
    ///         <c>ResourceSchema.PatternProblem</c>, where nothing is caught on purpose. So a property
    ///         with an unrunnable pattern <i>and</i> a default threw a bare
    ///         <c>NotSupportedException</c> out of <c>ResourceSchema.Of</c>: it named neither the
    ///         pointer nor the rule, it discarded every other problem the schema had, and it broke
    ///         <c>Of</c>'s documented <c>ArgumentException</c> contract for every caller that asserts
    ///         on it. Pattern-plus-default is not an exotic combination here —
    ///         <c>Cidr.OptionalV4Pattern</c> and <c>PortRange.OptionalListPattern</c> exist precisely
    ///         so that an optional patterned property can default to <c>""</c>, and every one of
    ///         <c>NetworkSecurityGroups</c>' patterned properties is declared that way.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The default has to be a well-formed string of the property's own kind for this to
    ///         test anything.</b> <c>ResourceSchema.ValueProblems</c> reports a kind mismatch and stops
    ///         — a <c>42</c> here would never reach the constraint checks, so the pattern would never
    ///         be built a second time and the test would pass against the defect. <c>"abc"</c> reaches
    ///         them; <c>MinLength</c> is the independent problem, and it is checked <i>before</i> the
    ///         pattern in <c>ConstraintProblems</c>, so it is also the thing the throw used to discard.
    ///     </para>
    ///     <para>
    ///         So this asserts all three halves at once: the exception type, the worded refusal naming
    ///         the pointer, and that the <i>other</i> problem on the same declaration still made it
    ///         into the aggregated list.
    ///     </para>
    /// </remarks>
    [Fact]
    public void APatternTheLinearEngineCannotRunIsRefusedByNameEvenWithADefaultAlongside() {
        var refusal = Should.Throw<ArgumentException>(
            () => Schema(
                new SchemaProperty("/s", SchemaKind.Text) {
                    Pattern = "(?=[a-z])[a-z0-9]+",
                    MinLength = 10,
                    DefaultJson = "\"abc\""
                }
            )
        );

        refusal.Message.ShouldContain("'/s'");
        refusal.Message.ShouldContain("non-backtracking");
        refusal.Message.ShouldContain(
            "the minimum is 10",
            Case.Sensitive,
            "the unrunnable pattern swallowed the other problem on the same declaration"
        );
    }

    /// <summary>
    ///     The same for a pattern that does not compile at all, and for an <c>ExampleJson</c>.
    /// </summary>
    /// <remarks>
    ///     The compile failure takes the other <c>catch</c> in <c>SchemaProperty.Incoherences</c>, and
    ///     an example is checked by the second <c>CheckLiteral</c> call rather than the first, so both
    ///     of those are separate ways back into the throw this pins. <c>Regex</c> raises
    ///     <c>ArgumentException</c> here rather than <c>NotSupportedException</c>, which is a different
    ///     escape with the same shape: an exception naming a bracket instead of the pointer.
    /// </remarks>
    [Fact]
    public void AMalformedPatternIsRefusedByNameEvenWithAnExampleAlongside() =>
        Should.Throw<ArgumentException>(
                () => Schema(
                    new SchemaProperty("/s", SchemaKind.Text) { Pattern = "[a-", ExampleJson = "\"abc\"" }
                )
            )
            .Message.ShouldContain("does not compile");

    // ── 9. Defaults and examples ───────────────────────────────────────────────────────────────

    [Fact]
    public void ADefaultTheSchemaWouldRejectIsRefusedAtDeclarationTime() =>
        // A default that fails its own property ships as a form nobody can submit, and the failure is
        // discovered by whoever opens the form.
        Should.Throw<ArgumentException>(
            () => Schema(new SchemaProperty("/n", SchemaKind.WholeNumber) { Minimum = 1, DefaultJson = "0" })
        ).Message.ShouldContain("DefaultJson");

    [Fact]
    public void ADefaultOfTheWrongKindIsRefusedAtDeclarationTime() =>
        Should.Throw<ArgumentException>(
            () => Schema(new SchemaProperty("/s", SchemaKind.Text) { DefaultJson = "42" })
        ).Message.ShouldContain("must be a string");

    [Fact]
    public void ALiteralThatIsNotJsonIsRefusedAtDeclarationTime() =>
        // ⚠ A string default is spelled with its quotes. "free" is not JSON; "\"free\"" is.
        Should.Throw<ArgumentException>(
            () => Schema(new SchemaProperty("/s", SchemaKind.Text) { DefaultJson = "free" })
        ).Message.ShouldContain("not JSON");

    [Fact]
    public void ADefaultIsNotAppliedByTheValidator() {
        // ⚠ Deliberate: a substituted default would make the stored body differ from the body sent,
        // and the grain's desired state is what the reconciler and the drift hash read.
        var schema = Schema(new SchemaProperty("/n", SchemaKind.WholeNumber, Required: true) { DefaultJson = "1" });

        Validate(schema, "{}").IsFailure.ShouldBeTrue();
    }

    // ── The tag bag, which is the platform's and not a provider's ──────────────────────────────

    [Fact]
    public void AProviderMayNotDeclareTheTagBagItself() =>
        // Two descriptions of one property is the drift ADR-012 exists to remove. SupportsTags()
        // declares it and TagRules is the one shape.
        Should.Throw<ArgumentException>(() => Schema(new SchemaProperty(TagRules.JsonPointer, SchemaKind.Nested)))
            .Message.ShouldContain("SupportsTags");

    [Fact]
    public void TheTagBagIsAcceptedOnlyForATypeThatDeclaredIt() {
        var schema = Schema(new SchemaProperty("/location", SchemaKind.Text));

        using var body = JsonDocument.Parse("""{"location":"eu-central","tags":{"env":"prod"}}""");

        schema.Validate(body.RootElement, allowTags: true).IsSuccess.ShouldBeTrue();
        schema.Validate(body.RootElement, allowTags: false).IsFailure.ShouldBeTrue();
    }

    // ── Every problem is reported, not just the first ──────────────────────────────────────────

    [Fact]
    public void EveryConstraintFailureIsReportedNotOnlyTheFirst() {
        var schema = Schema(
            new SchemaProperty("/sku", SchemaKind.Text) { AllowedValues = ["small"] },
            new SchemaProperty("/n", SchemaKind.WholeNumber) { Maximum = 5 },
            new SchemaProperty("/id", SchemaKind.Text) { Format = SchemaFormat.Uuid }
        );

        var validated = Validate(schema, """{"sku":"huge","n":9,"id":"nope"}""");

        // docs/plan/08 § Errors: the first is the top-level target so the portal has one field to
        // highlight; the rest are Details, because a form fixed one field per round trip is a form
        // nobody finishes.
        validated.IsFailure.ShouldBeTrue();
        validated.Error!.Details.Length.ShouldBe(2);
    }
}
