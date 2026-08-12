// ⚠ ONE TEST CLASS AT A TIME IN THIS ASSEMBLY, AND IT IS A CORRECTNESS RULE RATHER THAN A THROTTLE.
//
// Two things force it, and each one cost a run before it was here:
//
//   * ClusterConformanceState<TSource> is static, because Orleans'
//     TestClusterBuilder.AddSiloBuilderConfigurator<T> constructs the configurator with `new()` and
//     nothing can be handed to it. One provider has TWO harnesses in a run — the lifecycle fixture's
//     and the silo-kill test's successor pair — so two classes running at once means the second
//     overwrites the first's connection and then disposes the KubernetesClient the first's silo is
//     still calling. The observed symptom was a NullReferenceException inside k8s.SendRequestRaw,
//     several frames below anything this repository wrote.
//
//   * The silo-kill test KILLS SILOS and rebuilds a cluster on the same PostgreSQL, the same Redis
//     and the same k3s. Anything else mid-reconcile at that moment is collateral.
//
// It is also the right default for the suite's shape: every test here holds a real API server and a
// real database, so the parallelism that matters is between test PROCESSES — which
// ClusterSlot deliberately serialises too — and not between classes inside one.

[assembly: CollectionBehavior(DisableTestParallelization = true)]
