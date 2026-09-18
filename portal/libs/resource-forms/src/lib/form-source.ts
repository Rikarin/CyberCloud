import { HttpClient } from '@angular/common/http';
import { Injectable, InjectionToken, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { FormsDocument, ResourceForm, ScopeForm, ScopeFormKind } from './schema';

/** Identifies which form a page is for. Both halves are required; a type without a version is ambiguous. */
export interface ResourceSchemaKey {
  readonly resourceType: string;
  readonly apiVersion: string;
}

/**
 * Where the form documents are served from, by api-version.
 *
 * ⚠ **Fetched at runtime, never imported — and this token is what makes that a property of the
 * build rather than a habit.** docs/plan/20 § Performance budget: "the generated form renderer must
 * not pull every schema into the main bundle. Schemas are fetched per type, cached, and versioned
 * by the api-version". A `import forms from '…/2026-08-01.json'` anywhere in the portal would put
 * 250 KB of schema in a chunk; a URL cannot be imported. `angular.json` copies
 * `generated/forms/*.json` into the build as static assets under `/forms/`, which is the same
 * mechanism that will serve them from `CyberCloud.Portal.Host` once it exists.
 */
export const FORMS_BASE_PATH = new InjectionToken<string>('cc.forms.basePath', {
  providedIn: 'root',
  factory: () => '/forms'
});

/**
 * Fetches and caches the form documents.
 *
 * One request per api-version for the lifetime of the injector — the document is 250 KB and every
 * create blade would otherwise refetch it. ⚠ Per-injector, therefore per-request under SSR, like
 * every store in `libs/shell`; a module-level cache would be shared across tenants' renders, and
 * while a schema is not tenant data, the discipline is not worth an exception.
 */
@Injectable({ providedIn: 'root' })
export class ResourceFormSource {
  private readonly http = inject(HttpClient);
  private readonly basePath = inject(FORMS_BASE_PATH);
  private readonly documents = new Map<string, Promise<FormsDocument>>();

  /** The whole document for one api-version. */
  document(apiVersion: string): Promise<FormsDocument> {
    let pending = this.documents.get(apiVersion);

    if (pending === undefined) {
      pending = firstValueFrom(this.http.get<FormsDocument>(`${this.basePath}/${encodeURIComponent(apiVersion)}.json`));
      // A failed fetch is not cached: the next blade to ask gets a fresh attempt.
      pending.catch(() => this.documents.delete(apiVersion));
      this.documents.set(apiVersion, pending);
    }

    return pending;
  }

  /** The form for one resource type, or `undefined` when the api-version has no such type. */
  async form(key: ResourceSchemaKey): Promise<ResourceForm | undefined> {
    return (await this.document(key.apiVersion)).forms[key.resourceType];
  }

  /** The form for a management group, a subscription or a resource group. */
  async scopeForm(kind: ScopeFormKind, apiVersion: string): Promise<ScopeForm> {
    return (await this.document(apiVersion)).scopeForms[kind];
  }

  /** Every resource type the api-version has a form for, in document order. */
  async types(apiVersion: string): Promise<readonly ResourceForm[]> {
    return Object.values((await this.document(apiVersion)).forms);
  }
}
