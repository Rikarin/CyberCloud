import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { XuiButton } from '@xui/button';
import { XuiProgressBar } from '@xui/progress-bar';
import { XuiPopover } from '@xui/popover';
import { NotificationsStore, OperationNotification } from './notifications.store';

/**
 * The notifications tray.
 *
 * docs/plan/20 § Information architecture: "Notifications — A tray fed by `/hubs/operations` —
 * every LRO the user started, with progress." The hub wiring is docs/plan/10 § SignalR and lands
 * with the live-updates work; this renders whatever `NotificationsStore` holds, which is the shape
 * the hub will push.
 *
 * ⚠ Progress is shown, not implied. docs/plan/20 § Live updates: "anything that creates, deletes or
 * costs money … shows the operation's real progress. An optimistic 'deleted!' that later fails is
 * how trust is lost." A running operation with no reported percentage gets `[value]="null"`, which
 * `xui-progress-bar` renders as indeterminate — "we do not know how far along this is" is honest,
 * and a fabricated 60% is not.
 */
@Component({
  selector: 'cc-notifications-tray',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButton, XuiProgressBar, XuiPopover],
  template: `
    <button
      xuiButton
      type="button"
      variant="ghost"
      size="sm"
      interactionKind="click"
      [xuiPopover]="tray"
      [attr.aria-label]="triggerLabel()"
      (click)="onOpen()"
    >
      <span aria-hidden="true">🔔</span>
      @if (store.unreadCount(); as unread) {
        <span
          class="bg-primary text-primary-foreground ms-1 rounded-full px-1.5 text-xs tabular-nums"
          aria-hidden="true"
        >
          {{ unread }}
        </span>
      }
    </button>

    <ng-template #tray>
      <div class="w-96 max-w-[90vw]" role="group" [attr.aria-label]="panelLabel">
        <h2
          class="border-border border-b px-3 py-2 text-sm font-semibold"
          i18n="@@shell.notifications.heading"
        >
          Notifications
        </h2>

        @if (store.items().length === 0) {
          <p
            class="text-foreground-muted px-3 py-6 text-center text-sm"
            i18n="@@shell.notifications.empty"
          >
            No operations yet.
          </p>
        } @else {
          <ul class="max-h-96 overflow-y-auto">
            @for (item of store.items(); track item.id) {
              <li class="border-border/60 border-b px-3 py-2 last:border-b-0">
                <div class="flex items-baseline justify-between gap-2">
                  <span class="text-sm font-medium">{{ item.title }}</span>
                  <span class="text-foreground-muted text-xs">{{ statusText(item) }}</span>
                </div>

                @if (item.detail) {
                  <p class="text-foreground-muted mt-0.5 text-xs">{{ item.detail }}</p>
                }

                @if (item.status === 'running') {
                  <xui-progress-bar
                    class="mt-1.5"
                    [value]="fraction(item)"
                    [aria-label]="item.title"
                  />
                }
              </li>
            }
          </ul>
        }
      </div>
    </ng-template>

    <!--
      The count is announced when it changes, so a background deployment finishing is not something
      only a sighted user finds out about. docs/plan/20 § Accessibility, i18n, theming.
    -->
    <span class="sr-only" aria-live="polite">{{ liveMessage() }}</span>
  `,
})
export class NotificationsTray {
  protected readonly store = inject(NotificationsStore);

  protected readonly panelLabel = $localize`:@@shell.notifications.panel:Notifications`;

  protected readonly triggerLabel = computed(() => {
    const unread = this.store.unreadCount();
    return unread === 0
      ? $localize`:@@shell.notifications.triggerEmpty:Notifications`
      : $localize`:@@shell.notifications.trigger:Notifications, ${unread}:count: unread`;
  });

  protected readonly liveMessage = computed(() => {
    const running = this.store.running().length;
    return running === 0
      ? ''
      : $localize`:@@shell.notifications.running:${running}:count: operations in progress`;
  });

  protected onOpen(): void {
    this.store.markAllRead();
  }

  /** `xui-progress-bar` takes a fraction; the hub reports a percentage. `null` is indeterminate. */
  protected fraction(item: OperationNotification): number | null {
    return item.percentComplete === undefined ? null : item.percentComplete / 100;
  }

  protected statusText(item: OperationNotification): string {
    switch (item.status) {
      case 'running':
        return item.percentComplete === undefined
          ? $localize`:@@shell.notifications.status.running:Running`
          : $localize`:@@shell.notifications.status.runningPct:${item.percentComplete}:pct: per cent`;
      case 'succeeded':
        return $localize`:@@shell.notifications.status.succeeded:Succeeded`;
      case 'failed':
        return $localize`:@@shell.notifications.status.failed:Failed`;
      case 'cancelled':
        return $localize`:@@shell.notifications.status.cancelled:Cancelled`;
    }
  }
}
