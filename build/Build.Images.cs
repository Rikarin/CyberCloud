// Images — docs/plan/23 § Build, row `Images`:
// "Build, SBOM (Syft), sign (cosign), push by digest".

partial class Build
{
    void BuildImages()
        => NotImplementedYet(
            nameof(Images),
            "build a container image per host in src/Hosts, produce a Syft SBOM for each, sign them "
            + "with cosign and push by digest rather than by tag",
            "docs/plan/23 § Build (row `Images`). Depends on src/Hosts containing a host project — "
            + "docs/plan/03 § Hosts.");
}
