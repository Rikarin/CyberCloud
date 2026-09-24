import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { DevicePageResponse } from '../identity-api';
import { NAVIGATE } from '../navigate';
import { DeviceCodePage } from './device-code';

/**
 * The device page (#43): enter the code, sign in, allow or deny — and render only what the server
 * resolved, never a name from the link.
 */
describe('the device page', () => {
  let fixture: ComponentFixture<DeviceCodePage>;
  let http: HttpTestingController;
  let navigated: string[];

  const waiting: DevicePageResponse = {
    found: true,
    userCode: 'BCDF-GHJK',
    clientName: 'cyc',
    scopes: ['openid', 'offline_access', 'cyc.api'],
    signedIn: true,
    account: 'person@contoso.example',
    status: 'pending',
    message: ''
  };

  const configure = (query: Record<string, string>) => {
    navigated = [];

    TestBed.configureTestingModule({
      imports: [DeviceCodePage],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: NAVIGATE, useValue: (url: string) => navigated.push(url) },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } }
      ]
    });

    fixture = TestBed.createComponent(DeviceCodePage);
    http = TestBed.inject(HttpTestingController);
  };

  afterEach(() => http.verify());

  it('looks the linked code up on its own, and sends a person who is not signed in to sign in', async () => {
    configure({ user_code: 'bcdf-ghjk' });
    fixture.detectChanges();
    await fixture.whenStable();

    const lookup = http.expectOne('/api/device/lookup');

    expect(lookup.request.body).toEqual({ userCode: 'bcdf-ghjk' });
    lookup.flush({ ...waiting, signedIn: false, account: '' });

    // ⚠ Back here afterwards with the code, so the person resumes rather than retypes.
    expect(navigated).toEqual([`/signin?returnUrl=${encodeURIComponent('/device-code?user_code=bcdf-ghjk')}`]);
  });

  it('asks a signed-in person with the registered name and their own account, then posts the answer', async () => {
    configure({ user_code: 'BCDF-GHJK', client: 'MARKER-phisher' });
    fixture.detectChanges();
    await fixture.whenStable();

    http.expectOne('/api/device/lookup').flush(waiting);
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;

    expect(text).toContain('cyc');
    expect(text).toContain('person@contoso.example');
    expect(text).toContain('Stay signed in without asking again');
    // Nothing from the link but the code is rendered.
    expect(text).not.toContain('MARKER');

    const allow = [...fixture.nativeElement.querySelectorAll('button')].find(
      (x: HTMLButtonElement) => x.textContent?.trim() === 'Allow'
    ) as HTMLButtonElement;

    allow.click();

    const decision = http.expectOne('/api/device/decision');

    expect(decision.request.body).toEqual({ userCode: 'BCDF-GHJK', decision: 'allow' });
    decision.flush({ ...waiting, status: 'approved', message: 'Done. Return to your device — it is signing in.' });
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Return to your device');
    expect(fixture.nativeElement.querySelectorAll('button').length).toBe(0);
  });

  it('renders the server sentence for a code that is not waiting, and asks nothing', async () => {
    configure({ user_code: 'ZZZZ-ZZZZ' });
    fixture.detectChanges();
    await fixture.whenStable();

    http.expectOne('/api/device/lookup').flush({
      found: false,
      userCode: '',
      clientName: '',
      scopes: [],
      signedIn: false,
      account: '',
      status: '',
      message: 'That code is not valid. Check it, or run the sign-in again on your device.'
    });
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('That code is not valid');
    expect(navigated).toEqual([]);
  });

  it('waits for a code when the link carried none', async () => {
    configure({});
    fixture.detectChanges();
    await fixture.whenStable();

    http.expectNone('/api/device/lookup');
    expect(fixture.nativeElement.querySelector('#cc-user-code')).not.toBeNull();
  });
});
