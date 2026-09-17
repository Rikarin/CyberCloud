import { Injectable, inject } from '@angular/core';
import { API_BASE_PATH, HttpApiTransport } from './http-transport';

/** What `POST /hubs/{hub}/ticket` answers — the gateway's `HubTickets.Body`. */
export interface HubTicket {
  /** The opaque value the WebSocket upgrade carries as `?ticket=`. Thirty seconds, one use. */
  readonly ticket: string;
  /** The hub it opens, as a path: `/hubs/terminal`. */
  readonly hub: string;
  /** When it stops working, ISO 8601. */
  readonly expiresAt: string;
}

/**
 * The shape a hub path has to have before this portal will mint a ticket for it or open a
 * socket to it: `/hubs/{name}`, one lowercase word, nothing else. The value comes from the
 * platform — the console's `connect` result carries it — and a server-supplied path is still a
 * path this client would send the person's bearer token to, so it is checked rather than trusted.
 */
export const HUB_PATH = /^\/hubs\/[a-z]+$/;

/**
 * The hub ticket, and the socket address it opens — docs/plan/10 § SignalR, docs/plan/19
 * § Architecture.
 *
 * A browser cannot put an `Authorization` header on a WebSocket, and the platform's stage 2
 * reads nothing else. The SignalR client's own answer is `?access_token=<bearer>`, and that puts
 * the token in every proxy log and browser history between here and the gateway. So the portal
 * asks for a *ticket* over an ordinary authenticated `POST` — thirty seconds, one use, bound to
 * the caller and the one hub — and the socket carries that instead. ⚠ The bearer token never
 * appears in a URL this class builds; `hub-tickets.spec.ts` sabotages that and checks.
 *
 * ⚠ **Not in the generated client, on purpose.** `libs/api` is generated from the provider
 * registry and a hub is not a resource type; like `RoleAssignmentsApi` this goes through the same
 * `HttpApiTransport`, so the token, the api-version and the error mapping stay in one place.
 */
@Injectable({ providedIn: 'root' })
export class HubTicketsApi {
  private readonly transport = inject(HttpApiTransport);
  private readonly basePath = inject(API_BASE_PATH);

  /** Mints a ticket for one hub. `hub` is the path `connect` returned, `/hubs/terminal`. */
  async mint(hub: string): Promise<HubTicket> {
    assertHubPath(hub);
    return (await this.transport.send<HubTicket>({ method: 'POST', path: `${hub}/ticket` })).value;
  }

  /**
   * The WebSocket address for a hub and a ticket: the API base on this origin, `ws:` for `http:`
   * and `wss:` for `https:`, and the ticket as the one query parameter.
   *
   * ⚠ Same-origin by construction, as every API call is (`API_BASE_PATH`'s own doc). On the server,
   * where there is no `location`, this throws rather than guessing an origin — a socket is a
   * browser thing, and the terminal pane only asks after the first render there.
   */
  socketUrl(hub: string, ticket: string): string {
    assertHubPath(hub);

    if (typeof location === 'undefined') {
      throw new Error('A hub socket can only be addressed in the browser.');
    }

    const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
    return `${scheme}//${location.host}${this.basePath}${hub}?ticket=${encodeURIComponent(ticket)}`;
  }
}

function assertHubPath(hub: string): void {
  if (!HUB_PATH.test(hub)) {
    throw new Error(`'${hub}' is not a hub path. A hub is /hubs/{name}, and this client opens nothing else.`);
  }
}
