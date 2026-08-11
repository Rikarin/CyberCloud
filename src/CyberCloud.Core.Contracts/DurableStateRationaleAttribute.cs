namespace CyberCloud.Core.Contracts;

/// <summary>
///     Records why a grain binds state to <see cref="StorageTiers.Durable" /> without being on the
///     reviewed list in <c>durable-grains.txt</c>.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/05 § Choosing a tier: <i>"every grain type in <c>durable-grains.txt</c> must
///         bind its primary state to <c>Durable</c>, and any grain type not on the list that binds to
///         <c>Durable</c> must carry <c>[DurableStateRationale("…")]</c>. The list is reviewed like a
///         schema migration, because that is what it is."</i> The <c>Storage tier</c> gate in
///         <c>build/Build.Architecture.cs</c> is what makes that true.
///     </para>
///     <para>
///         ⚠ <b>This is the escape hatch, not the front door.</b> Reach for the list first. The
///         attribute exists for a grain whose durable binding is real but is not platform state a
///         reviewer should be asked to approve as a schema — a sample, a fixture, a
///         soon-to-be-deleted bootstrap. Anything holding tenant data belongs on the list, where the
///         diff is visible.
///     </para>
///     <para>
///         The rationale is never read at runtime. It is there so the next person reading the type
///         learns why it is off the list from the type itself, rather than from the build failing
///         when they take the attribute off.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     [DurableStateRationale("Phase-0 exit criterion — proves both tiers wire up. Not tenant state.")]
///     sealed class HelloGrain(
///         [PersistentState("hello-durable", StorageTiers.Durable)] IPersistentState&lt;HelloState&gt; durable)
///         : Grain, IHelloGrain;
///     </code>
/// </example>
/// <param name="reason">
///     Why this grain's durable binding is not a reviewed entry in <c>durable-grains.txt</c>. Written
///     for a reviewer, not for a log line.
/// </param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class DurableStateRationaleAttribute(string reason) : Attribute {
    /// <summary>Why this grain is off the reviewed list. Never empty — the gate checks.</summary>
    public string Reason { get; } = reason;
}
