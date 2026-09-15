import { guidClaimAsAddress } from './jwt-payload';

/**
 * The token writes GUIDs one way and the address reads them another — `AccessTokenClaims`' `N`
 * form against docs/plan/06 § Identifiers' `D` form. The first dev run put the claim straight
 * into `/api/tenants/{tid}` and the gateway answered 400 `InvalidResourceId`.
 */
describe('guidClaimAsAddress — the N-form claim as the D-form address', () => {
  it('hyphenatesA32DigitClaimAndLowersItsCase', () => {
    expect(guidClaimAsAddress('76FE0B2BE6D44FA29FB1F269C9E654CF')).toBe('76fe0b2b-e6d4-4fa2-9fb1-f269c9e654cf');
  });

  it('leavesAnythingElseAsItCame', () => {
    expect(guidClaimAsAddress('76fe0b2b-e6d4-4fa2-9fb1-f269c9e654cf')).toBe('76fe0b2b-e6d4-4fa2-9fb1-f269c9e654cf');
    expect(guidClaimAsAddress('u-1')).toBe('u-1');
    expect(guidClaimAsAddress('')).toBe('');
  });
});
