// The shell's public surface — docs/plan/03 § portal/: "libs/shell — navigation, breadcrumbs,
// resource blades, the omnibar".

export { AccessTokenStore } from './lib/auth/access-token-store';
export { AUTH_NAVIGATE, AuthFlow, PKCE_COOKIE, sameOriginPath } from './lib/auth/auth-flow';
export type { CallbackOutcome } from './lib/auth/auth-flow';
export { AuthSession, TENANT_COOKIE } from './lib/auth/auth-session';
export type { SignedInAccount, TokenResponse } from './lib/auth/auth-session';
export { authGuard } from './lib/auth/auth.guard';
export { CookieJar } from './lib/auth/cookies';
export type { CookieAttributes } from './lib/auth/cookies';
export { DEFAULT_IDENTITY_ISSUER, IDENTITY_ISSUER, IDENTITY_ISSUER_META } from './lib/auth/identity-issuer';
export { decodeJwtPayload, stringClaim } from './lib/auth/jwt-payload';
export { TENANT_CONTEXT_SOURCE } from './lib/auth/tenant-context-source';
export type { TenantContextSnapshot, TenantContextSource } from './lib/auth/tenant-context-source';
export { CALLBACK_PATH, PORTAL_CLIENT_ID, PORTAL_SCOPES } from './lib/auth/token-endpoint';
export { REFRESH_LEAD_MS, TokenRefresher } from './lib/auth/token-refresher';
export { BladeStackStore } from './lib/blades/blade-stack.store';
export type { BladeRef } from './lib/blades/blade-stack.store';
export { ShellBreadcrumbs } from './lib/breadcrumbs/shell-breadcrumbs';
export { ContextBar } from './lib/context-bar/context-bar';
export { TenantContextStore } from './lib/context/tenant-context';
export type { SubscriptionRef, TenantRef } from './lib/context/tenant-context';
export { EmptyBlade, ShellLayout } from './lib/layout/shell-layout';
export { NotificationsTray } from './lib/notifications/notifications-tray';
export { NotificationsStore } from './lib/notifications/notifications.store';
export type { OperationNotification, OperationStatus } from './lib/notifications/notifications.store';
export { OmnibarRegistry } from './lib/omnibar/omnibar-registry';
export type { OmnibarResult, OmnibarResultKind, OmnibarSource } from './lib/omnibar/omnibar-registry';
export { ShellOmnibar } from './lib/omnibar/shell-omnibar';
