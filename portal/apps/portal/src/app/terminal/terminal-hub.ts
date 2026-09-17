import { InjectionToken } from '@angular/core';

/**
 * The names on the wire between the terminal pane and the gateway's `TerminalHub` — the C# side's
 * `TerminalProtocol`, spelled once here. SignalR binds by string, so a rename on either side is a
 * method the other cannot find; `terminal-session.spec.ts` pins these and `ConsoleSessionTests`
 * pins the C# ones, against the same four words.
 */
export const terminalProtocol = {
  /** Client → hub: join a session and start receiving `Output`. */
  attach: 'Attach',
  /** Client → hub: bytes typed. */
  send: 'Send',
  /** Client → hub: the pane changed size. */
  resize: 'Resize',
  /** Hub → client: bytes the shell printed. */
  output: 'Output'
} as const;

/**
 * What the session needs from a hub connection, and no more.
 *
 * `@microsoft/signalr`'s `HubConnection` has this shape; the seam exists so that
 * `TerminalSession` can be driven by a scripted hub in a spec, and so that the real client — a
 * package that pulls `ws` and `node-fetch` along for Node — is loaded with the terminal's chunk
 * and never with the shell's.
 */
export interface TerminalHubConnection {
  start(): Promise<void>;
  stop(): Promise<void>;
  invoke(method: string, ...args: unknown[]): Promise<unknown>;
  on(method: string, handler: (...args: unknown[]) => void): void;
  onclose(handler: (error?: Error) => void): void;
}

/**
 * Opens a hub connection at a socket address the session already built — `HubTicketsApi.socketUrl`,
 * ticket included.
 *
 * ⚠ The default skips SignalR's negotiate and uses WebSockets only, deliberately. Negotiate is a
 * second HTTP request, and the ticket is redeemable exactly once — it is spent by whichever
 * request reaches the gateway first, and the WebSocket upgrade is the one that must. Long polling
 * and server-sent events would each carry the ticket on every request, so they are out too.
 * `withAutomaticReconnect` is not used for the same reason: it reopens the same URL, and the
 * same URL holds a spent ticket. Reconnect is `TerminalSession`'s, and it mints again.
 */
export const TERMINAL_HUB_FACTORY = new InjectionToken<(socketUrl: string) => Promise<TerminalHubConnection>>(
  'cc.terminal.hub',
  {
    providedIn: 'root',
    factory: () => async socketUrl => {
      const signalr = await import('@microsoft/signalr');

      return new signalr.HubConnectionBuilder()
        .withUrl(socketUrl, { skipNegotiation: true, transport: signalr.HttpTransportType.WebSockets })
        .configureLogging(signalr.LogLevel.Warning)
        .build();
    }
  }
);
