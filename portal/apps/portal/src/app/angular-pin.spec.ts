import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

/**
 * The Angular pin, as portal/README.md § The Angular pin states it, enforced as a test.
 *
 * Three claims, each of which used to hold only because someone remembered it during review:
 *
 * 1. The framework — `@angular/core` and its siblings, `@angular/compiler-cli`, `@angular/cdk` —
 *    is one exact version. That is pnpm-workspace.yaml's single-version policy; two copies of
 *    `@angular/core` in one tree is a broken app.
 * 2. That version is the one xUI compiled against. docs/plan/02 § ADR-017: "The portal does not
 *    choose the Angular version; it follows xUI." Every `@xui/*` bundle carries the version of the
 *    compiler that produced it in its partial-compilation declarations, so what xUI is tested
 *    against is readable from `node_modules`, not from a release note or a checkout.
 * 3. The tooling — `@angular/cli`, `@angular/build`, `@angular/ssr` — is one exact version of its
 *    own. It is published from a different repository on a different cadence, `@angular/build`
 *    peers `@angular/ssr` at its own minor, and it is not the framework's number: xUI's own tag
 *    pins its tooling two patch releases past its framework, and README § The Angular pin says
 *    why the portal could not pin the framework's number even if it wanted to.
 *
 * ⚠ The second claim is a policy, not a compile-time constraint, and this test exists because
 * nothing else would catch a breach. Angular's linker accepts a range of compiler versions — #26
 * took 3.0.0's 22.1.4 bundles with the framework still at 22.0.8, and its gate was green — so a
 * drift in either direction builds and passes. When this test fails, one of two things happened: Angular was
 * moved ahead of xUI, in which case the move is xUI's first (ADR-017), or xUI was bumped to a
 * release compiled by a newer Angular, in which case the pin moves with it and README § The Angular
 * pin is re-measured. The failure message names both versions so the reader knows which.
 */

const workspace = join(__dirname, '..', '..', '..', '..');

interface Manifest {
  readonly dependencies: Readonly<Record<string, string>>;
  readonly devDependencies: Readonly<Record<string, string>>;
}

const manifest = JSON.parse(readFileSync(join(workspace, 'package.json'), 'utf8')) as Manifest;

const tooling = ['@angular/cli', '@angular/build', '@angular/ssr'];

const angularPins = Object.entries({ ...manifest.dependencies, ...manifest.devDependencies }).filter(([name]) =>
  name.startsWith('@angular/')
);
const frameworkPins = angularPins.filter(([name]) => !tooling.includes(name));
const toolingPins = angularPins.filter(([name]) => tooling.includes(name));

/** The packages grouped by the version they pin — one key when the policy holds, more when it does not. */
function byVersion(pins: readonly (readonly [string, string])[]): Record<string, string[]> {
  const groups: Record<string, string[]> = {};
  for (const [name, version] of pins) (groups[version] ??= []).push(name);
  return groups;
}

/**
 * Every `version: "x.y.z"` stamp in every installed `@xui/*` bundle, deduplicated.
 *
 * The stamp is the `version` field of `ɵɵngDeclareComponent({ minVersion, version, … })` and its
 * siblings — the compiler that produced the bundle, not the runtime it needs. `minVersion` does not
 * match: the pattern is anchored on a word boundary and the case differs.
 */
function xuiCompilerVersions(): ReadonlySet<string> {
  const root = join(workspace, 'node_modules', '@xui');
  const stamps = new Set<string>();

  for (const pkg of readdirSync(root)) {
    const bundles = join(root, pkg, 'fesm2022');
    for (const file of readdirSync(bundles).filter(f => f.endsWith('.mjs'))) {
      for (const match of readFileSync(join(bundles, file), 'utf8').matchAll(/\bversion: "(\d+\.\d+\.\d+)"/g)) {
        stamps.add(match[1]);
      }
    }
  }

  return stamps;
}

describe('The Angular pin', () => {
  it('declares the framework, the CDK and the tooling', () => {
    expect(frameworkPins.map(([name]) => name)).toEqual(
      expect.arrayContaining(['@angular/core', '@angular/compiler-cli', '@angular/cdk'])
    );
    expect(toolingPins.map(([name]) => name).sort()).toEqual([...tooling].sort());
  });

  it('pins the framework to one exact version — pnpm-workspace.yaml § Single-version policy', () => {
    const groups = byVersion(frameworkPins);

    // Compared as the whole grouping so a failure lists the packages on each side of the split.
    expect(groups).toEqual({ [frameworkPins[0][1]]: expect.any(Array) });
    expect(frameworkPins[0][1]).toMatch(/^\d+\.\d+\.\d+$/);
  });

  it('pins the tooling to one exact version of its own', () => {
    const groups = byVersion(toolingPins);

    expect(groups).toEqual({ [toolingPins[0][1]]: expect.any(Array) });
    expect(toolingPins[0][1]).toMatch(/^\d+\.\d+\.\d+$/);
  });

  it('runs the framework xUI compiled against — docs/plan/02 § ADR-017', () => {
    const compiledBy = [...xuiCompilerVersions()];

    // One version across every bundle is itself a finding worth keeping: xUI publishes all of its
    // packages from one build, so a second stamp here would mean a partial release.
    expect(compiledBy).toHaveLength(1);

    // The portal's side is the whole grouping, not the first entry's version: with only the CDK
    // drifted to 22.1.6, the first entry says 22.1.4 and a message built from it would report the
    // framework as pinning the stamp it already pins. The grouping names the package that moved.
    expect({ portalPins: byVersion(frameworkPins), xuiCompiledBy: compiledBy[0] }).toEqual({
      portalPins: { [compiledBy[0]]: expect.any(Array) },
      xuiCompiledBy: compiledBy[0]
    });
  });
});
