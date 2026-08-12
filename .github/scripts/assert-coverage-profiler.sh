#!/usr/bin/env bash
#
# Proves `dotnet-coverage` can actually instrument on this runner, before `./build.sh Test` bets the
# coverage floor on it.
#
# ── ⚠ WHY THIS EXISTS, WHICH IS NOT "TO BE THOROUGH" ─────────────────────────────────────────────
#
# dotnet-coverage 18.9.0 fails to instrument by EXITING 0 AND WRITING AN EMPTY REPORT. Observed
# three ways while writing this:
#
#   macOS 15 / arm64                          "No code coverage data available. Profiler was not
#                                              initialized." → <packages /> , exit 0
#   Ubuntu 24.04 / x86_64, no libxml2         same line, plus "Verify that glibc (>=2.27), libxml2
#                                              and all .NET dependencies are installed"
#                                              → <packages /> , exit 0
#   Ubuntu 24.04 / x86_64, libxml2 installed  line-rate="0.667", lines-covered="2" — it works
#
# The package explains all three: tools/net8.0/any/ has profilers for ubuntu/x64, alpine/x64,
# macos/x64 and Windows only. THERE IS NO linux-arm64 PROFILER AND NO macos-arm64 ONE.
#
# Build.Test.cs § CoverageCollectionIsAvailable knows about the macOS arm64 hole and skips
# collection there. It does NOT know about the other two, and on Linux it returns true — so a
# runner without libxml2, or an arm64 Linux runner, collects an empty report, and
# EnforceCoverageFloor then reports every shipping project at 0 %. That is a red build with a
# confident, wrong diagnosis: it says "write more tests" when the truth is "the profiler never
# loaded". This script gets in front of that and says the true thing instead.
#
# Costs about fifteen seconds. The alternative costs somebody an afternoon.

set -euo pipefail

probe="${RUNNER_TEMP:-/tmp}/coverage-profiler-probe"
rm -rf "$probe"
mkdir -p "$probe"

echo "Coverage profiler probe on $(uname -s)/$(uname -m)"

# The tool is pinned in .config/dotnet-tools.json. Restoring here rather than trusting build.sh to
# have done it keeps this script runnable on its own, which is how it got sabotage-tested.
dotnet tool restore > /dev/null

# A three-line assembly with one covered branch and one uncovered one, so a working profiler has to
# report a line rate that is neither 0 nor 1. An assembly with nothing uncovered would report 1 and
# be indistinguishable from the empty-report case for a lazier assertion than the one below.
dotnet new console -o "$probe/Probe" --force > /dev/null
cat > "$probe/Probe/Program.cs" <<'PROBE'
public static class Probe
{
    public static int Covered(int n) => n > 0 ? n * 2 : 0;
    public static int NeverCalled(int n) => n - 1;
    public static void Main() => System.Console.WriteLine(Covered(21));
}
PROBE
dotnet build "$probe/Probe" -c Release -o "$probe/out" > /dev/null

report="$probe/probe.cobertura.xml"

# ⚠ No `set -e` reliance here: the whole point is that this command exits 0 when it has failed.
dotnet dotnet-coverage collect \
  --nologo --output-format cobertura --output "$report" \
  -- dotnet "$probe/out/Probe.dll" || true

if [ ! -f "$report" ]; then
    echo "::error title=Coverage profiler::dotnet-coverage wrote no report at all on $(uname -s)/$(uname -m). ./build.sh Test would then take the 'not enforced' path, which Build.Test.cs turns into a hard failure on CI."
    exit 1
fi

echo "--- probe report"
cat "$report"
echo "--- end probe report"

# The assertion. A working profiler emits at least one <package>; the broken one emits <packages />.
if ! grep -q '<package ' "$report"; then
    cat <<MESSAGE
::error title=Coverage profiler::dotnet-coverage cannot instrument on this runner, and it says so by exiting 0 with an empty report.

  runner   : $(uname -s)/$(uname -m)
  report   : <packages /> — nothing was instrumented

The two known causes, in the order they are worth checking:

  1. libxml2 is missing. dotnet-coverage's native profiler links against it and the SDK container
     images do not carry it. Fix: sudo apt-get install -y libxml2.
  2. The runner is arm64. dotnet-coverage 18.9.0 ships no arm64 profiler for Linux or macOS — see
     tools/net8.0/any/ in the package, which has ubuntu/x64, alpine/x64 and macos/x64 and nothing
     else. Fix: run this job on an x64 runner.

⚠ Do NOT "fix" this by dropping the coverage step. docs/plan/23 § Test layers requires >= 70 % per
project on every PR and Build.Test.cs makes a missing report a hard failure on CI precisely so that
a floor nobody measures cannot look green.
MESSAGE
    exit 1
fi

echo "dotnet-coverage instruments correctly here — the coverage floor in ./build.sh Test will be measured, not assumed."
