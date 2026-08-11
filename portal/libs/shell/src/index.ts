// The shell's public surface — docs/plan/03 § portal/: "libs/shell — navigation, breadcrumbs,
// resource blades, the omnibar".

export { AccessTokenStore } from './lib/auth/access-token-store';
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
