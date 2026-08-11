import { TenantContextStore } from './tenant-context';

const tenants = [
  { id: 't-acme', displayName: 'Acme Corporation' },
  { id: 't-initech', displayName: 'Initech Holdings' },
];

const subscriptions = [
  { id: 's-acme-prod', tenantId: 't-acme', displayName: 'Acme Production' },
  { id: 's-acme-dev', tenantId: 't-acme', displayName: 'Acme Development' },
  { id: 's-initech-prod', tenantId: 't-initech', displayName: 'Initech Production' },
];

/**
 * docs/plan/20 § Information architecture on the context bar: "Getting this wrong means people act
 * in the wrong subscription, which is a real and expensive class of mistake."
 *
 * Each test below is one way that mistake happens.
 */
describe('TenantContextStore — docs/plan/20 § Information architecture', () => {
  let store: TenantContextStore;

  beforeEach(() => {
    store = new TenantContextStore();
    store.load(tenants, subscriptions);
  });

  it('offers only the active tenant’s subscriptions', () => {
    store.selectTenant('t-acme');

    expect(store.subscriptions().map((s) => s.id)).toEqual(['s-acme-prod', 's-acme-dev']);
    expect(store.subscriptions().some((s) => s.tenantId !== 't-acme')).toBe(false);
  });

  it('clears the subscription when the tenant changes', () => {
    store.selectTenant('t-acme');
    store.selectSubscription('s-acme-dev');
    expect(store.activeSubscription()?.id).toBe('s-acme-dev');

    store.selectTenant('t-initech');

    // Carrying a subscription across a tenant switch is how somebody ends up acting somewhere they
    // did not choose. Initech has exactly one subscription, so it is auto-selected — but it is
    // Initech's, not the one that was active a moment ago.
    expect(store.activeSubscription()?.id).toBe('s-initech-prod');
    expect(store.activeSubscription()?.tenantId).toBe('t-initech');
  });

  it('refuses a subscription belonging to another tenant', () => {
    store.selectTenant('t-acme');

    expect(() => store.selectSubscription('s-initech-prod')).toThrow(/does not belong/);

    // Acme has two subscriptions, so nothing was auto-selected and nothing has been selected
    // since. The rejected call must not have left the other tenant's subscription active.
    expect(store.activeSubscription()).toBeNull();

    // And it is still selectable within the active tenant — the guard rejects the crossing, not
    // the operation.
    store.selectSubscription('s-acme-dev');
    expect(store.activeSubscription()?.tenantId).toBe('t-acme');
  });

  it('auto-selects only when there is exactly one choice', () => {
    // One subscription: no ambiguity, so choosing for the user saves a click.
    store.selectTenant('t-initech');
    expect(store.activeSubscription()?.id).toBe('s-initech-prod');

    // Two: guessing would be picking a subscription on the user's behalf, which is the whole risk.
    store.selectTenant('t-acme');
    expect(store.activeSubscription()).toBeNull();
  });

  it('is unresolved until a tenant is known', () => {
    // The context bar renders a skeleton while this is false. An empty switcher reads as "you have
    // no subscriptions"; a skeleton reads as "not yet".
    expect(store.resolved()).toBe(false);
    store.selectTenant('t-acme');
    expect(store.resolved()).toBe(true);
  });
});
