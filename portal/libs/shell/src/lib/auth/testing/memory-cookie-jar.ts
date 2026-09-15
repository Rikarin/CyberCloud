import { Injectable } from '@angular/core';
import { CookieAttributes, CookieJar } from '../cookies';

/** One write the jar saw, with the attributes it was asked for. */
export interface RecordedCookie {
  readonly name: string;
  readonly value: string;
  readonly attributes: CookieAttributes;
}

/**
 * A `CookieJar` that keeps its cookies in a map, for the suites.
 *
 * ⚠ **Why the real jar is not used under jsdom.** `CookieJar` writes every cookie with `Secure`,
 * and jsdom drops a `Secure` cookie set from `http://localhost` — real browsers accept it there,
 * which is what the dev run relies on. So a test that asserted through `document.cookie` would
 * see nothing, and relaxing the attribute for the test's sake would relax it for production.
 * This double records what the shell asked for, attributes included, so a suite can assert the
 * PKCE cookie is path-scoped to the callback without the environment deciding whether it exists.
 * `cookies.spec.ts` covers the real jar's serialization by spying on the setter.
 */
@Injectable()
export class MemoryCookieJar extends CookieJar {
  private readonly jar = new Map<string, RecordedCookie>();

  /** Every write, in order, including ones since overwritten or removed. */
  readonly written: RecordedCookie[] = [];

  override read(name: string): string | null {
    return this.jar.get(name)?.value ?? null;
  }

  override write(name: string, value: string, attributes: CookieAttributes): void {
    const cookie: RecordedCookie = { name, value, attributes };
    this.jar.set(name, cookie);
    this.written.push(cookie);
  }

  override remove(name: string, _path: string): void {
    this.jar.delete(name);
  }

  /** The attributes the last write of `name` carried, or `null` when it was never written. */
  attributesOf(name: string): CookieAttributes | null {
    const matching = this.written.filter(c => c.name === name);
    return matching.length === 0 ? null : matching[matching.length - 1].attributes;
  }
}
