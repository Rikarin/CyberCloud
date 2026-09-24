import { Injectable, inject } from '@angular/core';
import { ApiResponse } from '@cybercloud/api';
import { HttpApiTransport } from './http-transport';

/**
 * The identity addresses — `IdentityAddress` on the platform: everything under
 * `/tenants/{t}/providers/CyberCloud.Identity/`. #43 served the invitations collection; #41 added
 * the members, the applications, the caller's own sessions, and the verbs on each.
 */
export const IDENTITY_NAMESPACE = '/providers/CyberCloud.Identity';

const seg = encodeURIComponent;

/** The collection path for one kind, in one tenant. */
export function identityPath(
  tenantId: string,
  collection: 'invitations' | 'members' | 'applications' | 'sessions',
  id?: string,
  verb?: 'resend' | 'rotateSecret'
): string {
  let path = `/tenants/${seg(tenantId)}${IDENTITY_NAMESPACE}/${collection}`;
  if (id !== undefined) path += `/${seg(id)}`;
  if (verb !== undefined) path += `/${verb}`;
  return path;
}

/** Azure's envelope, as `IdentityBodies` renders every object here. */
interface Envelope<T> {
  readonly id: string;
  /** The object's id, the 32-digit lower-case `N` form of a GUID. */
  readonly name: string;
  readonly type: string;
  readonly properties: T;
}

/** `{ value: [ … ] }` — no `nextLink`: every list is one directory index, read whole. */
export interface IdentityList<T> {
  readonly value: readonly T[];
}

export type MemberStatus = 'invited' | 'active' | 'suspended' | 'deprovisioned';

export type Member = Envelope<{
  readonly email: string;
  readonly displayName: string;
  readonly status: MemberStatus;
  readonly createdAt: string;
}>;

export type InvitationStatus = 'pending' | 'accepted' | 'expired' | 'withdrawn' | 'revoked';

/** An invitation — never with its link, which went to the invited address and nowhere else. */
export type Invitation = Envelope<{
  readonly email: string;
  /** The user the invitation created — the principal id a role is granted to. */
  readonly userId: string;
  readonly status: InvitationStatus;
  readonly expiresAt: string;
  readonly invitedBy?: string;
  readonly sentAt?: string;
  readonly sendings?: number;
}>;

/**
 * A registered OAuth client. ⚠ `clientSecret` is present on exactly two answers — the
 * registration and a rotation — and only for a confidential client. The platform keeps its
 * digest and nothing returns it again, so the page shows it once and says so.
 */
export type Application = Envelope<{
  readonly clientId: string;
  readonly displayName: string;
  readonly redirectUris: readonly string[];
  readonly scopes: readonly string[];
  readonly publicClient: boolean;
  readonly createdAt: string;
  readonly clientSecretIssuedAt?: string;
  readonly clientSecret?: string;
}>;

/** One of the caller's sessions. `current` is the one the portal's own token belongs to. */
export type Session = Envelope<{
  readonly clientId: string;
  readonly deviceLabel: string;
  readonly createdAt: string;
  readonly lastUsedAt: string;
  readonly methods: readonly string[];
  readonly current: boolean;
}>;

/** What a registration asks for — `IdentityBodies.ApplicationDraft`'s four members, all required. */
export interface ApplicationDraft {
  readonly displayName: string;
  readonly redirectUris: readonly string[];
  readonly scopes: readonly string[];
  readonly publicClient: boolean;
}

/**
 * The scopes a registration may carry — `ApplicationPolicy.RegistrableScopes`, the four the
 * identity host registers. A fifth is refused by the application grain, so the page offers
 * these and no text field.
 */
export const registrableScopes = ['openid', 'profile', 'offline_access', 'cyc.api'] as const;

/**
 * The identity administration API, by hand.
 *
 * ⚠ **Not in `libs/api`, for `RoleAssignmentsApi`'s reason.** `CyberCloud.Identity` is a reserved
 * namespace the provider registry refuses, so the emitters that read the registry know nothing
 * of these addresses — docs/plan/10 § Shape records it as #63's question asked again. Until an
 * emitter learns a non-registry source for them, this goes through the same `HttpApiTransport`
 * the generated client does, so the bearer token, the api-version and the error mapping are owned
 * once; the day the client is generated, these methods become delegations and the pages don't
 * change.
 *
 * ⚠ **Every call but the session ones needs Owner on the tenant** — `assignRole`, checked fully
 * consistent by `IdentityAdministrationService`. A reader gets a 403 and anybody else the
 * canonical 404; the pages render both through `cc-page-status`.
 *
 * ⚠ **No `202` anywhere.** Each call is a handful of grain calls and converges before it returns,
 * so nothing here hands off to the operation view.
 */
@Injectable({ providedIn: 'root' })
export class IdentityAdminApi {
  private readonly transport = inject(HttpApiTransport);

  listMembers(tenantId: string): Promise<ApiResponse<IdentityList<Member>>> {
    return this.transport.send({ method: 'GET', path: identityPath(tenantId, 'members') });
  }

  /** `DELETE`: deprovisions the member and deletes every role they hold. `409` for yourself. */
  removeMember(tenantId: string, userId: string): Promise<ApiResponse<Member>> {
    return this.transport.send({ method: 'DELETE', path: identityPath(tenantId, 'members', userId) });
  }

  listInvitations(tenantId: string): Promise<ApiResponse<IdentityList<Invitation>>> {
    return this.transport.send({ method: 'GET', path: identityPath(tenantId, 'invitations') });
  }

  /** `POST { email }` — #43's address. `201` with the invitation, never its link. */
  invite(tenantId: string, email: string): Promise<ApiResponse<Invitation>> {
    return this.transport.send({ method: 'POST', path: identityPath(tenantId, 'invitations'), body: { email } });
  }

  /** Mails a new link; the old one stops working. */
  resendInvitation(tenantId: string, invitationId: string): Promise<ApiResponse<Invitation>> {
    return this.transport.send({ method: 'POST', path: identityPath(tenantId, 'invitations', invitationId, 'resend') });
  }

  /** Withdraws the invitation; the invited user stays invited, so a new invitation reuses them. */
  revokeInvitation(tenantId: string, invitationId: string): Promise<ApiResponse<Invitation>> {
    return this.transport.send({ method: 'DELETE', path: identityPath(tenantId, 'invitations', invitationId) });
  }

  listApplications(tenantId: string): Promise<ApiResponse<IdentityList<Application>>> {
    return this.transport.send({ method: 'GET', path: identityPath(tenantId, 'applications') });
  }

  /** `201` with the registration and, for a confidential client, its secret — once. */
  createApplication(tenantId: string, draft: ApplicationDraft): Promise<ApiResponse<Application>> {
    return this.transport.send({ method: 'POST', path: identityPath(tenantId, 'applications'), body: draft });
  }

  /** A new secret, returned once; the old one stops working at once. */
  rotateApplicationSecret(tenantId: string, applicationId: string): Promise<ApiResponse<Application>> {
    return this.transport.send({
      method: 'POST',
      path: identityPath(tenantId, 'applications', applicationId, 'rotateSecret')
    });
  }

  /** `204`; the client id is free again. */
  deleteApplication(tenantId: string, applicationId: string): Promise<ApiResponse<void>> {
    return this.transport.send({ method: 'DELETE', path: identityPath(tenantId, 'applications', applicationId) });
  }

  /** The caller's own live sessions — the address names no user, so it can't name anybody else. */
  listSessions(tenantId: string): Promise<ApiResponse<IdentityList<Session>>> {
    return this.transport.send({ method: 'GET', path: identityPath(tenantId, 'sessions') });
  }

  /** `204`; somebody else's session id is a `404`, never a different answer. */
  revokeSession(tenantId: string, sessionId: string): Promise<ApiResponse<void>> {
    return this.transport.send({ method: 'DELETE', path: identityPath(tenantId, 'sessions', sessionId) });
  }
}
