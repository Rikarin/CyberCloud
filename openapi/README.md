# `openapi/` — generated, checked in, diffed

Every file here is **output**. `./build.sh Generate` writes them from the provider registry
(`src/CyberCloud.ResourceManager/Registry/`) and overwrites anything you type into them.

Two build gates read this directory, and they ask different questions:

| Gate | Question |
|---|---|
| **Generated surfaces** | Does regenerating produce these exact bytes? |
| **OpenAPI compatibility** | Does the new document break the one it replaces? |

[02 § ADR-012](../docs/plan/02-technology-decisions.md) makes the registry the one source and the
document the first of four generated surfaces; [21 § OpenAPI](../docs/plan/21-cli-and-sdks.md) makes
it *"a build artifact that is **diffed**"* and states the rules the second gate enforces — **adding
an optional field is fine, removing anything or narrowing a type is not**.

## The files

- `index.json` — the entry point. A valid OpenAPI 3.1 document with no paths of its own, whose
  `x-cybercloud-api-versions` lists the per-version documents. Always written, even when the registry
  is empty, so that *"the generator found nothing"* and *"the generator did not run"* are different
  states on disk.
- `{yyyy-MM-dd}.json` — one complete document per api-version. Api-versions are dates and are
  immutable ([08 § The provider registry](../docs/plan/08-resource-manager.md)); adding a field is a
  new date, not an edit here.

## Changing one

You do not. Change the provider's `Describe`, run `./build.sh Generate`, and commit what it wrote.

If the compatibility gate fails, that is the gate working: something removed or narrowed a shape a
published api-version promised. The fix is a **new api-version**, not a smaller diff.

## Deleting one

Also not, without the 12-month notice window
[08 § The provider registry](../docs/plan/08-resource-manager.md) requires. A document here that the
registry no longer produces is reported as stale and fails both gates — deliberately, because a
published contract vanishing in a build step is the failure that window exists to prevent.
