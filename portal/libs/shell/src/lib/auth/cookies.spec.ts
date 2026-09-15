import { DOCUMENT } from '@angular/common';
import { PLATFORM_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { CookieJar } from './cookies';

/**
 * The real jar, against the string it hands the browser. jsdom drops a `Secure` cookie on
 * `http://localhost`, so the assertions here read the setter rather than the jar — what matters
 * is what the shell asks the browser for, attribute by attribute.
 */
describe('CookieJar', () => {
  let written: string[];

  beforeEach(() => {
    written = [];
    jest.spyOn(Document.prototype, 'cookie', 'set').mockImplementation(function (this: Document, value: string) {
      written.push(value);
    });
  });

  afterEach(() => jest.restoreAllMocks());

  it('writes Path, Max-Age, SameSite=Lax and Secure on every cookie, and encodes the value', () => {
    TestBed.inject(CookieJar).write('cyc-pkce', 'a.b./sub?x=1&y=2', { path: '/auth/callback', maxAgeSeconds: 600 });

    expect(written).toEqual([
      'cyc-pkce=a.b.%2Fsub%3Fx%3D1%26y%3D2; Path=/auth/callback; Max-Age=600; SameSite=Lax; Secure'
    ]);
  });

  it('removes at the path it was written with, by Max-Age=0', () => {
    TestBed.inject(CookieJar).remove('cyc-pkce', '/auth/callback');

    expect(written).toEqual(['cyc-pkce=; Path=/auth/callback; Max-Age=0; SameSite=Lax; Secure']);
  });

  it('reads one cookie by name out of the browser’s string, decoded', () => {
    jest.spyOn(Document.prototype, 'cookie', 'get').mockReturnValue('other=1; cyc-tenant=7f3c%2Fx; last=2');

    expect(TestBed.inject(CookieJar).read('cyc-tenant')).toBe('7f3c/x');
    expect(TestBed.inject(CookieJar).read('missing')).toBeNull();
  });

  it('on the server reads nothing and writes nothing — docs/plan/20 § SSR', () => {
    TestBed.configureTestingModule({
      providers: [
        { provide: PLATFORM_ID, useValue: 'server' },
        { provide: DOCUMENT, useValue: { cookie: 'cyc-tenant=leaked' } }
      ]
    });
    const jar = TestBed.inject(CookieJar);

    jar.write('cyc-tenant', 't', { path: '/', maxAgeSeconds: 1 });

    expect(jar.read('cyc-tenant')).toBeNull();
    expect(written).toEqual([]);
  });
});
