import { Injectable, computed, signal } from '@angular/core';

/**
 * Status of a long-running operation, mirroring what `/hubs/operations` publishes
 * (docs/plan/10 § SignalR).
 */
export type OperationStatus = 'running' | 'succeeded' | 'failed' | 'cancelled';

/**
 * One entry in the notifications tray.
 *
 * docs/plan/20 § Information architecture: "Notifications — A tray fed by `/hubs/operations` —
 * every LRO the user started, with progress."
 */
export interface OperationNotification {
  readonly id: string;
  readonly title: string;
  readonly status: OperationStatus;
  /** 0–100 where the operation reports it; absent where it does not. */
  readonly percentComplete?: number;
  readonly detail?: string;
  readonly startedAtEpochMs: number;
  readonly resourceId?: string;
  /** Cleared when the tray is opened, which is what makes the unread count mean something. */
  readonly read: boolean;
}

/**
 * The notifications tray's state.
 *
 * ⚠ Deliberately has no optimistic path. docs/plan/20 § Live updates: "Optimistic UI is used
 * narrowly and deliberately. Tags and names update optimistically; anything that creates, deletes
 * or costs money does not — it shows the operation's real progress. An optimistic 'deleted!' that
 * later fails is how trust is lost." Every entry here is an operation the server has acknowledged,
 * so `upsert` is only ever called from a hub message or from the response to the call that started
 * the operation.
 */
@Injectable({ providedIn: 'root' })
export class NotificationsStore {
  private readonly _items = signal<readonly OperationNotification[]>([]);

  readonly items = this._items.asReadonly();
  readonly unreadCount = computed(() => this._items().filter((n) => !n.read).length);
  readonly running = computed(() => this._items().filter((n) => n.status === 'running'));

  /**
   * Operations arrive out of order across a reconnect — docs/plan/10 § SignalR resubscribes with a
   * `since` version, which replays. Keying on id and replacing makes a replay idempotent.
   */
  upsert(notification: OperationNotification): void {
    this._items.update((items) => {
      const index = items.findIndex((n) => n.id === notification.id);
      if (index < 0) return [notification, ...items];

      const next = [...items];
      next[index] = notification;
      return next;
    });
  }

  markAllRead(): void {
    this._items.update((items) =>
      items.map((n) => (n.read ? n : { ...n, read: true })),
    );
  }

  dismiss(id: string): void {
    this._items.update((items) => items.filter((n) => n.id !== id));
  }

  clear(): void {
    this._items.set([]);
  }
}
