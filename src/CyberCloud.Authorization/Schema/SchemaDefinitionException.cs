using System.Collections.Immutable;

namespace CyberCloud.Authorization;

/// <summary>
///     Thrown by <see cref="SchemaBuilder.Build" /> when the schema breaks one of its rules.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             An exception rather than a <c>Result</c>, and that is the repository's own rule
///             applied rather than an exception to it.
///         </b>
///         docs/plan/00 § Non-negotiables reserves
///         <c>Result</c> for domain outcomes and exceptions for bugs. A schema is
///         <b>
///             compiled
///             C#
///         </b>
///         (docs/plan/07 § The model) — nothing outside this process can produce one, so a
///         schema that breaks a rule is our own code being wrong, which must page someone at
///         startup rather than return a tidy failure that a host might log and continue past.
///     </para>
///     <para>
///         <see cref="SchemaBuilder.Validate" /> is the same rules without the throw, for tests and
///         for anything that wants to report every problem rather than stop at the first host that
///         crashes.
///     </para>
/// </remarks>
public sealed class SchemaDefinitionException : Exception {
    /// <summary>Every problem found, one per rule violation.</summary>
    public ImmutableArray<string> Problems { get; }

    /// <summary>Creates an exception with no problems recorded.</summary>
    public SchemaDefinitionException()
        : this("The authorization schema is not valid.", []) { }

    /// <summary>Creates an exception with a message and no problems recorded.</summary>
    /// <param name="message">The message.</param>
    public SchemaDefinitionException(string message)
        : this(message, []) { }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public SchemaDefinitionException(string message, Exception innerException)
        : base(message, innerException) {
        Problems = [];
    }

    /// <summary>Creates an exception listing every problem found.</summary>
    /// <param name="message">The summary line.</param>
    /// <param name="problems">Every problem, in the order they were found.</param>
    public SchemaDefinitionException(string message, IEnumerable<string> problems)
        : base(Compose(message, problems)) {
        Problems = [.. problems];
    }

    static string Compose(string message, IEnumerable<string> problems) {
        var listed = problems.ToList();
        return listed.Count == 0
            ? message
            : message
            + Environment.NewLine
            + string.Join(Environment.NewLine, listed.Select(x => "  • " + x));
    }
}
