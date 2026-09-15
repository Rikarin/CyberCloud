import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  InjectionToken,
  computed,
  effect,
  inject,
  input,
  untracked
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { OperationProgress, OperationState, OperationStatus } from '@cybercloud/api';
import { BladeStackStore, NotificationsStore } from '@cybercloud/shell';
import { XuiButton } from '@xui/button';
import { XuiCallout } from '@xui/callout';
import { XuiProgressBar } from '@xui/progress-bar';
import { XuiTimeline, XuiTimelineItem } from '@xui/timeline';
import { ApiCallError } from '../../app/api/http-transport';
import { PlatformApi } from '../../app/api/platform-api';
import { NeedsTenant, PageStatus, activeTenantId, failure, pageState } from '../shared/page-state';

/** The states after which nothing changes — docs/plan/10 § Long-running operations, over HTTP. */
const TERMINAL: readonly OperationState[] = ['Succeeded', 'Failed', 'Canceled'];

/**
 * How long to wait between polls when the platform did not say.
 *
 * The 202 carries `Retry-After`, but the operation view is reached by URL — from the create blade,
 * from the notifications tray, from a pasted link — and by then the header is gone. Two seconds is
 * what the .NET SDK's poller falls back to. A token so a test can poll in milliseconds without
 * faking the clock under Angular's scheduler.
 */
export const OPERATION_POLL_MS = new InjectionToken<number>('cc.operation.pollMs', {
  providedIn: 'root',
  factory: () => 2_000
});

/**
 * The operation view: one long-running operation, polled until it stops.
 *
 * docs/plan/10 § Long-running operations, over HTTP: poll the `Azure-AsyncOperation` target
 * "until status is terminal, then GET the resource". The `progress` array is this platform's
 * addition to Azure's pattern — "what makes a nine-minute cluster creation tolerable" — and it is
 * rendered as the timeline, newest last, with the latest `percentComplete` on the bar.
 *
 * ⚠ **Progress is shown, never implied.** docs/plan/20 § Live updates: "An optimistic 'deleted!'
 * that later fails is how trust is lost." Until the platform says `Succeeded` the link to the
 * resource is not offered; on `Failed` the platform's own error is shown, with its code, because
 * `ProvisioningFailed` and `QuotaExceeded` want different next steps.
 *
 * ⚠ **The tray is kept in step.** Every poll upserts `NotificationsStore`, keyed on the operation
 * id, so leaving this page does not lose the operation — the tray docs/plan/20 asks for is "every
 * LRO the user started, with progress", and until `/hubs/operations` lands this is what feeds it.
 *
 * `then` is where to go once it succeeds — the resource blade the create page handed over. It is
 * a same-origin path and nothing else: a full URL in a query string would be an open redirect.
 */
@Component({
  selector: 'cc-operation-view',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, XuiProgressBar, XuiTimeline, XuiTimelineItem, XuiCallout, XuiButton, NeedsTenant, PageStatus],
  host: { class: 'block p-6' },
  template: `
    @if (tenantId() === null) {
      <cc-needs-tenant />
    } @else {
      <h1 class="text-lg font-semibold" i18n="@@operation.heading">Operation</h1>
      <p class="text-foreground-muted mt-1 font-mono text-xs">{{ operationId() }}</p>

      @if (status(); as status) {
        <div class="mt-4 flex items-center gap-3">
          <span class="text-sm font-medium" [attr.data-status]="status.status">{{ statusText(status.status) }}</span>
          @if (!terminal()) {
            <span class="text-foreground-muted text-xs" role="status" aria-live="polite" i18n="@@operation.polling"
              >Polling…</span
            >
          }
        </div>

        <xui-progress-bar class="mt-2" [value]="fraction(status)" [aria-label]="progressLabel" />

        @if (status.status === 'Failed' && status.error; as error) {
          <xui-callout class="mt-4" color="error" [title]="failedTitle(error.code)">
            <p>{{ error.message }}</p>
          </xui-callout>
        }

        @if (status.status === 'Canceled') {
          <xui-callout class="mt-4" color="warning" [title]="canceledTitle">
            <p i18n="@@operation.canceledBody">
              Cancellation completes rather than abandons: everything already applied has been removed.
            </p>
          </xui-callout>
        }

        @if (status.progress?.length) {
          <xui-timeline class="mt-6">
            @for (step of status.progress; track $index) {
              <xui-timeline-item
                [color]="stepColor(step, $index === status.progress!.length - 1, status.status)"
                [label]="stepTime(step)"
              >
                <span class="font-medium">{{ step.step }}</span>
                @if (step.message) {
                  <span class="text-foreground-muted"> — {{ step.message }}</span>
                }
                @if (step.percentComplete !== undefined) {
                  <span class="text-foreground-muted ms-1 text-xs tabular-nums">{{ step.percentComplete }}%</span>
                }
              </xui-timeline-item>
            }
          </xui-timeline>
        } @else if (!terminal()) {
          <p class="text-foreground-muted mt-4 text-sm" i18n="@@operation.noSteps">No steps reported yet.</p>
        }

        @if (status.status === 'Succeeded' && target(); as target) {
          <a xuiButton color="primary" class="mt-6" [routerLink]="target" i18n="@@operation.open">Open the resource</a>
        }
      } @else {
        <cc-page-status [state]="state()" />
      }
    }
  `
})
export class OperationView {
  readonly operationId = input.required<string>();
  /** Bound from `?then=`. */
  readonly then = input<string>();

  private readonly api = inject(PlatformApi);
  private readonly notifications = inject(NotificationsStore);
  private readonly blades = inject(BladeStackStore);
  private readonly destroyRef = inject(DestroyRef);
  private readonly pollMs = inject(OPERATION_POLL_MS);

  protected readonly tenantId = activeTenantId();

  /**
   * `then`, admitted only as a same-origin path. `withComponentInputBinding()` hands over whatever
   * the query string says, and a value like `https://…` or `//host` in a link somebody pasted
   * would make "Open the resource" an open redirect. A path that starts with one `/` is the only
   * shape `portal-links.ts` produces, so it is the only shape accepted.
   */
  protected readonly target = computed(() => {
    const then = this.then();
    return then !== undefined && /^\/(?!\/)/.test(then) ? then : undefined;
  });

  protected readonly state = pageState<OperationStatus>();
  protected readonly status = computed(() => {
    const state = this.state();
    return state.kind === 'ready' ? state.value : null;
  });
  protected readonly terminal = computed(() => {
    const status = this.status();
    return status !== null && TERMINAL.includes(status.status);
  });

  protected readonly progressLabel = $localize`:@@operation.progressLabel:Operation progress`;
  protected readonly canceledTitle = $localize`:@@operation.canceled:Canceled`;

  private timer: ReturnType<typeof setTimeout> | null = null;
  private startedAt = Date.now();

  constructor() {
    this.destroyRef.onDestroy(() => this.stop());

    effect(() => {
      const operationId = this.operationId();
      const tenantId = this.tenantId();

      untracked(() => {
        this.stop();
        this.blades.open({
          id: `operation:${operationId}`,
          title: $localize`:@@operation.blade:Operation`,
          route: `/operations/${operationId}`
        });

        if (tenantId === null) return;

        this.startedAt = Date.now();
        this.state.set({ kind: 'loading' });
        void this.poll(operationId);
      });
    });
  }

  /**
   * One poll, then the next after `OPERATION_POLL_MS` unless the state is terminal.
   *
   * ⚠ A failed poll — a 404 on an id the platform has forgotten, a network drop — stops polling
   * and shows why. Retrying a 404 for ever is a spinner nobody can leave; a person can press
   * reload for the transient case.
   */
  private async poll(operationId: string): Promise<void> {
    try {
      const response = await this.api.getOperation(operationId);
      if (this.operationId() !== operationId) return;

      this.state.set({ kind: 'ready', value: response.value });
      this.notify(operationId, response.value);

      if (!TERMINAL.includes(response.value.status)) {
        this.timer = setTimeout(() => void this.poll(operationId), this.pollMs);
      }
    } catch (error) {
      if (this.operationId() !== operationId) return;
      this.state.set(failure(error));

      if (error instanceof ApiCallError) {
        this.notifications.upsert({
          id: operationId,
          title: this.title(),
          status: 'failed',
          detail: error.error.message,
          startedAtEpochMs: this.startedAt,
          read: false
        });
      }
    }
  }

  private notify(operationId: string, status: OperationStatus): void {
    const last = status.progress?.at(-1);

    this.notifications.upsert({
      id: operationId,
      title: this.title(),
      status: toTrayStatus(status.status),
      ...(status.percentComplete === undefined ? {} : { percentComplete: status.percentComplete }),
      ...(last === undefined ? {} : { detail: last.message ?? last.step }),
      startedAtEpochMs: this.startedAt,
      read: !TERMINAL.includes(status.status),
      ...(this.target() === undefined ? {} : { resourceId: this.target() as string })
    });
  }

  private title(): string {
    const target = this.target();
    return target === undefined
      ? $localize`:@@operation.trayTitle:Operation ${this.operationId().slice(0, 8)}:id:`
      : $localize`:@@operation.trayTitleFor:${decodeURIComponent(target.split('/').at(-1) ?? '')}:name:`;
  }

  private stop(): void {
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }

  protected fraction(status: OperationStatus): number | null {
    if (status.status === 'Succeeded') return 1;
    const percent = status.percentComplete ?? status.progress?.at(-1)?.percentComplete;
    return percent === undefined ? null : percent / 100;
  }

  protected statusText(state: OperationState): string {
    switch (state) {
      case 'NotStarted':
        return $localize`:@@operation.status.notStarted:Not started`;
      case 'Running':
        return $localize`:@@operation.status.running:Running`;
      case 'Succeeded':
        return $localize`:@@operation.status.succeeded:Succeeded`;
      case 'Failed':
        return $localize`:@@operation.status.failed:Failed`;
      case 'Canceled':
        return $localize`:@@operation.status.canceled:Canceled`;
    }
  }

  protected failedTitle(code: string): string {
    return $localize`:@@operation.failedTitle:Failed (${code}:code:)`;
  }

  protected stepColor(
    step: OperationProgress,
    last: boolean,
    state: OperationState
  ): 'primary' | 'success' | 'error' | 'warning' | 'muted' {
    if (!last) return 'success';
    if (state === 'Succeeded') return 'success';
    if (state === 'Failed') return 'error';
    if (state === 'Canceled') return 'warning';
    return step.percentComplete === 100 ? 'success' : 'primary';
  }

  protected stepTime(step: OperationProgress): string {
    const at = new Date(step.at);
    return Number.isNaN(at.getTime()) ? step.at : at.toLocaleTimeString();
  }
}

function toTrayStatus(state: OperationState): 'running' | 'succeeded' | 'failed' | 'cancelled' {
  switch (state) {
    case 'Succeeded':
      return 'succeeded';
    case 'Failed':
      return 'failed';
    case 'Canceled':
      return 'cancelled';
    default:
      return 'running';
  }
}
