import { ChangeDetectionStrategy, Component, Signal, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TenantContextStore } from '@cybercloud/shell';
import { XuiButton } from '@xui/button';
import { XuiCallout } from '@xui/callout';
import { XuiSpinner } from '@xui/spinner';
import { ApiCallError } from '../../app/api/http-transport';

/**
 * What a page that calls the API is doing, as one value.
 *
 * Every blade here goes through the same four states and renders them the same way, so the
 * pages hold a `PageState` rather than three booleans that can disagree.
 */
export type PageState<T> =
  | { readonly kind: 'idle' }
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly value: T }
  | { readonly kind: 'failed'; readonly status: number; readonly code: string; readonly message: string };

/** The `failed` state for whatever `send` threw. */
export function failure(error: unknown): PageState<never> {
  if (error instanceof ApiCallError) {
    return { kind: 'failed', status: error.status, code: error.error.code, message: error.error.message };
  }

  return {
    kind: 'failed',
    status: 0,
    code: 'InternalError',
    message: error instanceof Error ? error.message : String(error)
  };
}

/**
 * Runs one call and reports it as a `PageState`.
 *
 * ⚠ `stale` is checked before the answer lands: a blade whose inputs changed mid-flight — the
 * user clicked the next resource — must not paint the previous one's answer over the new one's
 * spinner. The caller passes a function that says whether this request is still the one wanted.
 */
export async function load<T>(
  state: { set(value: PageState<T>): void },
  call: () => Promise<T>,
  stale: () => boolean = () => false
): Promise<void> {
  state.set({ kind: 'loading' });

  try {
    const value = await call();
    if (!stale()) state.set({ kind: 'ready', value });
  } catch (error) {
    if (!stale()) state.set(failure(error));
  }
}

/** A writable `PageState` signal, starting idle. */
export function pageState<T>(): ReturnType<typeof signal<PageState<T>>> {
  return signal<PageState<T>>({ kind: 'idle' });
}

/**
 * The tenant every call is made in, or `null` while nobody is signed in.
 *
 * docs/plan/20 § Information architecture, on the context bar: "Getting this wrong means people
 * act in the wrong subscription". The tenant is read from the one store the context bar shows,
 * never from a route or a query string, so what the bar says and what a page sends cannot differ.
 */
export function activeTenantId(): Signal<string | null> {
  const context = inject(TenantContextStore);
  return computed(() => context.activeTenant()?.id ?? null);
}

/**
 * What a page shows in place of its body while it has no tenant to act in.
 *
 * `TenantContextStore` is filled by `AuthFlow.ensureContext` once a token is accepted, so this is
 * what the server render shows and what a page shows for the moment between the guard passing
 * and the tenant's `GET` answering. It says so rather than rendering an empty page, and it says it
 * once, here, so the eleven pages that need a tenant do not each invent a wording.
 */
@Component({
  selector: 'cc-needs-tenant',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiCallout],
  template: `
    <xui-callout color="warning" [title]="title">
      <p i18n="@@page.needsTenant.body">
        Nothing is signed in, so there is no tenant to act in. The context bar fills in once the portal holds an access
        token.
      </p>
    </xui-callout>
  `
})
export class NeedsTenant {
  protected readonly title = $localize`:@@page.needsTenant.title:No tenant selected`;
}

/** The loading and failed halves of a `PageState`, rendered the same way on every page. */
@Component({
  selector: 'cc-page-status',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiCallout, XuiSpinner, XuiButton, RouterLink],
  template: `
    @switch (state().kind) {
      @case ('loading') {
        <div class="text-foreground-muted flex items-center gap-2 py-6 text-sm" role="status" aria-live="polite">
          <xui-spinner size="sm" />
          <span i18n="@@page.loading">Loading…</span>
        </div>
      }
      @case ('failed') {
        <xui-callout color="error" [title]="failedTitle()">
          <p>{{ failedMessage() }}</p>
          @if (retryLink(); as link) {
            <a xuiButton variant="outline" size="sm" class="mt-2" [routerLink]="link" i18n="@@page.back">Go back</a>
          }
        </xui-callout>
      }
    }
  `
})
export class PageStatus {
  readonly state = input.required<PageState<unknown>>();
  /** Where "Go back" points after a failure, when there is somewhere sensible. */
  readonly retryLink = input<string | null>(null);

  protected readonly failedTitle = computed(() => {
    const state = this.state();
    if (state.kind !== 'failed') return '';

    switch (state.status) {
      case 404:
        return $localize`:@@page.failed.notFound:Not found`;
      case 403:
        return $localize`:@@page.failed.forbidden:Not allowed`;
      case 0:
        return $localize`:@@page.failed.unreachable:The platform could not be reached`;
      default:
        return $localize`:@@page.failed.generic:The platform refused the request (${state.code}:code:)`;
    }
  });

  protected readonly failedMessage = computed(() => {
    const state = this.state();
    return state.kind === 'failed' ? state.message : '';
  });
}
