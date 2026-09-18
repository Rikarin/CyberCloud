import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { TerminalConsolesConnectResult } from '@cybercloud/api';
import { TERMINAL_HUB_FACTORY, TerminalHubConnection, terminalProtocol } from './terminal-hub';
import {
  ConsoleAddress,
  RECONNECT_ATTEMPTS,
  RECONNECT_BASE_MS,
  TerminalSession,
  TerminalSink,
  fromBase64,
  toBase64
} from './terminal-session';

/**
 * The session driver against a scripted hub: the order of calls, the words on the wire, and what
 * happens when the socket drops, when the hub refuses, and when the platform does.
 */
const ADDRESS: ConsoleAddress = {
  tenantId: 't-acme',
  subscriptionId: '0f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43',
  resourceGroup: 'example-rg',
  name: 'shell'
};
const CONNECT_PATH = `/api/tenants/${ADDRESS.tenantId}/subscriptions/${ADDRESS.subscriptionId}/resourceGroups/${ADDRESS.resourceGroup}/providers/CyberCloud.Terminal/consoles/${ADDRESS.name}/connect`;
const TICKET_PATH = '/api/hubs/terminal/ticket';

function connected(
  sessionId = 'pod-uid-1',
  extra: Partial<TerminalConsolesConnectResult> = {}
): TerminalConsolesConnectResult {
  return {
    sessionId,
    hub: '/hubs/terminal',
    state: 'Ready',
    idleTimeoutSeconds: 1200,
    maxDurationSeconds: 28_800,
    recording: false,
    ...extra
  };
}

/** A hub that records what it is asked and answers what the spec scripts. */
class FakeHub implements TerminalHubConnection {
  readonly invocations: { method: string; args: unknown[] }[] = [];
  readonly handlers = new Map<string, (...args: unknown[]) => void>();
  started = false;
  stopped = false;
  private closeHandler: ((error?: Error) => void) | null = null;

  constructor(
    readonly url: string,
    private readonly script: { startFails?: boolean; refuse?: string } = {}
  ) {}

  start(): Promise<void> {
    if (this.script.startFails) return Promise.reject(new Error('Error: Failed to start the connection'));
    this.started = true;
    return Promise.resolve();
  }

  stop(): Promise<void> {
    this.stopped = true;
    return Promise.resolve();
  }

  invoke(method: string, ...args: unknown[]): Promise<unknown> {
    this.invocations.push({ method, args });
    if (this.script.refuse !== undefined) return Promise.reject(new Error(this.script.refuse));
    return Promise.resolve(undefined);
  }

  on(method: string, handler: (...args: unknown[]) => void): void {
    this.handlers.set(method, handler);
  }

  onclose(handler: (error?: Error) => void): void {
    this.closeHandler = handler;
  }

  /** The server pushes output. */
  output(bytes: Uint8Array): void {
    this.handlers.get(terminalProtocol.output)?.(toBase64(bytes));
  }

  /** The socket drops. */
  drop(): void {
    this.closeHandler?.(new Error('gone'));
  }
}

class RecordingSink implements TerminalSink {
  readonly bytes: string[] = [];
  readonly notices: string[] = [];
  output(bytes: Uint8Array): void {
    this.bytes.push(new TextDecoder().decode(bytes));
  }
  notice(text: string): void {
    this.notices.push(text);
  }
}

describe('TerminalSession', () => {
  let http: HttpTestingController;
  let session: TerminalSession;
  let hubs: FakeHub[];
  let nextScript: { startFails?: boolean; refuse?: string };
  let sink: RecordingSink;

  beforeEach(() => {
    hubs = [];
    nextScript = {};
    sink = new RecordingSink();

    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        TerminalSession,
        { provide: RECONNECT_BASE_MS, useValue: 40 },
        {
          provide: TERMINAL_HUB_FACTORY,
          useValue: (url: string) => {
            const hub = new FakeHub(url, nextScript);
            hubs.push(hub);
            return Promise.resolve(hub);
          }
        }
      ]
    });

    http = TestBed.inject(HttpTestingController);
    session = TestBed.inject(TerminalSession);
  });

  afterEach(() => {
    session.close(false);
    http.verify();
  });

  const tick = (): Promise<void> => new Promise(resolve => setTimeout(resolve, 0));

  /** Answers connect, then the ticket, then lets the session reach the hub. */
  async function answerConnect(result = connected(), ticket = 'ticket-1'): Promise<void> {
    await tick();
    http.expectOne(r => r.method === 'POST' && r.url === CONNECT_PATH).flush(result);
    await tick();
    http
      .expectOne(r => r.method === 'POST' && r.url === TICKET_PATH)
      .flush({
        ticket,
        hub: '/hubs/terminal',
        expiresAt: '2026-08-11T12:00:30Z'
      });
    // The factory, start and Attach are three awaits.
    await tick();
    await tick();
    await tick();
  }

  it('connects, mints a ticket, opens the socket with it, attaches with the pane size, and streams both ways', async () => {
    session.open(ADDRESS, { cols: 120, rows: 40 }, sink);
    expect(session.state()).toEqual({ kind: 'connecting', attempt: 1 });

    await answerConnect();

    expect(hubs).toHaveLength(1);
    const hub = hubs[0];
    // ⚠ The ticket and nothing else in the address — never the bearer token.
    expect(hub.url).toBe('ws://localhost/api/hubs/terminal?ticket=ticket-1');
    expect(hub.started).toBe(true);
    expect(hub.invocations).toEqual([{ method: 'Attach', args: ['pod-uid-1', 120, 40] }]);
    expect(session.state().kind).toBe('attached');
    expect(session.attached()).toBe(true);

    hub.output(new TextEncoder().encode('$ '));
    expect(sink.bytes).toEqual(['$ ']);

    session.write('ls\r');
    session.resize({ cols: 100, rows: 30 });
    expect(hub.invocations.slice(1)).toEqual([
      { method: 'Send', args: ['pod-uid-1', toBase64(new TextEncoder().encode('ls\r'))] },
      { method: 'Resize', args: ['pod-uid-1', 100, 30] }
    ]);
    // The words on the wire are the C# side's TerminalProtocol constants.
    expect(terminalProtocol).toEqual({ attach: 'Attach', send: 'Send', resize: 'Resize', output: 'Output' });
  });

  it('drops keystrokes typed before Attach has been answered rather than queuing them', async () => {
    session.open(ADDRESS, { cols: 80, rows: 24 }, sink);
    session.write('early');

    await answerConnect();

    expect(hubs[0].invocations.map(i => i.method)).toEqual(['Attach']);
  });

  it('shows the hub refusing the session — the owed data plane — and does not retry on its own', async () => {
    nextScript = { refuse: "The cloud terminal's session grain is docs/plan/19 and is not implemented." };
    session.open(ADDRESS, { cols: 80, rows: 24 }, sink);

    await answerConnect();

    expect(session.state()).toEqual({
      kind: 'refused',
      message: "The cloud terminal's session grain is docs/plan/19 and is not implemented."
    });
    expect(hubs[0].stopped).toBe(true);

    // Nothing further in flight: no second connect, no second ticket.
    await new Promise(resolve => setTimeout(resolve, 30));
    expect(hubs).toHaveLength(1);
  });

  it('reports the platform refusing connect as failed, with its code, and mints no ticket', async () => {
    session.open(ADDRESS, { cols: 80, rows: 24 }, sink);
    await tick();

    http
      .expectOne(r => r.url === CONNECT_PATH)
      .flush(
        { error: { code: 'PreconditionFailed', message: 'cannot be attached to yet' } },
        { status: 412, statusText: 'Precondition Failed' }
      );
    await tick();

    expect(session.state()).toEqual({
      kind: 'failed',
      status: 412,
      code: 'PreconditionFailed',
      message: 'cannot be attached to yet'
    });
    expect(hubs).toHaveLength(0);
  });

  it('reconnects when the socket drops: a new connect, a new ticket, and the same session rejoined', async () => {
    session.open(ADDRESS, { cols: 80, rows: 24 }, sink);
    await answerConnect();

    hubs[0].drop();
    expect(session.state()).toMatchObject({ kind: 'reconnecting', attempt: 1, inMs: 40 });
    expect(sink.notices).toEqual(['Connection lost — reconnecting…']);

    await new Promise(resolve => setTimeout(resolve, 50));
    await answerConnect(connected('pod-uid-1'), 'ticket-2');

    expect(hubs).toHaveLength(2);
    expect(hubs[1].url).toBe('ws://localhost/api/hubs/terminal?ticket=ticket-2');
    expect(hubs[1].invocations).toEqual([{ method: 'Attach', args: ['pod-uid-1', 80, 24] }]);
    expect(session.state().kind).toBe('attached');
    expect(sink.notices).toEqual(['Connection lost — reconnecting…', 'Reconnected to the same session.']);
  });

  it('says so when the reconnect lands in a new shell — the pod was reclaimed in between', async () => {
    session.open(ADDRESS, { cols: 80, rows: 24 }, sink);
    await answerConnect(connected('pod-uid-1'));

    hubs[0].drop();
    await new Promise(resolve => setTimeout(resolve, 50));
    await answerConnect(connected('pod-uid-2'), 'ticket-2');

    expect(session.state().kind).toBe('attached');
    expect(sink.notices[1]).toContain('this is a new session with the same home directory');
  });

  it('backs off across the ladder and gives up after the fifth reconnect', async () => {
    session.open(ADDRESS, { cols: 80, rows: 24 }, sink);
    await answerConnect();

    nextScript = { startFails: true };
    hubs[0].drop();

    const delays: number[] = [];
    for (let attempt = 1; attempt <= RECONNECT_ATTEMPTS; attempt++) {
      const state = session.state();
      expect(state).toMatchObject({ kind: 'reconnecting', attempt });
      const inMs = state.kind === 'reconnecting' ? state.inMs : 0;
      delays.push(inMs);
      await new Promise(resolve => setTimeout(resolve, inMs + 10));
      await answerConnect(connected(), `ticket-${attempt + 1}`);
    }

    // Doubling from the base, and then the cap of ten times the base.
    expect(delays).toEqual([40, 80, 160, 320, 400]);
    expect(session.state()).toEqual({ kind: 'closed', reason: 'exhausted' });
    expect(sink.notices.at(-1)).toContain('Could not reconnect after 5 attempts');

    // Reconnect from the button starts the ladder again from the top.
    nextScript = {};
    session.reconnect();
    expect(session.state()).toEqual({ kind: 'connecting', attempt: 1 });
    await answerConnect(connected(), 'ticket-fresh');
    expect(session.state().kind).toBe('attached');
  });

  it('closing stops the socket and ignores anything that lands afterwards', async () => {
    session.open(ADDRESS, { cols: 80, rows: 24 }, sink);
    await tick();
    const connect = http.expectOne(r => r.url === CONNECT_PATH);

    session.close();
    expect(session.state()).toEqual({ kind: 'closed', reason: 'person' });

    connect.flush(connected());
    await tick();
    await tick();

    expect(hubs).toHaveLength(0);
    expect(session.state()).toEqual({ kind: 'closed', reason: 'person' });
  });

  it('refuses a connect result whose hub is not a hub path, before minting anything', async () => {
    session.open(ADDRESS, { cols: 80, rows: 24 }, sink);
    await tick();
    http
      .expectOne(r => r.url === CONNECT_PATH)
      .flush(connected('pod-uid-1', { hub: '/tenants/t-acme/subscriptions/x' }));
    await tick();
    await tick();

    expect(session.state()).toMatchObject({ kind: 'failed', status: 0 });
    expect(hubs).toHaveLength(0);
    http.expectNone(r => r.url === TICKET_PATH);
  });

  it("round-trips bytes through base64 the way SignalR's JSON protocol carries byte[]", () => {
    const bytes = new Uint8Array([0, 27, 91, 50, 74, 255, 128]);
    expect(fromBase64(toBase64(bytes))).toEqual(bytes);
    expect(toBase64(new TextEncoder().encode('hi'))).toBe('aGk=');
  });
});
