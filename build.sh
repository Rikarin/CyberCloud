#!/usr/bin/env bash

# docs/plan/23 § Build — the single entry point for every build action, locally and in CI, so
# "works on my machine" and "works in CI" are the same code path.
#
#   ./build.sh                 # the default target, Compile
#   ./build.sh Test
#   ./build.sh Compile --configuration Release
#   ./build.sh --help          # every target and parameter
#
# ⚠ This invokes the build project directly with `dotnet run`. It does NOT go through the `nuke`
# global tool, and that is deliberate: `dotnet nuke` in a directory it does not recognise prompts
# on the console — verified —
#
#     $ dotnet nuke --help < /dev/null
#     Could not find .nuke directory/file. Do you want to setup a build? [y/n] (y):
#     Failed to read input in non-interactive mode.
#
# A CI step that blocks on a question is a hung pipeline, not a failing one. The committed .nuke/
# directory is what stops that prompt appearing at all; running the project directly means the
# global tool is not even on the path for this entry point.

set -eo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" > /dev/null 2>&1 && pwd)"
BUILD_PROJECT="$SCRIPT_DIR/build/_build.csproj"

# Nothing in this build should ever wait on stdin.
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
# Nuke 10.1.0 does have a telemetry notice — "Press <Enter> to create awareness cookie" — and this
# is the documented opt-out. The repository-level answer is <NukeTelemetryVersion> in
# build/_build.csproj; this covers the case where someone runs the build a different way.
export NUKE_TELEMETRY_OPTOUT=1

# .config/dotnet-tools.json pins dotnet-coverage, dotnet-ef and friends. Restoring them here rather
# than in a separate CI step is what keeps `./build.sh <Target>` the whole contract.
dotnet tool restore

exec dotnet run --project "$BUILD_PROJECT" --no-launch-profile -- "$@"
