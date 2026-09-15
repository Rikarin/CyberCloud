#!/usr/bin/env bash
#
# charts/bundle/substitute.sh — the variable substitution clusterctl performs on a provider's
# components document, done here so that `kubectl apply` of the released document is the same bits
# `clusterctl init` would have applied. Sourced by install.sh; runnable on its own as a filter.
#
# ⚠ WHY THIS EXISTS, FOUND ON THE FIRST REAL RUN OF THE ROSTER (issue #2, 2026-09-15). The release
# manifests of the Cluster API family are clusterctl TEMPLATES, not plain documents: the container
# args reach the binary as `--insecure-diagnostics=${CAPI_INSECURE_DIAGNOSTICS:=false}` and
# `--feature-gates=…${CACPPK_DYNAMIC_INFRASTRUCTURE_CLUSTER_PATCH:=false}…`, and Go's
# `strconv.ParseBool` refuses the literal string — four controllers in CrashLoopBackOff, all four
# with `invalid argument "${CAPI_INSECURE_DIAGNOSTICS:=false}" for "--insecure-diagnostics" flag`.
# cluster-api/component.yaml chose `kubectl apply` over `clusterctl init` for the pinning argument,
# which is right, and missed that clusterctl also substitutes before it applies. Counted on that day:
# cluster-api-components.yaml v1.14.0 carries THIRTEEN distinct variables, control-plane-components.yaml
# v0.20.0 (kamaji-control-plane-provider) FIVE, and every one of the eighteen carries its own
# default — so the substituted document is a function of the pin alone, and the pinning argument
# survives.
#
# ⚠ THE THREE FORMS, AND WHAT EACH DOES — the subset of drone/envsubst that clusterctl's own
# SimpleProcessor documents, and the only forms any pinned manifest uses:
#
#   ${NAME}            the environment's value; REFUSED, naming NAME, when it is not set at all.
#   ${NAME:=default}   the environment's value when set and non-empty, else `default`.
#   ${NAME:-default}   the same. drone/envsubst treats the two alike and so does this.
#
# The environment wins so that the knob clusterctl offers — `CLUSTER_TOPOLOGY=true clusterctl init`
# — is the same knob here: `CLUSTER_TOPOLOGY=true install.sh --component cluster-api`. The default
# is read off the document itself, never off a config file or a remote contract, which is the whole
# of why this is a substitution pass and not a clusterctl invocation.
#
# ⚠ WHAT IS DELIBERATELY LEFT ALONE, BECAUSE THE SAME DOCUMENTS CARRY IT. `$(VAR_NAME)` is the
# Kubernetes container-env expansion syntax and appears in every CustomResourceDefinition's
# description text (64 times in cluster-api-components.yaml); `$$` is its escape; a bare `$` ends
# validation regexes. None of those is `${`, and the pattern below is anchored on `${` followed by an
# identifier, so they pass through byte for byte. A `${…}` that IS an identifier form this file does
# not implement — `${NAME^^}`, `${NAME:+word}` — is refused rather than passed through, because
# passing it through is exactly the crashloop this file exists to stop.
#
# ⚠ NOT envsubst. GNU envsubst knows `$NAME` and `${NAME}` and nothing else — it would hand
# `${CAPI_INSECURE_DIAGNOSTICS:=false}` to the container unchanged, or worse, expand it to the
# empty string. It is also not on a fresh machine: Git for Windows happens to ship one in
# /mingw64/bin and macOS ships none. awk is on every machine this script already assumes, and it
# is what the rest of this directory reads YAML with.
#
# ⚠ set-but-empty is "unset" for the two default forms and "set" for the bare form, which is what
# drone/envsubst does: `${X:=y}` with X="" yields y; `${X}` with X="" yields "".

# substitute_manifest <in> <out> — substitute one document. `-` is stdin or stdout. Returns 1,
# having printed every offending name to stderr, when a variable has neither a value nor a default,
# or when a `${…}` form this file does not implement is present.
#
# ⚠ NOT `/dev/stdin` AND `/dev/stdout`, WHICH WAS THE FIRST SPELLING AND FAILED UNDER THE TEST
# HARNESS ON ITS FIRST RUN: Git for Windows' bash, spawned from a .NET process with an anonymous
# pipe on each end, has no `/dev/stdout` to open — "line 112: /dev/stdout: No such file or
# directory" — while the same script from a terminal works. So `-` means "leave the descriptor
# alone": no `>` when the output is stdout, and no file argument when the input is stdin.
substitute_manifest() {
    local input="$1" output="$2"

    if [[ "$output" == "-" ]]; then
        substitute_stream "$input"
    else
        substitute_stream "$input" > "$output"
    fi
}

# substitute_stream <in> — the pass itself, to stdout. `-` reads stdin.
substitute_stream() {
    local input="$1"
    local -a files=()

    [[ "$input" == "-" ]] || files=("$input")

    awk '
        {
            line = $0
            out = ""
            while (match(line, /\$\{[A-Za-z_][A-Za-z0-9_]*(:[-=][^}]*)?\}/)) {
                out = out substr(line, 1, RSTART - 1)
                expr = substr(line, RSTART + 2, RLENGTH - 3)
                line = substr(line, RSTART + RLENGTH)

                name = expr
                has_default = 0
                def = ""
                at = index(expr, ":")
                if (at > 0) {
                    name = substr(expr, 1, at - 1)
                    def = substr(expr, at + 2)
                    has_default = 1
                }

                if (has_default) {
                    value = (name in ENVIRON && ENVIRON[name] != "") ? ENVIRON[name] : def
                } else if (name in ENVIRON) {
                    value = ENVIRON[name]
                } else {
                    if (!(name in missing)) { missing[name] = 1; missing_list = missing_list " " name }
                    value = "${" expr "}"
                }
                out = out value
            }

            if (match(line, /\$\{[A-Za-z_][^}]*\}/)) {
                form = substr(line, RSTART, RLENGTH)
                if (!(form in unknown)) { unknown[form] = 1; unknown_list = unknown_list " " form }
            }

            print out line
        }
        END {
            # Piped to `cat 1>&2` rather than written to "/dev/stderr": gawk, mawk and the BSD awk
            # all treat that name specially, and POSIX awk does not promise it. A pipe inherits the
            # real descriptor 2 under every awk this script can meet.
            if (missing_list != "") {
                printf "substitute.sh: the document names a variable with no default and no value in the environment:%s\n", missing_list | "cat 1>&2"
                printf "  clusterctl would refuse the same document with \"value for variables [%s] is not set\". Set it, or pin a\n", substr(missing_list, 2) | "cat 1>&2"
                printf "  release that carries a default for it — charts/bundle/substitute.sh.\n" | "cat 1>&2"
            }
            if (unknown_list != "") {
                printf "substitute.sh: the document uses a ${…} form this script does not implement:%s\n", unknown_list | "cat 1>&2"
                printf "  Only ${NAME}, ${NAME:=default} and ${NAME:-default} are substituted. Passing anything else through\n" | "cat 1>&2"
                printf "  is the crashloop this pass exists to stop, so it is refused instead — charts/bundle/substitute.sh.\n" | "cat 1>&2"
            }
            close("cat 1>&2")
            if (missing_list != "" || unknown_list != "") exit 1
        }
    ' ${files[@]+"${files[@]}"}
}

# Run as a command: `substitute.sh [in [out]]`, defaulting to stdin and stdout. Sourced: defines the
# function and nothing else. ⚠ `${BASH_SOURCE[0]}` against `$0` is the portable spelling of "was I
# executed"; `return` outside a function is the other one and is an error under `set -e` on bash 3.2.
if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    set -euo pipefail
    substitute_manifest "${1:--}" "${2:--}"
fi
