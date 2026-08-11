using System.Reflection;

namespace CyberCloud.Metering.Contracts.Tests;

/// <summary>
///     docs/plan/22 § The pipeline: <i>"The ledger is durable-tier and append-only. Corrections are
///     new entries with a reason and a link to the original, never edits. An adjustable ledger cannot
///     be audited and cannot be defended in a dispute."</i>
/// </summary>
/// <remarks>
///     ⚠ <b>These assert that editing is structurally impossible, not that it is refused.</b> A
///     refusal is a runtime check somebody can add a bypass to; the absence of a mutator is a
///     property of the type. These are reflection tests for exactly that reason — the day somebody
///     adds <c>UpdateEntryAsync</c> to <c>IUsageLedgerGrain</c>, or turns
///     <c>UsageLedgerEntry.Quantity</c> into a settable property, a test fails rather than a review
///     being relied on to notice. The behavioural half — that a correction is a new entry pointing at
///     the original — is <c>LedgerTests</c> in <c>CyberCloud.Metering.Tests</c>.
/// </remarks>
public sealed class LedgerImmutabilityTests {
    /// <summary>
    ///     Verbs that reach an entry that already exists. ⚠ <c>Append</c> is deliberately not one:
    ///     appending is the only thing a ledger does.
    /// </summary>
    static readonly string[] MutatingVerbs = [
        "Update", "Set", "Edit", "Replace", "Delete", "Remove", "Amend", "Adjust", "Modify", "Overwrite",
        "Rewrite", "Void", "Reverse", "Clear", "Truncate", "Purge"
    ];

    [Fact]
    public void TheLedgerInterfaceDeclaresNoMutator() {
        var offending = typeof(IUsageLedgerGrain)
            .GetMethods()
            .Where(method => MutatingVerbs.Any(verb => method.Name.StartsWith(verb, StringComparison.Ordinal)))
            .Select(method => method.Name)
            .ToList();

        offending.ShouldBeEmpty(
            "IUsageLedgerGrain is append-only — docs/plan/22 § The pipeline. A method that reaches an "
            + "entry which already exists makes the ledger adjustable, and an adjustable ledger "
            + "cannot be defended in a dispute. Corrections are new entries: AppendCorrectionAsync."
        );
    }

    /// <summary>
    ///     ⚠ The other half. Immutable-looking methods on a mutable record would let a caller who
    ///     holds an entry change it and hand it back to something that trusts it.
    /// </summary>
    [Fact]
    public void EveryLedgerEntryMemberIsInitOnly() {
        foreach (var property in typeof(UsageLedgerEntry).GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
            var setter = property.SetMethod;

            if (setter is null) {
                continue;
            }

            setter.ReturnParameter
                .GetRequiredCustomModifiers()
                .ShouldContain(
                    typeof(System.Runtime.CompilerServices.IsExternalInit),
                    $"UsageLedgerEntry.{property.Name} has an ordinary setter. Every member of a "
                    + "ledger entry is init-only so that a copy in flight cannot be edited and "
                    + "handed on as the original."
                );
        }
    }

    /// <summary>
    ///     The append record cannot name a sequence, an identity, a recording time or a correction
    ///     target. A caller that could set any of those could write into the middle of the history,
    ///     overwrite an entry by naming it, or backdate.
    /// </summary>
    [Theory]
    [InlineData("Sequence")]
    [InlineData("EntryId")]
    [InlineData("RecordedAt")]
    [InlineData("CorrectsEntryId")]
    [InlineData("Reason")]
    public void TheAppendRecordCannotNameAFieldTheGrainOwns(string member) =>
        typeof(UsageLedgerAppend)
            .GetProperty(member)
            .ShouldBeNull(
                $"UsageLedgerAppend.{member} would let a caller choose something only the ledger may "
                + "assign. See IUsageLedgerGrain and UsageLedgerAppend's remarks."
            );

    /// <summary>
    ///     The three reads a ledger owes an auditor, present so the "audited and defended in a
    ///     dispute" claim is checkable rather than aspirational.
    /// </summary>
    [Theory]
    [InlineData("ListAsync")]
    [InlineData("ListCorrectionsAsync")]
    [InlineData("NetQuantityAsync")]
    public void TheLedgerCanBeRead(string method) => typeof(IUsageLedgerGrain).GetMethod(method).ShouldNotBeNull();

    /// <summary>
    ///     ⚠ A correction must carry both halves docs/plan/22 names — "a reason and a link to the
    ///     original". A signature missing either is a signature that permits an unexplained
    ///     adjustment.
    /// </summary>
    [Fact]
    public void ACorrectionTakesBothAReasonAndALinkToTheOriginal() {
        var parameters = typeof(IUsageLedgerGrain)
            .GetMethod(nameof(IUsageLedgerGrain.AppendCorrectionAsync))
            .ShouldNotBeNull()
            .GetParameters();

        parameters.ShouldContain(x => x.ParameterType == typeof(Guid), "no link to the original");
        parameters.ShouldContain(x => x.ParameterType == typeof(string), "no reason");
    }
}
