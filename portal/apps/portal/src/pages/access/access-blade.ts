import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BladeStackStore } from '@cybercloud/shell';
import { XuiButton } from '@xui/button';
import { XuiCallout } from '@xui/callout';
import { XuiInput } from '@xui/input';
import { XuiNonIdealState } from '@xui/non-ideal-state';
import { XuiSelect } from '@xui/select';
import { XuiTable, XuiTd, XuiTh, XuiTr } from '@xui/table';
import { ApiCallError } from '../../app/api/http-transport';
import { ResourceAddress } from '../../app/api/resource-verbs';
import {
  AccessScope,
  PRINCIPAL_ID,
  PRINCIPAL_ID_MAX_LENGTH,
  PrincipalType,
  Role,
  RoleAssignment,
  RoleAssignmentName,
  RoleAssignmentsApi,
  parseName,
  principalTypes,
  renderName,
  roles
} from '../../app/api/role-assignments';
import { joinType, links } from '../../app/routes/portal-links';
import { NeedsTenant, activeTenantId } from '../shared/page-state';

/** What the last call did, shown once above the form and replaced by the next call. */
type Outcome =
  | { readonly kind: 'granted' | 'repeated' | 'assigned' | 'absent' | 'revoked'; readonly name: string }
  | { readonly kind: 'failed'; readonly name: string; readonly message: string };

/** One assignment the page knows about, with its name parsed for the table. */
interface KnownAssignment {
  readonly name: RoleAssignmentName;
  readonly rendered: string;
}

/**
 * The access page: role assignments at one scope — the "Access (ReBAC)" rail item of docs/plan/20
 * § Information architecture, on a subscription, a resource group or a resource.
 *
 * Three things the API can do, and this page does: grant a role to a principal (`PUT`), check
 * whether one is granted (`GET`), and revoke one (`DELETE`) — issue #70's write path, over
 * `RoleAssignmentsApi`. The name is never typed: it is derived from the three choices and shown
 * before anything is sent, because `{role}-{principalType}-{principalId}` *is* the assignment
 * (docs/plan/07 § Azure RBAC, expressed in it) and a user should see the address they are about
 * to grant.
 *
 * ⚠ **There is no list, and the empty state says so.** `GET …/roleAssignments` with no name
 * answers 400 today — issue #86 records the collection as owed — and what is assigned at a scope
 * lives only on `ICheckGrain`'s role-assignment view, which reaches no HTTP address. So the
 * table below holds what this page has granted or checked in this visit, labelled as exactly
 * that, and nothing pretends to be a listing. The day the collection lands, it loads into the
 * same table and the label goes.
 *
 * ⚠ **The principal is not validated against a directory.** #86's third point: the platform
 * accepts any well-formed id of a closed principal type and writes the tuple. The page checks the
 * id's shape (`RelationNaming.IdPattern`) and no more, and its hint says the id is the
 * directory's — a typo grants something nobody can use, and nothing here would notice.
 *
 * ⚠ **Revoke is two clicks, and a grant is one.** A grant is reversible by the button beside it;
 * revoking `owner` from yourself is a lockout that only another owner can undo, so the row asks
 * once. Neither hands off to the operation view: an assignment is one tuple write and there is no
 * `202` — `DispatchStage.RoleAssignmentAsync`.
 */
@Component({
  selector: 'cc-access-blade',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    XuiButton,
    XuiCallout,
    XuiInput,
    XuiNonIdealState,
    XuiSelect,
    XuiTable,
    XuiTr,
    XuiTh,
    XuiTd,
    NeedsTenant
  ],
  host: { class: 'block p-6' },
  template: `
    @if (scope() === null) {
      <cc-needs-tenant />
    } @else {
      <div class="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 class="text-lg font-semibold" i18n="@@access.heading">Access control</h1>
          <p class="text-foreground-muted mt-1 text-sm">
            {{ scopeKindLabel() }}
            <a class="underline" [routerLink]="scopeLink()">{{ scopeName() }}</a>
          </p>
        </div>
      </div>

      <section class="border-border mt-4 rounded-md border p-4" aria-labelledby="cc-access-assign-heading">
        <h2 class="text-sm font-semibold" id="cc-access-assign-heading" i18n="@@access.assignHeading">Assign a role</h2>

        <form class="mt-3 flex flex-col gap-4" (submit)="onAssign($event)">
          <fieldset class="flex flex-col gap-2">
            <legend class="text-sm font-medium" i18n="@@access.role">Role</legend>
            @for (choice of roleChoices; track choice.value) {
              <label class="border-border flex cursor-pointer items-start gap-2 rounded-md border px-3 py-2">
                <input
                  class="mt-1"
                  type="radio"
                  name="cc-access-role"
                  [value]="choice.value"
                  [checked]="role() === choice.value"
                  (change)="role.set(choice.value)"
                />
                <span class="min-w-0">
                  <span class="block font-medium">{{ choice.label }}</span>
                  <span class="text-foreground-muted block text-xs">{{ choice.description }}</span>
                </span>
              </label>
            }
          </fieldset>

          <div class="flex flex-wrap items-start gap-4">
            <div class="flex flex-col gap-1.5">
              <span class="text-sm font-medium" id="cc-access-principal-type-label" i18n="@@access.principalType"
                >Principal type</span
              >
              <xui-select
                class="min-w-48"
                [items]="principalTypeChoices"
                [itemText]="principalTypeText"
                [filterable]="false"
                [value]="principalType()"
                aria-labelledby="cc-access-principal-type-label"
                (valueChange)="onPrincipalType($event)"
              />
            </div>

            <div class="flex min-w-72 flex-1 flex-col gap-1.5">
              <label class="text-sm font-medium" for="cc-access-principal-id">
                <ng-container i18n="@@access.principalId">Principal id</ng-container>
                <span class="text-error" aria-hidden="true">*</span>
              </label>
              <input
                xuiInput
                id="cc-access-principal-id"
                autocomplete="off"
                [value]="principalId()"
                (input)="onPrincipalId($event)"
                (blur)="touched.set(true)"
                [attr.aria-describedby]="
                  idMessage() === null ? 'cc-access-principal-id-hint' : 'cc-access-principal-id-error'
                "
                [attr.aria-invalid]="idMessage() === null ? null : 'true'"
              />
              @if (idMessage(); as message) {
                <p class="text-error text-xs" id="cc-access-principal-id-error" role="alert">{{ message }}</p>
              } @else {
                <p
                  class="text-foreground-muted text-xs"
                  id="cc-access-principal-id-hint"
                  i18n="@@access.principalIdHint"
                >
                  The directory's id for the principal — the 32-digit lowercase form of a GUID, or a name of 1–63
                  lowercase letters, digits and hyphens. It is not checked against the directory yet.
                </p>
              }
            </div>
          </div>

          <p class="text-sm">
            <ng-container i18n="@@access.derivedName">Assignment name</ng-container>
            <code
              class="bg-surface-sunken ml-1 rounded px-1.5 py-0.5 text-xs"
              [attr.data-assignment-name]="derivedName() ?? ''"
              >{{ derivedName() ?? '—' }}</code
            >
            <span class="text-foreground-muted block text-xs" i18n="@@access.derivedNameHint">
              Derived from the three choices — the name is the assignment, so granting it twice is one grant.
            </span>
          </p>

          <div class="flex flex-wrap gap-2">
            <button
              xuiButton
              color="primary"
              size="sm"
              type="submit"
              [disabled]="busy() !== null"
              [loading]="busy() === 'assign'"
              i18n="@@access.assign"
            >
              Assign role
            </button>
            <button
              xuiButton
              variant="outline"
              size="sm"
              type="button"
              [disabled]="busy() !== null"
              [loading]="busy() === 'check'"
              (click)="onCheck()"
              i18n="@@access.check"
            >
              Check
            </button>
          </div>
        </form>

        @if (outcome(); as outcome) {
          <xui-callout
            class="mt-3"
            [color]="outcomeColor()"
            [title]="outcomeTitle()"
            [attr.data-outcome]="outcome.kind"
          >
            @switch (outcome.kind) {
              @case ('granted') {
                <p i18n="@@access.outcome.granted">
                  <code>{{ outcome.name }}</code> is granted at this scope.
                </p>
              }
              @case ('repeated') {
                <p i18n="@@access.outcome.repeated">
                  <code>{{ outcome.name }}</code> was already granted here. Nothing changed — a repeated grant is the
                  same assignment.
                </p>
              }
              @case ('assigned') {
                <p i18n="@@access.outcome.assigned">
                  <code>{{ outcome.name }}</code> is assigned at this scope.
                </p>
              }
              @case ('absent') {
                <p i18n="@@access.outcome.absent">
                  <code>{{ outcome.name }}</code> is not assigned at this scope, or is not yours to see: the platform
                  answers the two the same way.
                </p>
              }
              @case ('revoked') {
                <p i18n="@@access.outcome.revoked">
                  <code>{{ outcome.name }}</code> is revoked at this scope.
                </p>
              }
              @case ('failed') {
                <p>{{ outcome.message }}</p>
              }
            }
          </xui-callout>
        }
      </section>

      <section class="mt-6" aria-labelledby="cc-access-known-heading">
        <h2 class="text-sm font-semibold" id="cc-access-known-heading" i18n="@@access.knownHeading">Assignments</h2>

        @if (known().length === 0) {
          <xui-non-ideal-state class="mt-6" [title]="emptyTitle" [description]="emptyDescription" />
        } @else {
          <p class="text-foreground-muted mt-1 text-xs" i18n="@@access.knownNote">
            Only what this page has granted or checked. The platform cannot enumerate a scope's assignments yet (issue
            #86), so an assignment made elsewhere is not shown here until it is checked.
          </p>
          <xui-table class="mt-3" striped aria-labelledby="cc-access-known-heading">
            <xui-tr>
              <xui-th class="w-32" i18n="@@access.col.role">Role</xui-th>
              <xui-th class="w-40" i18n="@@access.col.principalType">Principal type</xui-th>
              <xui-th class="flex-1" i18n="@@access.col.principalId">Principal id</xui-th>
              <xui-th class="w-56" i18n="@@access.col.actions">
                <span class="sr-only">Actions</span>
              </xui-th>
            </xui-tr>
            @for (item of known(); track item.rendered) {
              <xui-tr [attr.data-assignment]="item.rendered">
                <xui-td class="w-32">{{ roleLabel(item.name.role) }}</xui-td>
                <xui-td class="w-40">{{ principalTypeText(item.name.principalType) }}</xui-td>
                <xui-td class="flex-1" truncate
                  ><code class="text-xs">{{ item.name.principalId }}</code></xui-td
                >
                <xui-td class="w-56">
                  @if (removing() === item.rendered) {
                    <span class="flex flex-wrap items-center gap-2">
                      <span class="text-xs" i18n="@@access.removeAsk">Revoke this role?</span>
                      <button
                        xuiButton
                        color="error"
                        size="sm"
                        type="button"
                        [disabled]="busy() !== null"
                        [loading]="busy() === 'revoke'"
                        (click)="confirmRemove(item)"
                        i18n="@@access.removeConfirm"
                      >
                        Revoke
                      </button>
                      <button
                        xuiButton
                        variant="ghost"
                        size="sm"
                        type="button"
                        (click)="removing.set(null)"
                        i18n="@@access.removeCancel"
                      >
                        Keep it
                      </button>
                    </span>
                  } @else {
                    <button
                      xuiButton
                      variant="outline"
                      color="error"
                      size="sm"
                      type="button"
                      [disabled]="busy() !== null"
                      [attr.aria-label]="removeLabel(item)"
                      (click)="removing.set(item.rendered)"
                      i18n="@@access.remove"
                    >
                      Remove
                    </button>
                  }
                </xui-td>
              </xui-tr>
            }
          </xui-table>
        }
      </section>
    }
  `
})
export class AccessBlade {
  readonly subscriptionId = input.required<string>();
  /** Present at resource-group scope and below. */
  readonly resourceGroup = input<string>();
  /** Present at resource scope: the type path and the name, with the parent pair for a child. */
  readonly provider = input<string>();
  readonly type = input<string>();
  readonly name = input<string>();
  readonly parent = input<string>();
  readonly childType = input<string>();

  private readonly api = inject(RoleAssignmentsApi);
  private readonly blades = inject(BladeStackStore);

  protected readonly tenantId = activeTenantId();

  /**
   * The scope the route names, before a tenant is known. `tenantId` is empty here and filled in
   * by `scope`; the links and the blade title need only the route's own segments, so a fresh
   * portal with nobody signed in still opens the right blade and points back at the right place.
   */
  private readonly target = computed<AccessScope>(() => this.scopeWith(''));

  /** The scope this page acts on — `target` with the token's tenant, or `null` with no tenant. */
  protected readonly scope = computed<AccessScope | null>(() => {
    const tenantId = this.tenantId();
    return tenantId === null ? null : this.scopeWith(tenantId);
  });

  protected readonly scopeLink = computed(() => {
    const target = this.target();
    switch (target.kind) {
      case 'resource':
        return links.resource(target.address, target.resourceType);
      case 'resourceGroup':
        return links.resourceGroup(target.subscriptionId, target.resourceGroup);
      default:
        return links.subscription(this.subscriptionId());
    }
  });

  private readonly accessRoute = computed(() => {
    const target = this.target();
    switch (target.kind) {
      case 'resource':
        return links.resourceAccess(target.address, target.resourceType);
      case 'resourceGroup':
        return links.resourceGroupAccess(target.subscriptionId, target.resourceGroup);
      default:
        return links.subscriptionAccess(this.subscriptionId());
    }
  });

  protected readonly scopeName = computed(() => {
    const target = this.target();
    switch (target.kind) {
      case 'resource':
        return target.address.name;
      case 'resourceGroup':
        return target.resourceGroup;
      default:
        return this.subscriptionId();
    }
  });

  protected readonly scopeKindLabel = computed(() => {
    switch (this.target().kind) {
      case 'resource':
        return $localize`:@@access.scope.resource:Resource`;
      case 'resourceGroup':
        return $localize`:@@access.scope.resourceGroup:Resource group`;
      default:
        return $localize`:@@access.scope.subscription:Subscription`;
    }
  });

  protected readonly role = signal<Role>('reader');
  protected readonly principalType = signal<PrincipalType>('user');
  protected readonly principalId = signal('');
  protected readonly touched = signal(false);

  /** The name that would be sent, or `null` while the id is not one the platform would accept. */
  protected readonly derivedName = computed(() => {
    const name = this.assignmentName();
    return name === null ? null : renderName(name);
  });

  private readonly assignmentName = computed<RoleAssignmentName | null>(() => {
    const principalId = this.principalId().trim();
    if (!PRINCIPAL_ID.test(principalId) || principalId.length > PRINCIPAL_ID_MAX_LENGTH) return null;
    return { role: this.role(), principalType: this.principalType(), principalId };
  });

  protected readonly idMessage = computed(() => {
    if (!this.touched()) return null;
    const principalId = this.principalId().trim();
    if (principalId.length === 0) return $localize`:@@access.principalIdRequired:A principal id is required.`;
    if (this.assignmentName() === null)
      return $localize`:@@access.principalIdInvalid:Use 1–63 lowercase letters, digits and hyphens, starting and ending with a letter or digit.`;
    return null;
  });

  protected readonly busy = signal<'assign' | 'check' | 'revoke' | null>(null);
  protected readonly outcome = signal<Outcome | null>(null);
  protected readonly known = signal<readonly KnownAssignment[]>([]);
  protected readonly removing = signal<string | null>(null);

  protected readonly roleChoices: readonly { value: Role; label: string; description: string }[] = roles.map(value => ({
    value,
    label: this.roleLabel(value),
    description: roleDescription(value)
  }));
  protected readonly principalTypeChoices = principalTypes;
  protected readonly principalTypeText = (value: PrincipalType): string => principalTypeLabel(value);

  protected readonly emptyTitle = $localize`:@@access.emptyTitle:No list endpoint yet`;
  protected readonly emptyDescription = $localize`:@@access.emptyDescription:The API can grant, read and revoke one assignment by name, and cannot yet enumerate what is assigned at a scope — that is issue #86. Assign a role above, or check one to see whether it is granted; either appears here.`;

  protected readonly outcomeColor = computed(() => {
    switch (this.outcome()?.kind) {
      case 'granted':
      case 'assigned':
      case 'revoked':
        return 'success' as const;
      case 'failed':
        return 'error' as const;
      default:
        return 'info' as const;
    }
  });

  protected readonly outcomeTitle = computed(() => {
    switch (this.outcome()?.kind) {
      case 'granted':
        return $localize`:@@access.outcomeTitle.granted:Granted`;
      case 'repeated':
        return $localize`:@@access.outcomeTitle.repeated:Already granted`;
      case 'assigned':
        return $localize`:@@access.outcomeTitle.assigned:Assigned`;
      case 'absent':
        return $localize`:@@access.outcomeTitle.absent:Not assigned`;
      case 'revoked':
        return $localize`:@@access.outcomeTitle.revoked:Revoked`;
      case 'failed':
        return $localize`:@@access.outcomeTitle.failed:The platform refused the request`;
      default:
        return '';
    }
  });

  constructor() {
    effect(() => {
      const route = this.accessRoute();
      // ⚠ Read for the change, not the value: the tenant is the part of the scope the route does
      // not spell. A switch from the context bar leaves `accessRoute` untouched, and without this
      // read the rows would stay — and the next Revoke would send the old tenant's name down the
      // new tenant's path.
      this.tenantId();

      untracked(() => {
        this.blades.open({ id: route, title: $localize`:@@access.blade:Access`, route });

        // ⚠ What this page knows is per scope, and the tenant is part of the scope. A resource
        // group's grants painted over its subscription's page would be a listing of the wrong
        // scope, which is worse than none; one tenant's grants shown under another's is the same
        // wrong listing with a worse Revoke.
        this.known.set([]);
        this.outcome.set(null);
        this.removing.set(null);
      });
    });
  }

  /**
   * Whether the scope moved while a call was in flight. `scope` is a computed over the tenant and
   * the route's inputs, so it is the same object until one of them changes — and an answer for
   * a scope this page no longer shows must not be painted onto the one it does.
   */
  private moved(scope: AccessScope): boolean {
    return this.scope() !== scope;
  }

  /** Which of the three scopes the bound inputs spell, with the tenant the caller supplies. */
  private scopeWith(tenantId: string): AccessScope {
    const subscriptionId = this.subscriptionId();
    const resourceGroup = this.resourceGroup();
    const provider = this.provider();
    const type = this.type();
    const name = this.name();

    if (resourceGroup !== undefined && provider !== undefined && type !== undefined && name !== undefined) {
      const childType = this.childType();
      const address: ResourceAddress = {
        tenantId,
        subscriptionId,
        resourceGroup,
        parents: this.parent() === undefined ? [] : [this.parent() as string],
        name
      };

      return {
        kind: 'resource',
        address,
        resourceType: joinType(provider, childType === undefined ? [type] : [type, childType])
      };
    }

    if (resourceGroup !== undefined) return { kind: 'resourceGroup', tenantId, subscriptionId, resourceGroup };

    return { kind: 'subscription', tenantId, subscriptionId };
  }

  protected roleLabel(role: Role): string {
    switch (role) {
      case 'owner':
        return $localize`:@@access.role.owner:Owner`;
      case 'contributor':
        return $localize`:@@access.role.contributor:Contributor`;
      case 'reader':
        return $localize`:@@access.role.reader:Reader`;
      case 'keyVaultSecretsOfficer':
        return $localize`:@@access.role.keyVaultSecretsOfficer:Key Vault Secrets Officer`;
      case 'keyVaultSecretsUser':
        return $localize`:@@access.role.keyVaultSecretsUser:Key Vault Secrets User`;
      case 'keyVaultCryptoOfficer':
        return $localize`:@@access.role.keyVaultCryptoOfficer:Key Vault Crypto Officer`;
      case 'keyVaultCryptoUser':
        return $localize`:@@access.role.keyVaultCryptoUser:Key Vault Crypto User`;
    }
  }

  protected removeLabel(item: KnownAssignment): string {
    return $localize`:@@access.removeLabel:Remove ${item.rendered}:name:`;
  }

  protected onPrincipalType(value: PrincipalType | null): void {
    if (value !== null) this.principalType.set(value);
  }

  protected onPrincipalId(event: Event): void {
    this.principalId.set((event.target as HTMLInputElement).value);
  }

  protected async onAssign(event: Event): Promise<void> {
    event.preventDefault();
    const scope = this.scope();
    const name = this.assignmentName();
    this.touched.set(true);
    if (scope === null || name === null || this.busy() !== null) return;

    this.busy.set('assign');
    this.outcome.set(null);

    try {
      const response = await this.api.assign(scope, name);
      if (this.moved(scope)) return;
      this.remember(response.value);
      this.outcome.set({ kind: response.status === 201 ? 'granted' : 'repeated', name: renderName(name) });
    } catch (error) {
      if (!this.moved(scope)) this.outcome.set(failed(renderName(name), error));
    } finally {
      this.busy.set(null);
    }
  }

  protected async onCheck(): Promise<void> {
    const scope = this.scope();
    const name = this.assignmentName();
    this.touched.set(true);
    if (scope === null || name === null || this.busy() !== null) return;

    this.busy.set('check');
    this.outcome.set(null);

    try {
      const response = await this.api.read(scope, name);
      if (this.moved(scope)) return;
      this.remember(response.value);
      this.outcome.set({ kind: 'assigned', name: renderName(name) });
    } catch (error) {
      if (this.moved(scope)) return;
      if (error instanceof ApiCallError && error.status === 404) {
        this.forget(renderName(name));
        this.outcome.set({ kind: 'absent', name: renderName(name) });
      } else {
        this.outcome.set(failed(renderName(name), error));
      }
    } finally {
      this.busy.set(null);
    }
  }

  protected async confirmRemove(item: KnownAssignment): Promise<void> {
    const scope = this.scope();
    if (scope === null || this.busy() !== null) return;

    this.busy.set('revoke');
    this.outcome.set(null);

    try {
      await this.api.revoke(scope, item.name);
      if (this.moved(scope)) return;
      this.forget(item.rendered);
      this.outcome.set({ kind: 'revoked', name: item.rendered });
    } catch (error) {
      if (!this.moved(scope)) this.outcome.set(failed(item.rendered, error));
    } finally {
      this.removing.set(null);
      this.busy.set(null);
    }
  }

  /**
   * Adds what the platform served, keyed by name, so a grant and a later check of the same
   * assignment are one row. ⚠ The served name is what is kept, not the one sent: the two agree
   * today by construction, and if they ever did not, the platform's is the one that exists.
   */
  private remember(served: RoleAssignment): void {
    const name = parseName(served.name);
    if (name === null) return;

    const row: KnownAssignment = { name, rendered: served.name };
    this.known.update(known => [...known.filter(k => k.rendered !== row.rendered), row]);
  }

  private forget(rendered: string): void {
    this.known.update(known => known.filter(k => k.rendered !== rendered));
  }
}

/** One line per role, from docs/plan/07's permission definitions — what each one lets a holder do. */
function roleDescription(role: Role): string {
  switch (role) {
    case 'owner':
      return $localize`:@@access.role.ownerHint:Read, write, delete, and assign roles. The only role that can delete or purge.`;
    case 'contributor':
      return $localize`:@@access.role.contributorHint:Read and write, and deliberately not delete — narrower than Azure's Contributor.`;
    case 'reader':
      return $localize`:@@access.role.readerHint:Read only.`;
    case 'keyVaultSecretsOfficer':
      return $localize`:@@access.role.keyVaultSecretsOfficerHint:Set, read, delete, recover and purge a key vault's secrets. No control-plane right.`;
    case 'keyVaultSecretsUser':
      return $localize`:@@access.role.keyVaultSecretsUserHint:Read a key vault's secret values. No control-plane right.`;
    case 'keyVaultCryptoOfficer':
      return $localize`:@@access.role.keyVaultCryptoOfficerHint:Create, import, delete, recover, purge and use a key vault's keys. No control-plane right.`;
    case 'keyVaultCryptoUser':
      return $localize`:@@access.role.keyVaultCryptoUserHint:Encrypt, decrypt, wrap, unwrap, sign and verify with a key vault's keys. No control-plane right.`;
  }
}

function principalTypeLabel(value: PrincipalType): string {
  switch (value) {
    case 'user':
      return $localize`:@@access.principalType.user:User`;
    case 'servicePrincipal':
      return $localize`:@@access.principalType.servicePrincipal:Service principal`;
    case 'managedIdentity':
      return $localize`:@@access.principalType.managedIdentity:Managed identity`;
    case 'group':
      return $localize`:@@access.principalType.group:Group`;
  }
}

function failed(name: string, error: unknown): Outcome {
  return {
    kind: 'failed',
    name,
    message:
      error instanceof ApiCallError ? error.error.message : error instanceof Error ? error.message : String(error)
  };
}
