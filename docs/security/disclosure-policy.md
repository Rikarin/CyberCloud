# Vulnerability disclosure policy

How to tell us about a security problem in Cyber Cloud, what you may and may not do while finding
one, and what we commit to doing once you have. This is the document `Policy:` in
`/.well-known/security.txt` points at, and the row of [docs/plan/18](../plan/18-security-vault-and-malware-scan.md)
§ Disclosure that was owed.

If you are here because something is on fire: **email `security@cybercloud.io`**. The rest of this
page can wait.

## Scope

In scope:

- This repository — every project under `src/`, `charts/`, `deploy/`, `portal/`, and `cli/`, on the
  `master` branch and in any released tag.
- The platform as deployed at its public origins: the gateway (`api.cybercloud.io`), the identity
  host (the login origin), and the portal.
- The container images and Helm charts this repository builds.

Out of scope:

- Third-party services we depend on but do not operate (the package registries, GitHub itself, the
  upstream projects the managed services wrap). Report those to their owners; tell us too if the
  issue reaches us through them.
- Denial of service, volumetric or otherwise. Rate limits and the per-IP shed are documented in
  [docs/plan/10](../plan/10-gateway-and-api.md) § Rate limiting; finding that they exist is not a finding.
- Social engineering of anyone, physical attacks, and anything that needs a stolen credential to
  start.
- Findings that amount to a version number or a missing header with no demonstrated impact.
- ⚠ **Another tenant's data.** If a test you are running shows you data that belongs to a tenant
  you do not control, that *is* a finding — and the moment you can see it, stop. Do not read
  further, do not copy it, and report it with the request id rather than the content.

## How to report

Send an email to `security@cybercloud.io`. Include what you can of:

- The affected component and version — a commit, a tag, or the `x-cybercloud-request-id` header
  from a response, which is on every response the gateway sends.
- Steps to reproduce, or a proof of concept. A minimal one is worth more than a complete one.
- Your assessment of the impact, and what you think the severity is. We will make our own, and we
  will tell you if it differs and why.
- How you would like to be credited, if at all.

Write in English if you can; we read others, more slowly.

There is no PGP key published yet. Do not put exploit detail in a subject line. If the report needs
encrypted transport, say so in a first message with no detail in it and we will arrange a channel
before you send the rest.

## Safe harbour

Research carried out in line with this policy is authorized. Concretely, if you:

- make a good-faith effort to stay within the scope above,
- stop and report as soon as you reach data that is not yours, and keep none of it,
- do not degrade the service for anyone else while you test,
- do not use a finding to pivot beyond what is needed to show it exists, and
- give us the time set out below before publishing,

then we will not pursue or support legal action against you for that research, we will work with you
to understand and resolve the issue, and we will not report you to law enforcement for it. If a third
party pursues you for research that follows this policy, we will make it known that it was authorized.

If you are unsure whether something is in bounds, ask first. That email is free.

## Triage SLA

Clocks start when the report arrives at `security@cybercloud.io`. Business days are Monday to Friday,
European time.

| Severity | Acknowledge | Triage — severity confirmed, owner named | Fix target |
|---|---|---|---|
| Critical | 1 business day | 3 business days | 7 calendar days |
| High | 1 business day | 5 business days | 30 calendar days |
| Medium | 2 business days | 10 business days | 90 calendar days |
| Low | 2 business days | 10 business days | The next scheduled release, within 180 days |

Severity follows CVSS v3.1 bands (Critical 9.0–10.0, High 7.0–8.9, Medium 4.0–6.9, Low 0.1–3.9),
adjusted for what the issue reaches in *this* platform: anything that lets one tenant read or write
another tenant's data is Critical regardless of the score, because tenant isolation is the
non-negotiable of [docs/plan/00](../plan/00-vision-and-principles.md) § Non-negotiables.

A fix target is a target. If we are going to miss one we will say so before the date, with the
reason and a new date — a missed target with no message is a policy failure, and you should say so.

## Coordinated disclosure

We ask for **90 days** from acknowledgement before public disclosure, or until a fix is released,
whichever comes first. We will:

- keep you informed at each row of the table above, and when the fix ships;
- publish an advisory for anything Medium or above, through GitHub Security Advisories on this
  repository, crediting you as you asked;
- tell you before we publish, and share the draft if you want to check it.

If a fix cannot ship within 90 days we will ask for more time and explain why; if you are not
willing to give it, we will not hold that against you, and the safe harbour still stands.

## What is not here yet

Stated so it can be read as a commitment rather than discovered as a gap:

- **No bounty.** docs/plan/18 § Disclosure puts one at GA. Until then, credit and thanks.
- **No PGP key.** See § How to report for how to get an encrypted channel.
- **`security.txt` is served from the gateway, not the apex.** RFC 9116 puts the file on the
  organizational domain; no ingress in this repository terminates one yet. It is at
  `https://api.cybercloud.io/.well-known/security.txt`, and its `Canonical:` line says so. Serving
  it at the apex is owed on that ingress.
- **`Expires` is gated.** The build fails thirty days before the file lapses
  (`SecurityTxtTests.ExpiresIsAtLeastThirtyDaysOut`), so a stale file is a red build rather than a
  quiet one.
