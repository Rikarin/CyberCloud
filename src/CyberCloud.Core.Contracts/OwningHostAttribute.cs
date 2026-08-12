namespace CyberCloud.Core.Contracts;

/// <summary>
///     Names a host under <c>src/Hosts</c> that is allowed to reference this <c>*.Application</c>
///     assembly.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/03 § Assembly graph rules, rule 4: <i>"Nothing references a
///         <c>*.Application</c> assembly except its own host."</i> Nothing else in the tree says
///         which host that is — an application assembly's name
///         (<c>CyberCloud.Providers.Sample.Application</c>) names its provider, not its host, and a
///         host's <c>ProjectReference</c> closure cannot supply the answer because it is the very
///         edge the rule is about. This attribute is the answer, and until an assembly carries one
///         it has no own host and rule 4 permits nothing to reference it.
///     </para>
///     <para>
///         ⚠ <b><see cref="AttributeUsageAttribute.AllowMultiple" /> is <c>true</c>, and that is not
///         a weakening.</b> docs/plan/03 § Assembly graph rules, rule 5 lets the gateway reference a
///         provider's <c>.Application</c> assembly, and the gateway is not the silo host — so the
///         doc's own two rules disagree about how many hosts an application layer may have. Each
///         entry here is one line in one diff naming one host, which is the reviewable form of that
///         disagreement rather than a blanket exemption for anything under <c>src/Hosts</c>.
///     </para>
///     <para>
///         The value is never read at runtime. It exists so the coupling is visible in the diff that
///         creates it and in the assembly that carries it, rather than only in a build failure.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     [assembly: OwningHost("CyberCloud.Silo.Host")]
///     [assembly: OwningHost("CyberCloud.Gateway.Host")]
///     </code>
/// </example>
/// <param name="host">
///     The assembly name of a host under <c>src/Hosts</c>. The Assembly graph gate rejects a name
///     that is not one, so this cannot launder an ordinary reference into an allowed one.
/// </param>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class OwningHostAttribute(string host) : Attribute {
    /// <summary>The host allowed to reference this assembly. Never empty — the gate checks.</summary>
    public string Host { get; } = host;
}
