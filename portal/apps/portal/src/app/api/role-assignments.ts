import { Injectable, inject } from '@angular/core';
import { ApiResponse } from '@cybercloud/api';
import { splitType } from '../routes/portal-links';
import { HttpApiTransport } from './http-transport';
import { ResourceAddress } from './resource-verbs';

/**
 * The roles a `PUT` may grant — `RoleAssignmentService.GrantableRoles`, spelled as the tuple store
 * spells the relation. docs/plan/07 § Azure RBAC, expressed in it: the first three are the
 * control-plane roles; the four `keyVault*` roles are the key-vault data plane's (docs/plan/18),
 * which no control-plane role implies. A deny assignment is a different resource type.
 */
export const roles = [
  'owner',
  'contributor',
  'reader',
  'keyVaultSecretsOfficer',
  'keyVaultSecretsUser',
  'keyVaultCryptoOfficer',
  'keyVaultCryptoUser'
] as const;
export type Role = (typeof roles)[number];

/**
 * The principal types — `RoleAssignmentService.PrincipalTypes`: the ReBAC subject types plus
 * `group`, which the store writes as the userset `group:{id}#member`.
 *
 * ⚠ Matched ordinally on the platform. `ServicePrincipal` or `service-principal` is a 400, and the
 * page offers these four rather than a text field for that reason.
 */
export const principalTypes = ['user', 'servicePrincipal', 'managedIdentity', 'group'] as const;
export type PrincipalType = (typeof principalTypes)[number];

/**
 * `RelationNaming.IdPattern`, which is `ResourceNaming.Pattern`: 1–63 characters of lowercase
 * letters, digits and hyphens, starting and ending alphanumeric. The 32-digit lowercase `N` form
 * of a GUID satisfies it, which is the normal case.
 */
export const PRINCIPAL_ID = /^[a-z0-9]([-a-z0-9]*[a-z0-9])?$/;
export const PRINCIPAL_ID_MAX_LENGTH = 63;

/**
 * The three parts of an assignment's name — `RoleAssignmentName` in `CyberCloud.Core`.
 *
 * ⚠ **The name is derived from the tuple, not chosen.** `{role}-{principalType}-{principalId}`
 * — `reader-user-7f3c…` — is the one place the address departs from Azure's client-minted GUID,
 * and it is why a repeated `PUT` is the same assignment rather than a second one. docs/plan/07
 * § Azure RBAC, expressed in it carries the argument; issue #86 records it as a decision that
 * may yet be re-taken against a Terraform provider. The portal never asks for a name: it derives
 * one from what the user picked and shows it, so what is sent is what is seen.
 */
export interface RoleAssignmentName {
  readonly role: Role;
  readonly principalType: PrincipalType;
  readonly principalId: string;
}

/** `RoleAssignmentName.Render`: the three parts joined by `-`. */
export function renderName(name: RoleAssignmentName): string {
  return `${name.role}-${name.principalType}-${name.principalId}`;
}

/**
 * `RoleAssignmentName.Parse`, for a name the platform served — `null` for one this page cannot
 * offer, which is a role or a principal type outside the closed sets.
 *
 * ⚠ The id may contain `-`; the role and the principal type cannot, so the split is at the first
 * two hyphens and never at the last.
 */
export function parseName(value: string): RoleAssignmentName | null {
  const first = value.indexOf('-');
  const second = first < 0 ? -1 : value.indexOf('-', first + 1);
  if (first <= 0 || second < 0 || second === first + 1 || second === value.length - 1) return null;

  const role = value.slice(0, first);
  const principalType = value.slice(first + 1, second);
  const principalId = value.slice(second + 1);

  if (!isRole(role) || !isPrincipalType(principalType)) return null;
  if (!PRINCIPAL_ID.test(principalId) || principalId.length > PRINCIPAL_ID_MAX_LENGTH) return null;

  return { role, principalType, principalId };
}

export function isRole(value: string): value is Role {
  return (roles as readonly string[]).includes(value);
}

export function isPrincipalType(value: string): value is PrincipalType {
  return (principalTypes as readonly string[]).includes(value);
}

/**
 * Where an assignment applies: a tenant, a subscription, a resource group or a resource —
 * `RoleAssignmentId`'s two halves, as the pieces a route carries.
 *
 * The tenant kind has no page yet (the portal has no tenant blade), and is here so the path
 * builder is the whole grammar rather than three quarters of it.
 */
export type AccessScope =
  | { readonly kind: 'tenant'; readonly tenantId: string }
  | { readonly kind: 'subscription'; readonly tenantId: string; readonly subscriptionId: string }
  | {
      readonly kind: 'resourceGroup';
      readonly tenantId: string;
      readonly subscriptionId: string;
      readonly resourceGroup: string;
    }
  | { readonly kind: 'resource'; readonly address: ResourceAddress; readonly resourceType: string };

const seg = encodeURIComponent;

/**
 * The scope's own path — docs/plan/06 § Identifiers, every segment encoded as
 * `CyberCloudApi.segment` encodes it.
 */
export function scopePath(scope: AccessScope): string {
  switch (scope.kind) {
    case 'tenant':
      return `/tenants/${seg(scope.tenantId)}`;
    case 'subscription':
      return `/tenants/${seg(scope.tenantId)}/subscriptions/${seg(scope.subscriptionId)}`;
    case 'resourceGroup':
      return `${scopePath({ kind: 'subscription', tenantId: scope.tenantId, subscriptionId: scope.subscriptionId })}/resourceGroups/${seg(scope.resourceGroup)}`;
    case 'resource': {
      const { address, resourceType } = scope;
      const { provider, segments } = splitType(resourceType);
      const path: string[] = [];

      segments.forEach((segment, i) => {
        path.push(seg(segment));
        path.push(seg(i < segments.length - 1 ? (address.parents[i] ?? '') : address.name));
      });

      return `${scopePath({ kind: 'resourceGroup', tenantId: address.tenantId, subscriptionId: address.subscriptionId, resourceGroup: address.resourceGroup })}/providers/${seg(provider)}/${path.join('/')}`;
    }
  }
}

/** `RoleAssignmentId.Suffix`, the reserved namespace and type segment that follow every scope. */
export const ROLE_ASSIGNMENTS_SUFFIX = '/providers/CyberCloud.Authorization/roleAssignments/';

/** The full address: the scope, the suffix, and the derived name. */
export function assignmentPath(scope: AccessScope, name: RoleAssignmentName): string {
  return scopePath(scope) + ROLE_ASSIGNMENTS_SUFFIX + seg(renderName(name));
}

/**
 * An assignment as `ResponseBodies.RoleAssignment` renders one: Azure's envelope, with the three
 * body properties under `properties` so that a `GET` can be sent back as a `PUT` unchanged.
 * `roleDefinitionId` is a role *name*; there are no role definitions to address.
 */
export interface RoleAssignment {
  readonly id: string;
  readonly name: string;
  readonly type: 'CyberCloud.Authorization/roleAssignments';
  readonly properties: {
    readonly scope: string;
    readonly principalId: string;
    readonly principalType: string;
    readonly roleDefinitionId: string;
  };
}

/**
 * The role-assignment API, by hand.
 *
 * ⚠ **Not in `libs/api`, and not because it was forgotten.** `CyberCloud.Authorization` is a
 * reserved namespace the registry refuses, so the emitters that read the registry know nothing
 * of `{scope}/providers/CyberCloud.Authorization/roleAssignments/{name}` — docs/plan/10 § Shape
 * calls that "#63's question asked a third time". Until an emitter learns the address as a
 * third non-registry source, this is the portal's one hand-written client, and it goes through
 * the same `HttpApiTransport` the generated one does so that the bearer token, the api-version
 * and the error mapping are owned once. The day the client is generated, the three methods here
 * become one-line delegations and the page does not change.
 *
 * ⚠ **`201` on a grant, `200` on a repeat, `204` on a revoke, and no `202` anywhere.** An
 * assignment is one tuple write and converges before the call returns, so there is no operation
 * to hand to the operation view — `DispatchStage.RoleAssignmentAsync` says so. `status` is what
 * tells a grant from a repeat, and the page reads it.
 *
 * ⚠ **There is no `list`.** The collection address answers 400 (issue #86); what is assigned at
 * a scope lives only on `ICheckGrain`'s role-assignment view, which reaches no HTTP address.
 * The page says so rather than inventing an empty list.
 */
@Injectable({ providedIn: 'root' })
export class RoleAssignmentsApi {
  private readonly transport = inject(HttpApiTransport);

  /**
   * `PUT`: grants the role. The body carries the three properties the address already spells,
   * which the platform checks against it — `RoleAssignmentService.BodyAgrees` refuses a body that
   * disagrees rather than trusting either one, so sending them is a self-check and not a
   * redundancy.
   */
  assign(scope: AccessScope, name: RoleAssignmentName): Promise<ApiResponse<RoleAssignment>> {
    return this.transport.send<RoleAssignment>({
      method: 'PUT',
      path: assignmentPath(scope, name),
      body: {
        principalId: name.principalId,
        principalType: name.principalType,
        roleDefinitionId: name.role
      }
    });
  }

  /** `GET`: reads one assignment. A 404 is "not assigned", and also "not yours to see". */
  read(scope: AccessScope, name: RoleAssignmentName): Promise<ApiResponse<RoleAssignment>> {
    return this.transport.send<RoleAssignment>({ method: 'GET', path: assignmentPath(scope, name) });
  }

  /**
   * `DELETE`: revokes the role. `204` with no body, and a repeated revoke is still a `204` —
   * `RoleAssignmentTests.ARevokeRemovesAccessAndARepeatedRevokeIsStillASuccess`.
   */
  revoke(scope: AccessScope, name: RoleAssignmentName): Promise<ApiResponse<void>> {
    return this.transport.send<void>({ method: 'DELETE', path: assignmentPath(scope, name) });
  }
}
