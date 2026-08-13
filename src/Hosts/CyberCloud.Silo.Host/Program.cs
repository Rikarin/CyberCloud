using CyberCloud.ServiceDefaults;
using CyberCloud.ServiceDefaults.Storage;
using CyberCloud.Silo.Host;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Volo.Abp;

// ── The one-shot durable-schema mode ──────────────────────────────────────────────────────────
//
// ⚠ THIS IS A JOB, NOT A SILO, AND THE DISTINCTION IS THE DESIGN.
//
// Microsoft.Orleans.Persistence.AdoNet ships no SQL and does not migrate (the full account is on
// CyberCloud.ServiceDefaults.Storage.OrleansAdoNetSchema). The DDL is two bare `CREATE TABLE`
// scripts, so it must run exactly once per shard, before any silo bound to that shard starts. A
// silo that applied it on start would be N silos racing one `CREATE TABLE`: N-1 failures, and the
// one that wins is a coin toss.
//
// So it is a separate PROCESS, in the same image, selected by an argument:
//
//   local  — CyberCloud.AppHost runs `CyberCloud.Silo.Host --apply-durable-schema` as an Aspire
//            resource and makes both silos WaitForCompletion on it;
//   real   — the same image, the same argument, as a Helm `pre-install,pre-upgrade` hook Job.
//            Nothing in charts/ does this yet; that is a gap docs/plan/05 § Durable does not name.
//
// It reads the same `CyberCloud:Storage:Durable:Shards:*` configuration a silo does, so the job and
// the silos cannot disagree about which shards exist.
const string applySchemaFlag = "--apply-durable-schema";

if (args.Contains(applySchemaFlag, StringComparer.Ordinal)) {
    // ⚠ Filtered out before the configuration builder sees it. Microsoft.Extensions.Configuration's
    // command-line provider expects `--key=value` or `--key value`; a bare trailing switch is
    // "Unrecognized argument format", thrown out of AddCommandLine before a line of our code runs.
    return await ApplyDurableSchemaAsync(
        [.. args.Where(x => !string.Equals(x, applySchemaFlag, StringComparison.Ordinal))]
    );
}

// ⚠ EVERY LINE OF COMPOSITION LIVES IN SiloComposition AND NOT HERE, and that is not tidiness.
// Top-level statements cannot be called from a test, so for as long as the wiring was in this file
// the only thing that could exercise it was a process launch — and the wiring was wrong: no provider
// module, no resource manager, no reconciler anywhere in production, with 61 green test suites over
// it. SiloComposition.BuildAsync is what CyberCloud.Silo.Host.Tests composes, so a test can fail on
// what this process actually builds.
var app = await SiloComposition.BuildAsync(args);

// ⚠ NOT `app.InitializeApplicationAsync()`, which is the line every ABP sample uses. That extension
// lives in Volo.Abp.AspNetCore, a package docs/plan/02's dependency register does not list and this
// host does not need: it exists to insert ABP's own ASP.NET middleware pipeline, and a silo serves
// nothing but health endpoints. This is the same initialisation without the pipeline — without it
// no module's OnApplicationInitialization ever runs, which is silent until the first provider
// module has one.
await app.Services
    .GetRequiredService<IAbpApplicationWithExternalServiceProvider>()
    .InitializeAsync(app.Services);

app.MapDefaultEndpoints();

await app.RunAsync();
return 0;

static async Task<int> ApplyDurableSchemaAsync(string[] args) {
    var configuration = Host.CreateApplicationBuilder(args).Configuration;

    var storage = new CyberCloudStorageOptions();
    configuration.GetSection(CyberCloudStorageOptions.SectionName).Bind(storage);

    if (storage.Durable.Shards.Count == 0) {
        // A silent success here is the worst outcome available: the job goes green, the silos start,
        // and the first grain write fails with a message about `orleansquery` that names nothing
        // about configuration.
        await Console.Error.WriteLineAsync(
            $"--apply-durable-schema: {CyberCloudStorageOptions.SectionName}:Durable:Shards is "
            + "empty, so there is nothing to create the schema on. A silo started against this "
            + "configuration has no durable tier at all."
        );

        return 1;
    }

    var applied = await OrleansAdoNetSchema.ApplyAsync(storage.Durable, CancellationToken.None);

    foreach (var (shard, created) in applied) {
        Console.WriteLine(
            created
                ? $"--apply-durable-schema: created the Orleans grain-storage schema on '{shard}'."
                : $"--apply-durable-schema: '{shard}' already had it; nothing to do."
        );
    }

    return 0;
}
