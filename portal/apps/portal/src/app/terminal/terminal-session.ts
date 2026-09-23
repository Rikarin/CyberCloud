import { Injectable, InjectionToken, computed, inject, signal } from '@angular/core';
import { TerminalConsolesConnectResult } from '@cybercloud/api';
import { ApiCallError } from '../api/http-transport';
import { HubTicketsApi } from '../api/hub-tickets';
import { PlatformApi } from '../api/platform-api';
import { TERMINAL_HUB_FACTORY, TerminalHubConnection, terminalProtocol } from './terminal-hub';

/** Which console a session is for. */
export interface ConsoleAddress {
  readonly tenantId: string;
  readonly subscriptionId: string;
  readonly resourceGroup: string;
  readonly name: string;
}

/** A pane's size in cells. */
export interface PaneSize {
  readonly cols: number;
  readonly rows: number;
}

/** Where a session's bytes and its own remarks go — the pane. */
export interface TerminalSink {
  /** Bytes the shell printed. Raw, so escape sequences reach the emulator intact. */
  output(bytes: Uint8Array): void;
  /** A line from the portal rather than from the shell — "reconnecting…", "this is a new session". */
  notice(text: string): void;
}

/**
 * Where a session is, as one value. The pane renders it; the buttons read it.
 *
 * `refused` and `failed` are different states because they are different answers: `failed` is the
 * platform refusing `connect` or the ticket over HTTP — a 403, a console that has not converged —
 * and `refused` is the hub accepting the socket and the session grain then declining — a session
 * that belongs to someone else, a `connect` permission since revoked. Neither is retried on its
 * own: a refusal is deterministic, and a retry loop against one would be a pane that flickers.
 *
 * `ended` is the hub saying the shell itself is over — it exited, sat idle past its timeout, or was
 * terminated. It is not a dropped socket and is not reconnected: a reconnect is `connect`, and after
 * an idle reclaim that would start the very pod the reclaim stopped, for a tab nobody is looking at.
 */
export type SessionState =
  | { readonly kind: 'idle' }
  | { readonly kind: 'connecting'; readonly attempt: number }
  | { readonly kind: 'attached'; readonly session: TerminalConsolesConnectResult }
  | {
      readonly kind: 'reconnecting';
      readonly session: TerminalConsolesConnectResult;
      readonly attempt: number;
      readonly inMs: number;
    }
  | { readonly kind: 'refused'; readonly message: string }
  | { readonly kind: 'ended'; readonly message: string }
  | { readonly kind: 'failed'; readonly status: number; readonly code: string; readonly message: string }
  | { readonly kind: 'closed'; readonly reason: 'person' | 'exhausted' };

/** How many times a dropped socket is reopened before the pane asks the person. */
export const RECONNECT_ATTEMPTS = 5;

/**
 * The first reconnect delay; each attempt doubles it, capped at ten times. Injectable so a spec
 * can run the whole ladder in milliseconds — the same reason `OPERATION_POLL_MS` is.
 */
export const RECONNECT_BASE_MS = new InjectionToken<number>('cc.terminal.reconnectBaseMs', {
  providedIn: 'root',
  factory: () => 1000
});

/**
 * One shell session: `connect` → ticket → socket → `Attach`, then bytes both ways until the socket
 * drops, and then the same again — docs/plan/19 § Architecture.
 *
 * **Connect and reconnect are the same call, on purpose.** `connect` on the console is an apply
 * rather than a create (`CloudConsoleSessionHandler`'s own remarks), so calling it again while the
 * shell is running returns the *same* `sessionId` and touches nothing; the hub then replays its
 * ring buffer into the pane and the person carries on. When the id comes back *different*, the
 * shell was reclaimed in between — twenty idle minutes, or the hard cap — and the pane says so,
 * because docs/plan/19 § The pod wants "reconnecting" rather than a portal that pretends the
 * session never ended. The home directory is the same either way.
 *
 * **Every reopen mints a fresh ticket.** A ticket is spent by the upgrade that used it, so the
 * socket address is rebuilt each time rather than reused; `HubTicketsApi` says why there is a
 * ticket at all.
 *
 * ⚠ **Nothing here is optimistic.** The state is `attached` only once the hub has answered
 * `Attach`, and `write` before that point is dropped rather than queued: a keystroke buffered
 * across a reconnect could land in a *different* shell than the one it was typed into.
 *
 * ⚠ **Per pane, not per app.** Provided by the blade rather than `providedIn: 'root'`, so two panes
 * are two sessions and closing a blade closes exactly its own socket.
 */
@Injectable()
export class TerminalSession {
  private readonly api = inject(PlatformApi);
  private readonly tickets = inject(HubTicketsApi);
  private readonly openHub = inject(TERMINAL_HUB_FACTORY);
  private readonly baseDelayMs = inject(RECONNECT_BASE_MS);

  readonly state = signal<SessionState>({ kind: 'idle' });
  /** The last `connect` answer, while there is one — for the idle timeout and the recording banner. */
  readonly session = computed<TerminalConsolesConnectResult | null>(() => {
    const state = this.state();
    return state.kind === 'attached' || state.kind === 'reconnecting' ? state.session : null;
  });
  /** Whether the pane should take keystrokes. */
  readonly attached = computed(() => this.state().kind === 'attached');

  private address: ConsoleAddress | null = null;
  private sink: TerminalSink | null = null;
  private size: PaneSize = { cols: 80, rows: 24 };
  private hub: TerminalHubConnection | null = null;
  private previous: TerminalConsolesConnectResult | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;
  /**
   * Bumped by every `open` and `close`, and checked after every `await`. An answer that lands for
   * a session the person has since closed or replaced must not paint over the current one — the
   * same rule the access page's `moved()` applies to a scope.
   */
  private generation = 0;

  /** Starts a session for a console, painting into `sink`. Closes any session already open. */
  open(address: ConsoleAddress, size: PaneSize, sink: TerminalSink): void {
    this.close(false);
    this.address = address;
    this.sink = sink;
    this.size = size;
    this.previous = null;
    void this.connect(++this.generation, 1);
  }

  /** Sends what the person typed. Dropped unless attached — see the class remarks. */
  write(text: string): void {
    const state = this.state();
    if (state.kind !== 'attached' || this.hub === null) return;

    void this.hub
      .invoke(terminalProtocol.send, state.session.sessionId, toBase64(new TextEncoder().encode(text)))
      .catch(error => this.refuse(this.generation, error));
  }

  /** Tells the shell the pane changed size. Remembered for the next `Attach` either way. */
  resize(size: PaneSize): void {
    this.size = size;
    const state = this.state();
    if (state.kind !== 'attached' || this.hub === null) return;

    void this.hub
      .invoke(terminalProtocol.resize, state.session.sessionId, size.cols, size.rows)
      .catch(error => this.refuse(this.generation, error));
  }

  /** Reopens after `closed` or `refused`, from the top: a new `connect`, a new ticket. */
  reconnect(): void {
    if (this.address === null || this.sink === null) return;
    const address = this.address;
    const sink = this.sink;
    // Kept across the reopen, so the pane can still say whether it rejoined the same shell.
    const previous = this.previous;
    this.open(address, this.size, sink);
    this.previous = previous;
  }

  /** Closes the socket and forgets the console. The shell keeps running — that is what `terminate` is for. */
  close(byPerson = true): void {
    this.generation++;
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }

    const hub = this.hub;
    this.hub = null;
    if (hub !== null) void hub.stop().catch(() => undefined);

    if (byPerson) this.state.set({ kind: 'closed', reason: 'person' });
  }

  private async connect(generation: number, attempt: number): Promise<void> {
    const address = this.address;
    const sink = this.sink;
    if (address === null || sink === null) return;

    this.state.set({ kind: 'connecting', attempt });

    let session: TerminalConsolesConnectResult;
    let hub: TerminalHubConnection;

    try {
      session = (
        await this.api.connectCloudTerminal(
          address.tenantId,
          address.subscriptionId,
          address.resourceGroup,
          address.name
        )
      ).value;
      if (this.stale(generation)) return;

      const ticket = await this.tickets.mint(session.hub);
      if (this.stale(generation)) return;

      hub = await this.openHub(this.tickets.socketUrl(session.hub, ticket.ticket));
    } catch (error) {
      if (this.stale(generation)) return;
      this.fail(error);
      return;
    }

    hub.on(terminalProtocol.output, data => {
      if (!this.stale(generation) && typeof data === 'string') sink.output(fromBase64(data));
    });
    hub.on(terminalProtocol.ended, reason => this.end(generation, typeof reason === 'string' ? reason : ''));
    hub.onclose(() => this.dropped(generation, session));

    try {
      await hub.start();
    } catch {
      if (this.stale(generation)) return;
      // The upgrade was refused or the network is gone. A spent or expired ticket lands here too,
      // and the next attempt mints a fresh one.
      this.retryLater(generation, session, attempt);
      return;
    }

    if (this.stale(generation)) {
      void hub.stop().catch(() => undefined);
      return;
    }

    this.hub = hub;

    try {
      await hub.invoke(terminalProtocol.attach, session.sessionId, this.size.cols, this.size.rows);
    } catch (error) {
      this.refuse(generation, error);
      return;
    }

    if (this.stale(generation)) return;

    if (this.previous !== null && this.previous.sessionId !== session.sessionId) {
      sink.notice(
        $localize`:@@terminal.newSession:The shell was reclaimed while the connection was down; this is a new session with the same home directory.`
      );
    } else if (this.previous !== null) {
      sink.notice($localize`:@@terminal.rejoined:Reconnected to the same session.`);
    }

    this.previous = session;
    this.state.set({ kind: 'attached', session });
  }

  /**
   * The hub said the shell is over. Bumps the generation first, so the close that follows is stale
   * and `dropped` does not turn it into a reconnect.
   */
  private end(generation: number, reason: string): void {
    if (this.stale(generation)) return;
    this.generation++;

    const hub = this.hub;
    this.hub = null;
    if (hub !== null) void hub.stop().catch(() => undefined);

    this.sink?.notice(reason);
    this.state.set({ kind: 'ended', message: reason });
  }

  /** The hub said no to a method. Deterministic — the pane shows it, and the person decides. */
  private refuse(generation: number, error: unknown): void {
    if (this.stale(generation)) return;
    this.generation++;

    const hub = this.hub;
    this.hub = null;
    if (hub !== null) void hub.stop().catch(() => undefined);

    this.state.set({ kind: 'refused', message: error instanceof Error ? error.message : String(error) });
  }

  private fail(error: unknown): void {
    this.generation++;

    if (error instanceof ApiCallError) {
      this.state.set({ kind: 'failed', status: error.status, code: error.error.code, message: error.message });
      return;
    }

    this.state.set({
      kind: 'failed',
      status: 0,
      code: 'InternalError',
      message: error instanceof Error ? error.message : String(error)
    });
  }

  /** The socket closed under an attached session. */
  private dropped(generation: number, session: TerminalConsolesConnectResult): void {
    if (this.stale(generation) || this.state().kind !== 'attached') return;
    this.hub = null;
    this.sink?.notice($localize`:@@terminal.dropped:Connection lost — reconnecting…`);
    this.retryLater(generation, session, 0);
  }

  /**
   * Schedules the attempt after `failed`. Attempt 0 is the open that was attached and then dropped;
   * attempt 1 is the first reconnect; after `RECONNECT_ATTEMPTS` of them the pane stops and says so.
   */
  private retryLater(generation: number, session: TerminalConsolesConnectResult, failed: number): void {
    const attempt = failed + 1;

    if (attempt > RECONNECT_ATTEMPTS) {
      this.generation++;
      this.sink?.notice(
        $localize`:@@terminal.exhausted:Could not reconnect after ${RECONNECT_ATTEMPTS}:attempts: attempts. Press Reconnect to try again.`
      );
      this.state.set({ kind: 'closed', reason: 'exhausted' });
      return;
    }

    const inMs = Math.min(this.baseDelayMs * 2 ** (attempt - 1), this.baseDelayMs * 10);
    this.state.set({ kind: 'reconnecting', session, attempt, inMs });
    this.timer = setTimeout(() => {
      this.timer = null;
      if (!this.stale(generation)) void this.connect(generation, attempt);
    }, inMs);
  }

  private stale(generation: number): boolean {
    return generation !== this.generation;
  }
}

/** `byte[]` on SignalR's JSON protocol is a base64 string; the client does not convert on its own. */
export function toBase64(bytes: Uint8Array): string {
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary);
}

export function fromBase64(text: string): Uint8Array {
  const binary = atob(text);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}
