# Security

Found a vulnerability in Cyber Cloud? **Email `security@cybercloud.io`.** Do not open a public
issue for it.

The full policy — scope, safe harbour, what to include, and the triage SLA we hold ourselves to — is
[docs/security/disclosure-policy.md](docs/security/disclosure-policy.md). The short version:

| Severity | Acknowledge | Triage | Fix target |
|---|---|---|---|
| Critical | 1 business day | 3 business days | 7 days |
| High | 1 business day | 5 business days | 30 days |
| Medium | 2 business days | 10 business days | 90 days |
| Low | 2 business days | 10 business days | Next scheduled release, within 180 days |

Research within the policy's scope is authorized and we will not pursue legal action for it. We ask
for 90 days before public disclosure, and we credit reporters unless they ask us not to.

The machine-readable form is the RFC 9116 file the gateway serves at
`https://api.cybercloud.io/.well-known/security.txt`; its source is
[`src/Hosts/CyberCloud.Gateway.Host/WellKnown/security.txt`](src/Hosts/CyberCloud.Gateway.Host/WellKnown/security.txt).
Its `Expires` is gated by the build, so it cannot lapse on `master` without the build going red first.

There is no bug bounty yet and no PGP key published yet; the policy says how to get an encrypted
channel if a report needs one.
