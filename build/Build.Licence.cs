// Licence — docs/plan/23 § Build, row `Licence`: "ADR-011 scan over charts and images".

partial class Build
{
    void ScanLicences()
        => NotImplementedYet(
            nameof(Licence),
            "scan the licences of every packaged chart and every image layer, and fail on a licence "
            + "outside the ADR-011 allow-list",
            "docs/plan/23 § Build (row `Licence`) and docs/plan/02 ADR-011. Depends on `Charts` and "
            + "`Images` producing artefacts to scan.");
}
