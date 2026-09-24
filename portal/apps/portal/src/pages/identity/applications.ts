import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { BladeStackStore } from '@cybercloud/shell';
import { XuiButton } from '@xui/button';
import { XuiCallout } from '@xui/callout';
import { XuiInput } from '@xui/input';
import { XuiNonIdealState } from '@xui/non-ideal-state';
import { XuiTable, XuiTd, XuiTh, XuiTr } from '@xui/table';
import { XuiTag } from '@xui/tag';
import { Application, IdentityAdminApi, registrableScopes } from '../../app/api/identity-admin';
import { links } from '../../app/routes/portal-links';
import { NeedsTenant, PageStatus, activeTenantId, load, pageState } from '../shared/page-state';
import { IdentityNav, messageOf, when } from './identity-shared';

type Scope = (typeof registrableScopes)[number];

/** A secret the platform just issued, shown until the person dismisses it — and never again. */
interface IssuedSecret {
  readonly application: string;
  readonly clientId: string;
  readonly secret: string;
}

/**
 * The applications page: the OAuth clients this organisation has registered, a form to register
 * one, and a confidential client's secret — shown once. Issue #41, over #94's consent page, which
 * is what made a tenant-registered client usable at all.
 *
 * ⚠ **The secret is on screen once, and the page says so before it is.** The platform keeps its
 * SHA-256 and no call returns it again (`IdentityBodies`), so the callout that shows it stays
 * until the person says they have it, and a reload loses it for good — the answer then is
 * "Rotate", which issues a new one and kills the old in the same breath. The value is kept in a
 * signal on this page only: not in a store, not in the URL, not in the blade stack's title.
 *
 * ⚠ **Public or confidential is a required choice, never a default.** A browser or native app
 * can't keep a secret, so it gets none and must use PKCE; a server gets one. Guessing either way
 * is somebody's threat model wrong — `IdentityBodies.ApplicationDraft` refuses a body without it.
 *
 * ⚠ **Owner only**, as the members page: the API answers the check, and `cc-page-status` renders it.
 */
@Component({
  selector: 'cc-identity-applications',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    IdentityNav,
    NeedsTenant,
    PageStatus,
    XuiButton,
    XuiCallout,
    XuiInput,
    XuiNonIdealState,
    XuiTable,
    XuiTr,
    XuiTh,
    XuiTd,
    XuiTag
  ],
  host: { class: 'block p-6' },
  template: `
    @if (tenantId() === null) {
      <cc-needs-tenant />
    } @else {
      <div class="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 class="text-lg font-semibold" i18n="@@identity.apps.heading">Applications</h1>
          <p class="text-foreground-muted mt-1 text-sm" i18n="@@identity.apps.subheading">
            OAuth clients your organisation's people can sign in to. Each asks for consent the first time.
          </p>
        </div>
        <button
          xuiButton
          color="primary"
          size="sm"
          type="button"
          (click)="creating.set(!creating())"
          i18n="@@identity.apps.new"
        >
          Register an application
        </button>
      </div>
      <cc-identity-nav />

      @if (issued(); as shown) {
        <xui-callout class="mt-4" color="warning" [title]="secretTitle" data-issued-secret>
          <p i18n="@@identity.apps.secretBody">
            The client secret for {{ shown.application }} (client id <code>{{ shown.clientId }}</code
            >). Copy it now: the platform keeps only a fingerprint of it, and this page can't show it again.
          </p>
          <p class="mt-2">
            <code class="bg-surface-sunken rounded px-1.5 py-0.5 text-xs break-all" data-secret>{{
              shown.secret
            }}</code>
          </p>
          <div class="mt-3 flex gap-2">
            <button
              xuiButton
              variant="outline"
              size="sm"
              type="button"
              (click)="copy(shown.secret)"
              i18n="@@identity.apps.copy"
            >
              Copy
            </button>
            <button
              xuiButton
              variant="ghost"
              size="sm"
              type="button"
              (click)="issued.set(null)"
              i18n="@@identity.apps.done"
            >
              I've stored it
            </button>
          </div>
        </xui-callout>
      }

      @if (creating()) {
        <section class="border-border mt-4 rounded-md border p-4" aria-labelledby="cc-app-create-heading">
          <h2 class="text-sm font-semibold" id="cc-app-create-heading" i18n="@@identity.apps.createHeading">
            Register an application
          </h2>
          <form class="mt-3 flex flex-col gap-4" (submit)="onCreate($event)">
            <div class="flex flex-col gap-1.5">
              <label class="text-sm font-medium" for="cc-app-name" i18n="@@identity.apps.name">Display name</label>
              <input
                xuiInput
                id="cc-app-name"
                autocomplete="off"
                maxlength="100"
                [value]="displayName()"
                (input)="onName($event)"
                [attr.aria-describedby]="'cc-app-name-hint'"
              />
              <p class="text-foreground-muted text-xs" id="cc-app-name-hint" i18n="@@identity.apps.nameHint">
                What the consent page shows the people signing in.
              </p>
            </div>

            <div class="flex flex-col gap-1.5">
              <label class="text-sm font-medium" for="cc-app-redirects" i18n="@@identity.apps.redirects"
                >Redirect URIs</label
              >
              <textarea
                xuiInput
                id="cc-app-redirects"
                rows="3"
                class="font-mono text-xs"
                [value]="redirects()"
                (input)="onRedirects($event)"
                [attr.aria-describedby]="'cc-app-redirects-hint'"
              ></textarea>
              <p class="text-foreground-muted text-xs" id="cc-app-redirects-hint" i18n="@@identity.apps.redirectsHint">
                One per line, absolute, with no fragment. Matched whole — never by prefix.
              </p>
            </div>

            <fieldset class="flex flex-col gap-2">
              <legend class="text-sm font-medium" i18n="@@identity.apps.kind">Kind of client</legend>
              <label class="border-border flex cursor-pointer items-start gap-2 rounded-md border px-3 py-2">
                <input
                  class="mt-1"
                  type="radio"
                  name="cc-app-kind"
                  [checked]="kind() === 'confidential'"
                  (change)="kind.set('confidential')"
                />
                <span>
                  <span class="block font-medium" i18n="@@identity.apps.confidential">Server</span>
                  <span class="text-foreground-muted block text-xs" i18n="@@identity.apps.confidentialHint"
                    >Keeps a secret the platform issues now. A stolen code is useless without it.</span
                  >
                </span>
              </label>
              <label class="border-border flex cursor-pointer items-start gap-2 rounded-md border px-3 py-2">
                <input
                  class="mt-1"
                  type="radio"
                  name="cc-app-kind"
                  [checked]="kind() === 'public'"
                  (change)="kind.set('public')"
                />
                <span>
                  <span class="block font-medium" i18n="@@identity.apps.public">Browser or native app</span>
                  <span class="text-foreground-muted block text-xs" i18n="@@identity.apps.publicHint"
                    >Can't keep a secret, so it gets none and signs in with PKCE.</span
                  >
                </span>
              </label>
            </fieldset>

            <fieldset class="flex flex-wrap gap-4">
              <legend class="mb-2 text-sm font-medium" i18n="@@identity.apps.scopes">Scopes it may ask for</legend>
              @for (scope of scopeChoices; track scope) {
                <label class="flex items-center gap-2 text-sm">
                  <input type="checkbox" [checked]="scopes().includes(scope)" (change)="toggle(scope)" />
                  <code class="text-xs">{{ scope }}</code>
                </label>
              }
            </fieldset>

            @if (formError(); as message) {
              <p class="text-error text-sm" role="alert">{{ message }}</p>
            }

            <div class="flex gap-2">
              <button
                xuiButton
                color="primary"
                size="sm"
                type="submit"
                [disabled]="busy() !== null"
                [loading]="busy() === 'create'"
                i18n="@@identity.apps.register"
              >
                Register
              </button>
              <button
                xuiButton
                variant="ghost"
                size="sm"
                type="button"
                (click)="creating.set(false)"
                i18n="@@identity.apps.cancel"
              >
                Cancel
              </button>
            </div>
          </form>
        </section>
      }

      @if (failure(); as message) {
        <xui-callout class="mt-4" color="error" [title]="refusedTitle"
          ><p>{{ message }}</p></xui-callout
        >
      }

      <section class="mt-6" aria-labelledby="cc-apps-heading">
        <h2 class="text-sm font-semibold" id="cc-apps-heading" i18n="@@identity.apps.listHeading">Registered</h2>
        <cc-page-status [state]="applications()" />
        @if (applications(); as state) {
          @if (state.kind === 'ready') {
            @if (state.value.length === 0) {
              <xui-non-ideal-state class="mt-6" [title]="emptyTitle" [description]="emptyDescription" />
            } @else {
              <xui-table class="mt-3" striped aria-labelledby="cc-apps-heading">
                <xui-tr>
                  <xui-th role="columnheader" class="w-48" i18n="@@identity.apps.col.name">Name</xui-th>
                  <xui-th role="columnheader" class="w-80" i18n="@@identity.apps.col.clientId">Client id</xui-th>
                  <xui-th role="columnheader" class="w-32" i18n="@@identity.apps.col.kind">Kind</xui-th>
                  <xui-th role="columnheader" class="flex-1" i18n="@@identity.apps.col.redirects">Redirect URIs</xui-th>
                  <xui-th role="columnheader" class="w-64"
                    ><span class="sr-only" i18n="@@identity.apps.col.actions">Actions</span></xui-th
                  >
                </xui-tr>
                @for (app of state.value; track app.name) {
                  <xui-tr [attr.data-application]="app.name">
                    <xui-td role="cell" class="w-48" truncate>{{ app.properties.displayName }}</xui-td>
                    <xui-td role="cell" class="w-80" truncate
                      ><code class="text-xs">{{ app.properties.clientId }}</code></xui-td
                    >
                    <xui-td role="cell" class="w-32">
                      <xui-tag minimal>{{ app.properties.publicClient ? publicLabel : confidentialLabel }}</xui-tag>
                    </xui-td>
                    <xui-td role="cell" class="flex-1" truncate>{{ app.properties.redirectUris.join(', ') }}</xui-td>
                    <xui-td role="cell" class="w-64">
                      @if (confirming() === app.name) {
                        <span class="flex flex-wrap items-center gap-2">
                          <button
                            xuiButton
                            color="error"
                            size="sm"
                            type="button"
                            [disabled]="busy() !== null"
                            (click)="confirmed(app)"
                          >
                            {{ pendingAction() === 'rotate' ? rotateConfirm : deleteConfirm }}
                          </button>
                          <button
                            xuiButton
                            variant="ghost"
                            size="sm"
                            type="button"
                            (click)="confirming.set(null)"
                            i18n="@@identity.apps.keep"
                          >
                            Cancel
                          </button>
                        </span>
                      } @else {
                        <span class="flex flex-wrap gap-2">
                          @if (!app.properties.publicClient) {
                            <button
                              xuiButton
                              variant="outline"
                              size="sm"
                              type="button"
                              [disabled]="busy() !== null"
                              [attr.aria-label]="rotateLabel(app)"
                              (click)="ask(app, 'rotate')"
                              i18n="@@identity.apps.rotate"
                            >
                              Rotate secret
                            </button>
                          }
                          <button
                            xuiButton
                            variant="outline"
                            color="error"
                            size="sm"
                            type="button"
                            [disabled]="busy() !== null"
                            [attr.aria-label]="deleteLabel(app)"
                            (click)="ask(app, 'delete')"
                            i18n="@@identity.apps.delete"
                          >
                            Delete
                          </button>
                        </span>
                      }
                    </xui-td>
                  </xui-tr>
                  @if (app.properties.clientSecretIssuedAt; as issuedAt) {
                    <xui-tr>
                      <xui-td
                        role="cell"
                        class="text-foreground-muted flex-1 text-xs"
                        i18n="@@identity.apps.secretIssued"
                      >
                        Secret issued {{ when(issuedAt) }}
                      </xui-td>
                    </xui-tr>
                  }
                }
              </xui-table>
            }
          }
        }
      </section>
    }
  `
})
export class IdentityApplications {
  private readonly api = inject(IdentityAdminApi);
  private readonly blades = inject(BladeStackStore);

  protected readonly tenantId = activeTenantId();
  protected readonly applications = pageState<readonly Application[]>();

  protected readonly creating = signal(false);
  protected readonly displayName = signal('');
  protected readonly redirects = signal('');
  protected readonly kind = signal<'public' | 'confidential' | null>(null);
  protected readonly scopes = signal<readonly Scope[]>(['openid', 'profile']);
  protected readonly formError = signal<string | null>(null);

  protected readonly busy = signal<'create' | 'row' | null>(null);
  protected readonly confirming = signal<string | null>(null);
  protected readonly pendingAction = signal<'rotate' | 'delete'>('delete');
  protected readonly failure = signal<string | null>(null);
  protected readonly issued = signal<IssuedSecret | null>(null);

  protected readonly scopeChoices = registrableScopes;
  protected readonly when = when;
  protected readonly publicLabel = $localize`:@@identity.apps.kind.public:Public`;
  protected readonly confidentialLabel = $localize`:@@identity.apps.kind.confidential:Confidential`;
  protected readonly rotateConfirm = $localize`:@@identity.apps.rotateConfirm:Issue a new secret`;
  protected readonly deleteConfirm = $localize`:@@identity.apps.deleteConfirm:Delete it`;
  protected readonly secretTitle = $localize`:@@identity.apps.secretTitle:Copy this secret now`;
  protected readonly refusedTitle = $localize`:@@identity.apps.refused:The platform refused the request`;
  protected readonly emptyTitle = $localize`:@@identity.apps.emptyTitle:No applications yet`;
  protected readonly emptyDescription = $localize`:@@identity.apps.emptyDescription:Register one to let your organisation's people sign in to it with their Cyber Cloud account.`;

  private readonly redirectList = computed(() =>
    this.redirects()
      .split(/\r?\n/)
      .map(x => x.trim())
      .filter(x => x.length > 0)
  );

  constructor() {
    effect(() => {
      const tenantId = this.tenantId();

      untracked(() => {
        const route = links.identityApplications();
        this.blades.open({ id: route, title: $localize`:@@identity.apps.blade:Applications`, route });
        // ⚠ A secret issued in one tenant is never left on screen under another.
        this.issued.set(null);
        this.failure.set(null);
        this.confirming.set(null);
        if (tenantId !== null) void this.refresh(tenantId);
      });
    });
  }

  protected rotateLabel(app: Application): string {
    return $localize`:@@identity.apps.rotateLabel:Rotate the secret of ${app.properties.displayName}:name:`;
  }

  protected deleteLabel(app: Application): string {
    return $localize`:@@identity.apps.deleteLabel:Delete ${app.properties.displayName}:name:`;
  }

  protected onName(event: Event): void {
    this.displayName.set((event.target as HTMLInputElement).value);
  }

  protected onRedirects(event: Event): void {
    this.redirects.set((event.target as HTMLTextAreaElement).value);
  }

  protected toggle(scope: Scope): void {
    this.scopes.update(scopes => (scopes.includes(scope) ? scopes.filter(x => x !== scope) : [...scopes, scope]));
  }

  protected ask(app: Application, action: 'rotate' | 'delete'): void {
    this.pendingAction.set(action);
    this.confirming.set(app.name);
  }

  protected async copy(secret: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(secret);
    } catch {
      // A browser that refuses the clipboard still shows the value, selectable, above.
    }
  }

  protected async onCreate(event: Event): Promise<void> {
    event.preventDefault();
    const tenantId = this.tenantId();
    const kind = this.kind();
    if (tenantId === null || this.busy() !== null) return;

    const message =
      this.displayName().trim().length === 0
        ? $localize`:@@identity.apps.nameRequired:Give the application a name.`
        : this.redirectList().length === 0
          ? $localize`:@@identity.apps.redirectRequired:Add at least one redirect URI.`
          : kind === null
            ? $localize`:@@identity.apps.kindRequired:Choose whether it is a server or a browser or native app.`
            : null;

    this.formError.set(message);
    if (message !== null || kind === null) return;

    this.busy.set('create');
    this.failure.set(null);

    try {
      const created = await this.api.createApplication(tenantId, {
        displayName: this.displayName().trim(),
        redirectUris: this.redirectList(),
        scopes: this.scopes(),
        publicClient: kind === 'public'
      });
      if (this.tenantId() !== tenantId) return;

      this.show(created.value);
      this.creating.set(false);
      this.displayName.set('');
      this.redirects.set('');
      this.kind.set(null);
    } catch (error) {
      if (this.tenantId() === tenantId) this.formError.set(messageOf(error));
    } finally {
      this.busy.set(null);
    }

    if (this.tenantId() === tenantId) await this.refresh(tenantId);
  }

  protected async confirmed(app: Application): Promise<void> {
    const tenantId = this.tenantId();
    if (tenantId === null || this.busy() !== null) return;

    this.busy.set('row');
    this.failure.set(null);

    try {
      if (this.pendingAction() === 'rotate') {
        const rotated = await this.api.rotateApplicationSecret(tenantId, app.name);
        if (this.tenantId() === tenantId) this.show(rotated.value);
      } else {
        await this.api.deleteApplication(tenantId, app.name);
        // A secret on screen for an application that no longer exists would be a credential for nothing.
        if (this.issued()?.clientId === app.properties.clientId) this.issued.set(null);
      }
    } catch (error) {
      if (this.tenantId() === tenantId) this.failure.set(messageOf(error));
    } finally {
      this.busy.set(null);
      this.confirming.set(null);
    }

    if (this.tenantId() === tenantId) await this.refresh(tenantId);
  }

  /** Puts a just-issued secret on screen; a public client's registration has none to show. */
  private show(app: Application): void {
    const secret = app.properties.clientSecret;
    this.issued.set(
      secret === undefined || secret.length === 0
        ? null
        : { application: app.properties.displayName, clientId: app.properties.clientId, secret }
    );
  }

  private async refresh(tenantId: string): Promise<void> {
    await load(
      this.applications,
      async () => (await this.api.listApplications(tenantId)).value.value,
      () => this.tenantId() !== tenantId
    );
  }
}
