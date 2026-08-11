// Generate — docs/plan/23 § Build, row `Generate`:
// "Provider registry → OpenAPI → CLI verbs → SDK → portal forms (ADR-012). Fails on drift".

partial class Build
{
    void GenerateSurfaces()
        => NotImplementedYet(
            nameof(Generate),
            "walk the provider registry, emit the OpenAPI document, the `cc` CLI verbs, the .NET and "
            + "TypeScript SDKs and the portal resource forms, then diff them against what is checked "
            + "in and fail on any drift",
            "docs/plan/23 § Build (row `Generate`) and docs/plan/02 ADR-012. Depends on the first "
            + "provider assembly and the annotated chart values existing — docs/plan/03 § charts/.");
}
