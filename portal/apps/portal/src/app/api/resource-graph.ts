import { Injectable, inject } from '@angular/core';
import { HttpApiTransport } from './http-transport';

/** One result column, as the query's translator typed it. */
export interface GraphColumn {
  readonly name: string;
  readonly type: string;
}

/** One page of a resource graph query. */
export interface GraphPage {
  readonly columns: readonly GraphColumn[];
  readonly rows: readonly Readonly<Record<string, unknown>>[];
  /** The `$skipToken` of the next page, or `null` on the last one. */
  readonly skipToken: string | null;
}

/**
 * The resource graph's one address — `POST /tenants/{t}/providers/CyberCloud.ResourceGraph/resources`
 * with a KQL body (#54, docs/plan/08 § The resource-graph projection).
 *
 * ⚠ **Hand-written, like `RoleAssignmentsApi`, and for the same reason.** The address is under a
 * reserved namespace the provider registry does not hold, so no emitter publishes it — docs/plan/10
 * § Shape records it as #63's question asked a fourth time — and the generated client has no method
 * for it. This builds the one path and sends it through the same `HttpApiTransport` the generated
 * client uses, so the token, the `api-version` and the error shape are the same as every other call.
 *
 * ⚠ **The next page is followed by its token, never by its URL.** The platform answers a `nextLink`
 * that is an absolute URL on its public base; POSTing to a server-supplied URL would put an origin
 * the server chose in front of the user's bearer token — `operationIdOf`'s argument. The token is read
 * off the link's query string and sent to the one address this class builds, with the same query.
 */
@Injectable({ providedIn: 'root' })
export class ResourceGraphApi {
  private readonly transport = inject(HttpApiTransport);

  async query(tenantId: string, kql: string, top: number, skipToken: string | null = null): Promise<GraphPage> {
    const body: Record<string, unknown> = { query: kql, $top: top };
    if (skipToken !== null) body['$skipToken'] = skipToken;

    const response = await this.transport.send<unknown>({ method: 'POST', path: graphPath(tenantId), body });

    return asGraphPage(response.value);
  }
}

/** The query address for a tenant. */
export function graphPath(tenantId: string): string {
  return `/tenants/${encodeURIComponent(tenantId)}/providers/CyberCloud.ResourceGraph/resources`;
}

/** The `$skipToken` a `nextLink` carries, or `null`. */
export function skipTokenOf(nextLink: unknown): string | null {
  if (typeof nextLink !== 'string' || nextLink.length === 0) return null;

  try {
    return new URL(nextLink, 'https://unused.invalid').searchParams.get('$skipToken');
  } catch {
    return null;
  }
}

/** Checks a page's shape. */
export function asGraphPage(value: unknown): GraphPage {
  const record = typeof value === 'object' && value !== null ? (value as Record<string, unknown>) : null;
  const columns = record?.['columns'];
  const rows = record?.['value'];

  if (!Array.isArray(columns) || !Array.isArray(rows)) {
    throw new Error(
      $localize`:@@graph.unexpected:The resource graph answered in a shape this page does not recognise.`
    );
  }

  return {
    columns: columns.map(c => ({ name: String((c as GraphColumn).name), type: String((c as GraphColumn).type) })),
    rows: rows as Record<string, unknown>[],
    skipToken: skipTokenOf(record?.['nextLink'])
  };
}
