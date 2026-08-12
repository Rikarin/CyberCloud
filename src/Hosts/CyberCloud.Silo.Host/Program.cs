using CyberCloud.Communication;
using CyberCloud.ServiceDefaults;
using CyberCloud.ServiceDefaults.Storage;
using CyberCloud.Silo.Host;
using CyberCloud.Tenancy;
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

var builder = OrleansApplication.CreateSilo(
    args,
    // ── The sending domain — docs/plan/17, docs/plan/04 § Silo composition ─────────────────────
    //
    // ⚠ THIS ADDS SEVEN GRAIN TYPES TO THE SILO AND NO NEW START-UP REQUIREMENT, and the second half
    // is the part worth checking rather than assuming. AddCyberCloudCommunication registers
    // services only — the clock, the five refusing channel providers, the provider registry, the
    // client-side sender and the webhook router. It configures no reminder service (nothing in the
    // module is IRemindable), no stream provider, and no storage: the grains bind
    // StorageTiers.Hot and StorageTiers.Durable, which are exactly the two AddCyberCloudTenancy
    // already wires below. That is why this can sit beside the tenancy call rather than needing the
    // "reminders are the host's to choose" arrangement AddCyberCloudResourceManager's remarks
    // describe.
    //
    // ⚠ configureCluster runs AFTER configureStorage inside CreateSilo, which is the order this
    // needs: the tiers must exist before a grain that binds one is activated. Nothing here resolves
    // a tier at wiring time, so the ordering is belt to the braces rather than load-bearing.
    //
    // ⚠ WITH NO CARRIER CONFIGURED, EVERY SEND FAILS WITH A SENTENCE SAYING SO. That is the
    // designed state of a silo with no Twilio client in it, not a defect — see
    // CommunicationSiloBuilderExtensions on why the refusing seams are registered rather than
    // omitted. A channel with no provider at all would fail with a wiring error instead, which is
    // the message an operator cannot act on.
    // ── The identity module — docs/plan/11, docs/plan/04 § Silo composition ────────────────────
    //
    // ⚠ THIS IS WHAT MAKES A ONE-TIME CODE POSSIBLE ANYWHERE IN THE PLATFORM, AND ITS ABSENCE WAS
    // NOT A REGISTRATION OVERSIGHT — it was the reason three separate comments in this tree claimed
    // UnavailableOtpDelivery was "what every host gets" while no host had one at all.
    //
    // The ProjectReference in this .csproj is what puts the identity grains in the silo (Orleans
    // DISCOVERS grains by scanning referenced assemblies); AddSiloIdentity below is what registers
    // the services their constructors ask for, including the IOtpDeliverySeam that
    // UserGrain.IssueOtpAsync calls. Either one without the other is a silo that fails at the first
    // activation rather than at start-up.
    //
    // ⚠ It goes BESIDE AddCyberCloudCommunication and not instead of it: the seam adapter resolves
    // IMessageSender, which that call is what provides. OtpPolicy carries the argument for why the
    // code is minted, stored and verified here rather than in CyberCloud.Identity.Host.
    configureCluster: silo => silo.AddCyberCloudCommunication().AddSiloIdentity(),
    configureStorage: (silo, storage) => silo.AddCyberCloudTenancy(storage)
);

await builder.Services.AddApplicationAsync<SiloHostModule>();

var app = builder.Build();

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
