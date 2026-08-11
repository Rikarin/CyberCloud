namespace CyberCloud.Analyzers.Tests;

/// <summary>
///     CC1004 — <c>docs/plan/02 § ADR-002</c>: "an analyzer that flags string literals containing
///     <c>|</c> in <c>GetGrain</c> arguments".
/// </summary>
public sealed class GrainKeyLiteralAnalyzerTests {
    const string Grains = """
                          using Orleans;
                          using Orleans.Multitenant;
                          using CyberCloud.Core.Resources;

                          public interface IResourceGrain : IGrainWithStringKey
                          {
                          }
                          """;

    // ── positive ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public Task ALiteralKeyCarryingTheTenantSeparatorIsReported() =>
        AnalyzerHarness.ReportsAsync<GrainKeyLiteralAnalyzer>(
            Grains
            + """

              public sealed class Reader
              {
                  public IResourceGrain Get(IGrainFactory grains) =>
                      grains.GetGrain<IResourceGrain>({|CC1004:"tenant-a|res/1"|});
              }
              """
        );

    [Fact]
    public Task AnInterpolatedKeyCarryingTheTenantSeparatorIsReported() =>
        AnalyzerHarness.ReportsAsync<GrainKeyLiteralAnalyzer>(
            Grains
            + """

              public sealed class Reader
              {
                  public IResourceGrain Get(IGrainFactory grains, string tenant) =>
                      grains.GetGrain<IResourceGrain>({|CC1004:$"{tenant}|res/1"|});
              }
              """
        );

    /// <summary>
    ///     ⚠ Going through <c>ForTenant</c> does not license a hand-forged key — the qualification is
    ///     applied on top of whatever is passed, so a <c>|</c> here still lands inside a physical
    ///     key.
    /// </summary>
    [Fact]
    public Task TenantQualifyingDoesNotExcuseALiteralSeparator() =>
        AnalyzerHarness.ReportsAsync<GrainKeyLiteralAnalyzer>(
            Grains
            + """

              public sealed class Reader
              {
                  public IResourceGrain Get(IGrainFactory grains) =>
                      grains.ForTenant("t").GetGrain<IResourceGrain>({|CC1004:"a|b"|});
              }
              """
        );

    // ── negative ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public Task AKeyBuiltByGrainKeysIsCorrect() =>
        AnalyzerHarness.IsSilentAsync<GrainKeyLiteralAnalyzer>(
            Grains
            + """

              public sealed class Reader
              {
                  public IResourceGrain Get(IGrainFactory grains, System.Guid id) =>
                      grains.ForTenant("t").GetGrain<IResourceGrain>(GrainKeys.Resource(id));
              }
              """
        );

    [Fact]
    public Task AnOrdinaryLiteralKeyIsCorrect() =>
        AnalyzerHarness.IsSilentAsync<GrainKeyLiteralAnalyzer>(
            Grains
            + """

              public sealed class Reader
              {
                  public IResourceGrain Get(IGrainFactory grains) =>
                      grains.ForTenant("t").GetGrain<IResourceGrain>("res/00000000000000000000000000000000");
              }
              """
        );

    /// <summary>
    ///     ⚠ The near-miss that a naive "any literal with a pipe near the word GetGrain" rule would
    ///     fire on. A method called <c>GetGrain</c> on something that is not a grain factory is not
    ///     addressing a grain.
    /// </summary>
    [Fact]
    public Task AMethodCalledGetGrainOnSomethingElseIsNotAGrainReference() =>
        AnalyzerHarness.IsSilentAsync<GrainKeyLiteralAnalyzer>(
            """
            public sealed class Silo
            {
                public string GetGrain(string pattern) => pattern;
            }

            public sealed class Reader
            {
                public string Get(Silo silo) => silo.GetGrain("wheat|barley");
            }
            """
        );

    /// <summary>A pipe in a string that is not a <c>GetGrain</c> argument is just a pipe.</summary>
    [Fact]
    public Task APipeElsewhereIsNotAGrainKey() =>
        AnalyzerHarness.IsSilentAsync<GrainKeyLiteralAnalyzer>(
            Grains
            + """

              public sealed class Reader
              {
                  public string Describe() => "tenant|resource";
              }
              """
        );
}
