# `references/` — read-only, not built, not restored

This directory exists so that "how did Cozystack wire CloudNativePG" is a `grep` away rather than a
browser tab away.

```
references/
├── cozystack/              # ADR-010 — the operator survey lives here as a grep target
├── orleans-multitenant/    # ADR-002
├── malware-multiscan/      # github.com/Rikarin/MalwareMultiScan — design reference (docs/plan/18)
└── survival/               # symlink → ~/Projects/Survival/Server — the Orleans reference
```

## Nothing here is our code

No assembly in [`src/`](../src) may reference anything in here. Nothing here is held to our coding
standards, our target framework, or our package versions. Contents are `.gitignore`d — clone or
symlink what you need locally.

## The four opt-out barrier files, and why all four are needed

`Directory.Build.props`, `Directory.Build.targets` and `Directory.Packages.props` at the repository
root apply to **every descendant directory**. Without a barrier, cloning a reference repo in here
would graft `net10.0`, `TreatWarningsAsErrors` and Central Package Management onto someone else's
source tree, and a root `dotnet build` would try to build it.

| File | Stops |
|---|---|
| `Directory.Build.props` | root properties; also switches CPM off so a versioned `PackageReference` is not NU1008 |
| `Directory.Build.targets` | root targets — intentionally empty |
| `Directory.Packages.props` | the root package-version list |
| `.editorconfig` (`root = true`) | the root `.editorconfig` |

**The `.editorconfig` is not redundant.** EditorConfig and MSBuild are independent inheritance
chains. Verified during bring-up: with only the three MSBuild barriers in place, a reference project
still failed to compile with `error CS0219`, because the root `.editorconfig` had raised CS0219 to
`error` even though `TreatWarningsAsErrors` was not applied. All four must stay.

See [docs/plan/03](../docs/plan/03-repository-layout.md).
