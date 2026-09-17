import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild
} from '@angular/core';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { TerminalConsolesData, TerminalConsolesResource, apiVersion } from '@cybercloud/api';
import { ResourceForm, ResourceFormRenderer, ResourceFormSource } from '@cybercloud/resource-forms';
import { BladeStackStore } from '@cybercloud/shell';
import { XuiButton } from '@xui/button';
import { XuiCallout } from '@xui/callout';
import { XuiInput } from '@xui/input';
import { ApiCallError, operationIdOf } from '../../app/api/http-transport';
import { PlatformApi } from '../../app/api/platform-api';
import { ResourceAddress } from '../../app/api/resource-verbs';
import { links } from '../../app/routes/portal-links';
import { TerminalPane } from '../../app/terminal/terminal-pane';
import { ConsoleAddress, TerminalSession } from '../../app/terminal/terminal-session';
import { NeedsTenant, PageStatus, activeTenantId, load, pageState } from '../shared/page-state';

/** The type this page is about. The one page in the portal that names a resource type. */
export const CONSOLE_TYPE = 'CyberCloud.Terminal/consoles';

/** `ResourceNaming`'s rule, as `resource-create.ts` states it. */
const RESOURCE_NAME = /^[a-z0-9]([-a-z0-9]*[a-z0-9])?$/;

/** What a terminate answered, shown once above the pane. */
type Outcome = { readonly kind: 'terminated' | 'wasIdle' } | { readonly kind: 'failed'; readonly message: string };

/**
 * The cloud shell: docs/plan/20 § The pages that are not generated, "`xterm.js` in a dockable
 * panel", over docs/plan/19's `CyberCloud.Terminal/consoles`.
 *
 * A shell needs a console resource to run in — the home volume, the identity and the network
 * policy that `connect` refuses to start a pod without — so the page starts from the group's
 * consoles: it lists them (`listCloudTerminal`), opens the one the URL names or the first, and,
 * when the group has none, renders the generated create form for the type right here rather
 * than sending the person to a blade to come back from. The create is the ordinary `PUT` → `202`
 * → operation view, and the operation view's `then` is this page, with the new console named.
 *
 * What the pane does is `TerminalSession`'s: `connect` through the generated client, a hub ticket
 * from the gateway, the socket, `Attach`, bytes both ways, reconnect. What this page adds is the
 * choosing, the creating, the two-click `terminate`, and being loud about recording — docs/plan/19
 * § Auditing says the portal must be, and `connect` carries the flag so the pane can be before the
 * first byte.
 *
 * ⚠ **The pane will say the data plane is owed, and that is the truth today.** Every method on
 * the gateway's `TerminalHub` throws by name until docs/plan/19's session grain exists;
 * `charts/managed/cloud-shell/conformance.yaml § owed` carries the item. The pane shows the hub's
 * own message, in the pane, so the state of the row is visible where a person would look for a
 * prompt. Everything before that point — the console, the ticket, the socket — is real.
 *
 * ⚠ **`terminate` is two clicks and `connect` is none.** A connect against a running shell joins
 * it (the handler applies rather than creates), so opening the page costs nothing that was not
 * already running. A terminate stops a process somebody may be mid-command in, so the row asks
 * once — the same rule the access page applies to revoking a role.
 */
@Component({
  selector: 'cc-terminal-blade',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    RouterLink,
    ResourceFormRenderer,
    XuiButton,
    XuiCallout,
    XuiInput,
    TerminalPane,
    NeedsTenant,
    PageStatus
  ],
  providers: [TerminalSession],
  host: { class: 'block p-6' },
  template: `
    @if (tenantId() === null) {
      <cc-needs-tenant />
    } @else {
      <div class="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 class="text-lg font-semibold" i18n="@@terminal.heading">Cloud shell</h1>
          <p class="text-foreground-muted mt-1 text-sm">
            <ng-container i18n="@@terminal.subheading">Consoles in</ng-container>
            <a class="underline" [routerLink]="groupLink()">{{ resourceGroup() }}</a>
          </p>
        </div>
        @if (consoles().kind === 'ready') {
          <a xuiButton variant="outline" size="sm" [routerLink]="createLink()" i18n="@@terminal.createAnother"
            >Create a console</a
          >
        }
      </div>

      <cc-page-status [state]="consoles()" [retryLink]="groupLink()" />

      @if (list(); as list) {
        @if (list.length === 0) {
          <section class="mt-4" aria-labelledby="cc-terminal-create-heading">
            <xui-callout color="info" [title]="noneTitle">
              <p i18n="@@terminal.none.body">
                A shell runs in a console: a home directory, a managed identity and a network policy in this group's
                cluster. This group has none yet — create one and the shell opens here.
              </p>
            </xui-callout>

            <h2 class="mt-4 text-sm font-semibold" id="cc-terminal-create-heading" i18n="@@terminal.create.heading">
              Create a console
            </h2>

            <cc-page-status [state]="form()" [retryLink]="groupLink()" />

            @if (schema(); as schema) {
              @if (sendError(); as message) {
                <xui-callout class="mt-3" color="error" [title]="refusedTitle">
                  <p>{{ message }}</p>
                </xui-callout>
              }

              <div class="mt-3 flex flex-col gap-1.5">
                <label class="text-sm font-medium" for="cc-terminal-name">
                  <ng-container i18n="@@terminal.create.name">Name</ng-container>
                  <span class="text-error" aria-hidden="true">*</span>
                </label>
                <input
                  xuiInput
                  id="cc-terminal-name"
                  class="max-w-72"
                  [formControl]="name"
                  autocomplete="off"
                  [attr.aria-describedby]="nameMessage() === null ? 'cc-terminal-name-hint' : 'cc-terminal-name-error'"
                  [attr.aria-invalid]="nameMessage() === null ? null : 'true'"
                />
                @if (nameMessage(); as message) {
                  <p class="text-error text-xs" id="cc-terminal-name-error" role="alert">{{ message }}</p>
                } @else {
                  <p class="text-foreground-muted text-xs" id="cc-terminal-name-hint" i18n="@@terminal.create.nameHint">
                    1–63 lowercase letters, digits and hyphens. It cannot change later.
                  </p>
                }
              </div>

              <cc-resource-form
                class="mt-2"
                [form]="schema"
                mode="create"
                [busy]="sending()"
                [submitLabel]="submitLabel"
                (submitted)="onCreate($event)"
              />
            }
          </section>
        } @else {
          <section class="mt-4" aria-labelledby="cc-terminal-consoles-heading">
            <h2 class="sr-only" id="cc-terminal-consoles-heading" i18n="@@terminal.consoles.heading">Consoles</h2>
            <ul class="flex flex-wrap gap-2" data-consoles>
              @for (console of list; track console.id) {
                <li>
                  <button
                    xuiButton
                    size="sm"
                    type="button"
                    variant="outline"
                    [active]="console.name === selected()?.name"
                    [attr.data-console]="console.name"
                    (click)="choose(console.name)"
                  >
                    {{ console.name }}
                  </button>
                </li>
              }
            </ul>
          </section>

          @if (selected(); as console) {
            <section class="mt-4" [attr.aria-label]="paneLabel(console)">
              <div class="flex flex-wrap items-center justify-between gap-2">
                <p class="text-sm" role="status" aria-live="polite" [attr.data-session-state]="session.state().kind">
                  <span class="font-medium">{{ stateLabel() }}</span>
                  @if (session.session(); as live) {
                    <span class="text-foreground-muted ml-2 text-xs">{{ limitsLabel(live) }}</span>
                  }
                </p>
                <div class="flex flex-wrap gap-2">
                  @if (canReconnect()) {
                    <button
                      xuiButton
                      color="primary"
                      size="sm"
                      type="button"
                      (click)="session.reconnect()"
                      i18n="@@terminal.reconnect"
                    >
                      Reconnect
                    </button>
                  }
                  @if (canDisconnect()) {
                    <button
                      xuiButton
                      variant="outline"
                      size="sm"
                      type="button"
                      (click)="session.close()"
                      i18n="@@terminal.disconnect"
                    >
                      Disconnect
                    </button>
                  }
                  @if (terminating()) {
                    <span class="flex flex-wrap items-center gap-2">
                      <span class="text-xs" i18n="@@terminal.terminateAsk"
                        >Stop the shell? Its home directory stays.</span
                      >
                      <button
                        xuiButton
                        color="error"
                        size="sm"
                        type="button"
                        [disabled]="busy()"
                        [loading]="busy()"
                        (click)="confirmTerminate(console)"
                        i18n="@@terminal.terminateConfirm"
                      >
                        Stop it
                      </button>
                      <button
                        xuiButton
                        variant="ghost"
                        size="sm"
                        type="button"
                        (click)="terminating.set(false)"
                        i18n="@@terminal.terminateCancel"
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
                      [disabled]="busy()"
                      (click)="terminating.set(true)"
                      i18n="@@terminal.terminate"
                    >
                      Terminate
                    </button>
                  }
                </div>
              </div>

              @if (session.session()?.recording) {
                <xui-callout class="mt-3" color="warning" [title]="recordingTitle" data-recording>
                  <p i18n="@@terminal.recording.body">
                    Everything typed into and printed by this shell is being recorded. The console was created with
                    session recording on, and that cannot be switched off for its lifetime.
                  </p>
                </xui-callout>
              }

              @if (outcome(); as outcome) {
                <xui-callout
                  class="mt-3"
                  [color]="outcome.kind === 'failed' ? 'error' : 'success'"
                  [title]="outcomeTitle(outcome)"
                  [attr.data-outcome]="outcome.kind"
                >
                  @switch (outcome.kind) {
                    @case ('terminated') {
                      <p i18n="@@terminal.outcome.terminated">
                        The shell is stopped. Reconnect starts a new one in the same home directory.
                      </p>
                    }
                    @case ('wasIdle') {
                      <p i18n="@@terminal.outcome.wasIdle">No shell was running — nothing to stop.</p>
                    }
                    @case ('failed') {
                      <p>{{ outcome.message }}</p>
                    }
                  }
                </xui-callout>
              }

              @if (problem(); as problem) {
                <xui-callout class="mt-3" [color]="problem.color" [title]="problem.title" data-problem>
                  <p>{{ problem.message }}</p>
                </xui-callout>
              }

              <cc-terminal-pane
                class="mt-3"
                [label]="paneLabel(console)"
                (data)="session.write($event)"
                (resized)="session.resize($event)"
              />
            </section>
          }
        }
      }
    }
  `
})
export class TerminalBlade {
  readonly subscriptionId = input.required<string>();
  readonly resourceGroup = input.required<string>();
  /** Bound from `?console=`: which console to open. The first in the list when absent. */
  readonly console = input<string>();

  private readonly api = inject(PlatformApi);
  private readonly forms = inject(ResourceFormSource);
  private readonly router = inject(Router);
  private readonly blades = inject(BladeStackStore);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly session = inject(TerminalSession);

  protected readonly tenantId = activeTenantId();
  protected readonly consoles = pageState<readonly TerminalConsolesResource[]>();
  protected readonly list = computed(() => {
    const state = this.consoles();
    return state.kind === 'ready' ? state.value : null;
  });
  protected readonly selected = computed<TerminalConsolesResource | null>(() => {
    const list = this.list();
    if (list === null || list.length === 0) return null;
    const wanted = this.console();
    return list.find(c => c.name === wanted) ?? list[0];
  });

  protected readonly form = pageState<ResourceForm | undefined>();
  protected readonly schema = computed(() => {
    const state = this.form();
    return state.kind === 'ready' ? state.value : undefined;
  });
  protected readonly name = new FormControl('shell', {
    nonNullable: true,
    validators: [Validators.required, Validators.maxLength(63), Validators.pattern(RESOURCE_NAME)]
  });
  protected readonly sending = signal(false);
  protected readonly sendError = signal<string | null>(null);
  private readonly renderer = viewChild(ResourceFormRenderer);
  private readonly pane = viewChild(TerminalPane);

  protected readonly busy = signal(false);
  protected readonly terminating = signal(false);
  protected readonly outcome = signal<Outcome | null>(null);

  protected readonly groupLink = computed(() => links.resourceGroup(this.subscriptionId(), this.resourceGroup()));
  protected readonly createLink = computed(() =>
    links.create(this.subscriptionId(), this.resourceGroup(), CONSOLE_TYPE)
  );

  protected readonly noneTitle = $localize`:@@terminal.none.title:No console in this group`;
  protected readonly refusedTitle = $localize`:@@terminal.create.refused:The platform refused the create`;
  protected readonly submitLabel = $localize`:@@terminal.create.submit:Create console`;
  protected readonly recordingTitle = $localize`:@@terminal.recording.title:This session is being recorded`;

  protected readonly canReconnect = computed(() => {
    const kind = this.session.state().kind;
    return kind === 'closed' || kind === 'refused' || kind === 'failed';
  });
  protected readonly canDisconnect = computed(() => {
    const kind = this.session.state().kind;
    return kind === 'attached' || kind === 'connecting' || kind === 'reconnecting';
  });

  protected readonly stateLabel = computed(() => {
    const state = this.session.state();
    switch (state.kind) {
      case 'idle':
        return $localize`:@@terminal.state.idle:Not connected`;
      case 'connecting':
        return state.attempt === 1
          ? $localize`:@@terminal.state.connecting:Connecting…`
          : $localize`:@@terminal.state.reconnectingNow:Reconnecting (attempt ${state.attempt}:attempt:)…`;
      case 'attached':
        return state.session.state === 'Ready'
          ? $localize`:@@terminal.state.attached:Connected`
          : $localize`:@@terminal.state.starting:Connected — the shell is still starting`;
      case 'reconnecting':
        return $localize`:@@terminal.state.reconnecting:Connection lost — retrying in ${Math.round(state.inMs / 1000)}:seconds: s (attempt ${state.attempt}:attempt: of 5)`;
      case 'refused':
        return $localize`:@@terminal.state.refused:The hub refused the session`;
      case 'failed':
        return $localize`:@@terminal.state.failed:Could not connect`;
      case 'closed':
        return state.reason === 'person'
          ? $localize`:@@terminal.state.closed:Disconnected`
          : $localize`:@@terminal.state.exhausted:Disconnected — reconnecting gave up`;
    }
  });

  /** What to say above the pane when the session cannot proceed on its own. */
  protected readonly problem = computed<{ color: 'error' | 'warning'; title: string; message: string } | null>(() => {
    const state = this.session.state();
    switch (state.kind) {
      case 'refused':
        return {
          color: 'warning',
          title: $localize`:@@terminal.problem.refusedTitle:The platform's terminal data plane is not built yet`,
          message: state.message
        };
      case 'failed':
        return {
          color: 'error',
          title:
            state.status === 412
              ? $localize`:@@terminal.problem.notReady:The console is not ready to attach to yet`
              : $localize`:@@terminal.problem.failedTitle:The platform refused the connection (${state.code}:code:)`,
          message: state.message
        };
      default:
        return null;
    }
  });

  constructor() {
    this.destroyRef.onDestroy(() => this.session.close(false));

    effect(() => {
      const tenantId = this.tenantId();
      const subscriptionId = this.subscriptionId();
      const resourceGroup = this.resourceGroup();

      untracked(() => {
        const route = links.terminal(subscriptionId, resourceGroup);
        this.blades.open({ id: route, title: $localize`:@@terminal.blade:Cloud shell`, route });
        // Per group, and per tenant: a list painted for one group must not be opened against another.
        this.session.close(false);
        this.outcome.set(null);
        this.terminating.set(false);

        if (tenantId === null) return;

        void load(
          this.consoles,
          async () =>
            (await this.api.listCloudTerminal(tenantId, subscriptionId, resourceGroup, { top: 50 })).value.value,
          () =>
            this.tenantId() !== tenantId ||
            this.subscriptionId() !== subscriptionId ||
            this.resourceGroup() !== resourceGroup
        );
      });
    });

    // The create form, only once the list has come back empty: a page that fetched the form
    // document for every visit would pay for a case most visits do not have.
    effect(() => {
      const list = this.list();
      untracked(() => {
        if (list === null || list.length > 0 || this.form().kind !== 'idle') return;
        void load(this.form, () => this.forms.form({ resourceType: CONSOLE_TYPE, apiVersion }));
      });
    });

    // Open the selected console. `selected` changes with the list, the query and the tenant.
    effect(() => {
      const tenantId = this.tenantId();
      const console = this.selected();
      const pane = this.pane();

      untracked(() => {
        if (tenantId === null || console === null || pane === undefined) return;

        const address: ConsoleAddress = {
          tenantId,
          subscriptionId: this.subscriptionId(),
          resourceGroup: this.resourceGroup(),
          name: console.name
        };

        this.session.open(address, pane.fit(), {
          output: bytes => pane.write(bytes),
          notice: text => pane.notice(text)
        });
      });
    });
  }

  protected choose(name: string): void {
    void this.router.navigate([], { queryParams: { console: name }, queryParamsHandling: 'merge' });
  }

  protected paneLabel(console: TerminalConsolesResource): string {
    return $localize`:@@terminal.paneLabel:Terminal for ${console.name}:console:`;
  }

  protected limitsLabel(live: { idleTimeoutSeconds: number; maxDurationSeconds: number }): string {
    const idle = Math.round(live.idleTimeoutSeconds / 60);
    const max = Math.round(live.maxDurationSeconds / 3600);
    return $localize`:@@terminal.limits:Reclaimed after ${idle}:minutes: min idle · stops after ${max}:hours: h`;
  }

  protected outcomeTitle(outcome: Outcome): string {
    switch (outcome.kind) {
      case 'terminated':
        return $localize`:@@terminal.outcomeTitle.terminated:Stopped`;
      case 'wasIdle':
        return $localize`:@@terminal.outcomeTitle.wasIdle:Nothing running`;
      case 'failed':
        return $localize`:@@terminal.outcomeTitle.failed:The platform refused the request`;
    }
  }

  protected nameMessage(): string | null {
    if (!this.name.touched && !this.name.dirty) return null;
    const errors = this.name.errors;
    if (errors === null) return null;
    if (errors['server'] !== undefined) return String(errors['server']);
    if (errors['required'] !== undefined) return $localize`:@@terminal.create.nameRequired:A name is required.`;
    return $localize`:@@terminal.create.nameInvalid:Use 1–63 lowercase letters, digits and hyphens, starting and ending with a letter or digit.`;
  }

  protected async confirmTerminate(console: TerminalConsolesResource): Promise<void> {
    const tenantId = this.tenantId();
    if (tenantId === null || this.busy()) return;

    const subscriptionId = this.subscriptionId();
    const resourceGroup = this.resourceGroup();
    this.busy.set(true);
    this.outcome.set(null);

    try {
      const response = await this.api.terminateCloudTerminal(tenantId, subscriptionId, resourceGroup, console.name);
      if (this.moved(subscriptionId, resourceGroup)) return;
      this.session.close();
      this.outcome.set({ kind: response.value.terminated ? 'terminated' : 'wasIdle' });
    } catch (error) {
      if (this.moved(subscriptionId, resourceGroup)) return;
      this.outcome.set({ kind: 'failed', message: error instanceof Error ? error.message : String(error) });
    } finally {
      this.terminating.set(false);
      this.busy.set(false);
    }
  }

  protected async onCreate(body: Record<string, unknown>): Promise<void> {
    const tenantId = this.tenantId();
    this.name.markAsTouched();
    if (tenantId === null || this.name.invalid || this.sending()) return;

    const address: ResourceAddress = {
      tenantId,
      subscriptionId: this.subscriptionId(),
      resourceGroup: this.resourceGroup(),
      parents: [],
      name: this.name.value
    };

    this.sending.set(true);
    this.sendError.set(null);

    try {
      const response = await this.api.createOrUpdateCloudTerminal(
        address.tenantId,
        address.subscriptionId,
        address.resourceGroup,
        address.name,
        body as unknown as TerminalConsolesData
      );
      const back = links.terminal(address.subscriptionId, address.resourceGroup, address.name);
      const operationId = response.operationUrl === undefined ? null : operationIdOf(response.operationUrl);

      await this.router.navigateByUrl(operationId === null ? back : links.operation(operationId, back));
    } catch (error) {
      this.sending.set(false);

      if (!(error instanceof ApiCallError)) {
        this.sendError.set(error instanceof Error ? error.message : String(error));
        return;
      }

      if (
        error.status === 409 ||
        error.error.code === 'ResourceAlreadyExists' ||
        error.error.code === 'InvalidResourceName'
      ) {
        this.name.setErrors({ server: error.error.message });
        return;
      }

      if (error.error.target !== undefined || (error.error.details?.length ?? 0) > 0) {
        this.renderer()?.reject(error.error);
        return;
      }

      this.sendError.set(error.error.message);
    }
  }

  private moved(subscriptionId: string, resourceGroup: string): boolean {
    return this.subscriptionId() !== subscriptionId || this.resourceGroup() !== resourceGroup;
  }
}
