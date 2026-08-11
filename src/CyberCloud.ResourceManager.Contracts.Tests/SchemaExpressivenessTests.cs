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
