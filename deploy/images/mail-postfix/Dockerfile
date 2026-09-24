# Postfix for CyberCloud.Mail/domains — the one of the three mail daemons with no upstream image.
#
# ⚠ WHY THIS FILE EXISTS AND THE OTHER TWO DO NOT. Dovecot publishes docker.io/dovecot/dovecot and
# Rspamd publishes docker.io/rspamd/rspamd, both from the projects' own repositories, and the mail
# back end runs those unmodified. Postfix publishes no image at all, and every third-party one bakes
# in an entrypoint that rewrites main.cf from environment variables — which would fight the main.cf
# MailDomains.PostfixMainCf renders. So this image is Debian's own postfix package and nothing else:
# no entrypoint, no configuration, no opinion. What makes it a mail back end is the start script the
# domain's ConfigMap mounts (MailDomains.PostfixStartScript), which is also why the pod spec names a
# command rather than relying on this CMD.
#
# ⚠ BUILT BY NOTHING BUT THE TEST THAT NEEDS IT. MailDeliveryOnK3sTests builds it with Testcontainers
# and imports it into its own k3s; no pipeline pushes it to ghcr.io/rikarin/cybercloud/mail-postfix.
# That is charts/managed/mail/conformance.yaml § owed, `the-postfix-image-is-not-published`.
# ⚠ NOT docker.io/cybercloud, which the first cut named: that Docker Hub organisation belongs to an
# unrelated party, so an unpublished tag there was a tag somebody else could publish.
#
# ⚠ debian:13-slim, the base the Dovecot 2.4 image is also built on, so the three containers of one
# pod share a libc and a CA bundle. The postfix package version is whatever trixie ships on the day
# of the build — pinned by the base tag, not by a version string, and marked unverified the same way
# every other pin in MailDomains is.
FROM debian:13-slim

ARG DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
    && apt-get install -y --no-install-recommends postfix ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && postconf -e "maillog_file = /dev/stdout"

EXPOSE 25 587

CMD ["postfix", "start-fg"]
