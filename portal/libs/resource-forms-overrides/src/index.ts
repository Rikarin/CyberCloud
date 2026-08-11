import { ResourceFormOverride, ResourceSchemaKey } from '@cybercloud/resource-forms';

/**
 * `libs/resource-forms-overrides` — hand-written forms that replace a generated one. **A stub at
 * M1**, holding only the registry and the rule.
 *
 * docs/plan/20 § The shape that makes 100 resource types affordable: "A hand-written form may
 * replace the generated one, keyed by `(resourceType, apiVersion)` … Expect ~10 of these — the
 * resources people create daily."
 *
 * ⚠ The registry is keyed by type **and** version, not by type. An override written against
 * `2026-01-01` silently applied to `2026-06-01` is a form that accepts fields the newer API
 * rejects, which is the failure mode the next paragraph of that section is about.
 */

const key = (k: ResourceSchemaKey): string => `${k.resourceType}@${k.apiVersion}`;

/**
 * ⚠ Registration is where the mandatory test hangs off. docs/plan/20: "**Every override must render
 * the same schema**, verified by a test that submits the override's output against the schema. An
 * override that accepts something the API rejects is worse than the generated form." Every entry
 * added here must come with that test — the override is not the escape hatch from the schema, only
 * from the generated *rendering* of it.
 */
export class ResourceFormOverrideRegistry {
  private readonly overrides = new Map<string, ResourceFormOverride>();

  register(override: ResourceFormOverride): void {
    this.overrides.set(key(override.key), override);
  }

  /** `undefined` means "render the generated form", which is the common case by design. */
  find(k: ResourceSchemaKey): ResourceFormOverride | undefined {
    return this.overrides.get(key(k));
  }

  /** Everything registered — what the conformance test iterates. */
  all(): readonly ResourceFormOverride[] {
    return [...this.overrides.values()];
  }
}
