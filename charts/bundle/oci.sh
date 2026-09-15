#!/usr/bin/env bash
#
# charts/bundle/oci.sh — one resolver from an image tag to the digest its registry serves today,
# sourced by install.sh and images.sh. charts/bundle/README.md § What this bundle pulls.
#
# ⚠ ONE COPY, SOURCED BY TWO SCRIPTS, AND THAT IS A DELIBERATE EXCEPTION TO THE RULE THE TWO READERS
# FOLLOW. install.sh and images.sh each carry their own `key()` and `helm_sets()` rather than sharing
# one, because a reader is a claim about the component.yaml FORMAT and the Bundle gate pins that
# format for both. This function is not a reader: it is the OCI distribution token dance, and it has
# to return the same digest to the script that RECORDS a pin (`images.sh --resolve`) and to the script
# that REFUSES to install against a moved one (install.sh). Two copies of a token dance are two
# answers the day one of them is fixed — issue #17 is about a record that nothing consumed, and a
# consumer that resolves differently from the recorder is the same defect with an extra step.
#
# ⚠ NOTHING HERE RUNS ON SOURCE. This file defines functions and sets one variable. A script that
# sources it gets `digest_of` and nothing else happens; a person who runs it gets nothing at all.
#
# ⚠ curl and nothing else, for the reason install.sh gives about yq: the machine that has to answer
# "what is this bundle about to pull" is frequently the machine that has nothing installed. This is
# the OCI distribution token dance — an unauthenticated request for the challenge, a token from the
# realm it names, then a HEAD whose Docker-Content-Digest header is the answer. Exercised firsthand
# on 2026-09-03 against quay.io, ghcr.io, registry.k8s.io and docker.io, and again on 2026-09-15
# through install.sh against all thirty-two recorded references.
#
# ⚠ `-L` on both requests, and it is not decoration: registry.k8s.io answers 307 and a resolver
# without it reports every Kubernetes-hosted image as unresolvable — which reads as a broken pin.
#
# ⚠ THE ACCEPT HEADER DECIDES WHICH DIGEST COMES BACK, AND THE RECORD IS WRITTEN AGAINST THIS ONE.
# A multi-architecture tag points at an INDEX, and the index has one digest while each platform's
# manifest under it has another. Asking for the index media types first is what makes the answer the
# digest a kubelet resolves the tag to; `docker manifest inspect -v` reports the platform manifest's
# digest instead, and comparing that against a component.yaml reports every multi-arch image as
# moved. Measured on 2026-09-15 against quay.io/jetstack/cert-manager-controller:v1.21.1: the index
# is sha256:416a2d76…, which is what the record holds, and the linux/amd64 manifest under it is
# sha256:4c2b5201….

oci_accept='application/vnd.oci.image.index.v1+json,application/vnd.oci.image.manifest.v1+json,application/vnd.docker.distribution.manifest.list.v2+json,application/vnd.docker.distribution.manifest.v2+json'

# digest_of <reference> — prints the digest the registry serves for the reference's tag, or nothing.
#
# The reference may carry an `@sha256:…` suffix; it is dropped, because the question is what the TAG
# serves today and not whether the digest exists. A reference with no tag is `latest`, exactly as it
# is for a kubelet.
digest_of() {
    local ref="$1" registry repo tag host challenge realm service scope token headers digest

    ref="${ref%%@*}"

    if [[ "${ref##*/}" == *:* ]]; then
        tag="${ref##*:}"
        repo="${ref%:*}"
    else
        tag="latest"
        repo="$ref"
    fi

    if [[ "${repo%%/*}" == *.* || "${repo%%/*}" == "localhost" ]]; then
        registry="${repo%%/*}"
        repo="${repo#*/}"
    else
        registry="docker.io"
    fi

    host="$registry"
    if [[ "$registry" == "docker.io" ]]; then
        host="registry-1.docker.io"
        [[ "$repo" == */* ]] || repo="library/$repo"
    fi

    challenge=$(curl -sSL -o /dev/null -D - --max-time 60 "https://$host/v2/$repo/manifests/$tag" \
        | tr -d '\r' | sed -n 's/^[Ww][Ww][Ww]-[Aa]uthenticate: *//p' | head -1)

    token=""
    if [[ "$challenge" == Bearer* ]]; then
        realm=$(printf '%s' "$challenge" | sed -n 's/.*realm="\([^"]*\)".*/\1/p')
        service=$(printf '%s' "$challenge" | sed -n 's/.*service="\([^"]*\)".*/\1/p')
        scope=$(printf '%s' "$challenge" | sed -n 's/.*scope="\([^"]*\)".*/\1/p')
        [[ -n "$scope" ]] || scope="repository:$repo:pull"
        token=$(curl -sS --max-time 60 "$realm?service=$service&scope=$scope" \
            | sed -n 's/.*"token"[: ]*"\([^"]*\)".*/\1/p')
    fi

    headers=$(curl -sSL -o /dev/null -D - --max-time 60 -H "Accept: $oci_accept" \
        ${token:+-H "Authorization: Bearer $token"} \
        "https://$host/v2/$repo/manifests/$tag" | tr -d '\r')

    digest=$(printf '%s' "$headers" | sed -n 's/^[Dd]ocker-[Cc]ontent-[Dd]igest: *//p' | head -1)
    printf '%s' "$digest"
}
