// The shell's test doubles — `@cybercloud/shell/testing`. Imported by suites only; nothing under
// `apps/` or `libs/` outside a `.spec.ts` may reach for this entry.

export { FakeTokenEndpoint, unsignedJwt } from './lib/auth/testing/fake-tokens';
export type { TokenAnswer, TokenCall } from './lib/auth/testing/fake-tokens';
export { MemoryCookieJar } from './lib/auth/testing/memory-cookie-jar';
export type { RecordedCookie } from './lib/auth/testing/memory-cookie-jar';
