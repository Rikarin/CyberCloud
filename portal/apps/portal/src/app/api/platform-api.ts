import { Injectable, inject } from '@angular/core';
import { CyberCloudApi } from '@cybercloud/api';
import { HttpApiTransport } from './http-transport';

/**
 * The generated client, injectable.
 *
 * `CyberCloudApi` is a plain class the generator owns and it takes its transport as a constructor
 * argument, so it cannot be `@Injectable` itself — `libs/api` has no hand-written files. This is
 * the one-line bridge: the same class, constructed over the portal's `HttpApiTransport`.
 *
 * ⚠ `providedIn: 'root'` is per-injector and therefore per-request under SSR, like every store in
 * `libs/shell`. It is also lazy in the bundle sense: nothing in the shell imports this file, so the
 * client's 150 methods arrive with the first route that injects it, not with the initial bundle.
 */
@Injectable({ providedIn: 'root' })
export class PlatformApi extends CyberCloudApi {
  constructor() {
    super(inject(HttpApiTransport));
  }
}
