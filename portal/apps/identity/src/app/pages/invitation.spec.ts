import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { InvitationPageResponse } from '../identity-api';
import { NAVIGATE } from '../navigate';
import { InvitationPage } from './invitation';

/**
 * The invitation page (#43): describe the link, take a name and a password — or join with the
 * account the browser is signed into — and render what the host answered, never anything from the
 * link itself.
 */
describe('the invitation page', () => {
  let fixture: ComponentFixture<InvitationPage>;
  let http: HttpTestingController;
  let navigated: string[];

  const link = { tenant: 'a'.repeat(32), invitation: 'b'.repeat(32), token: 'MARKER-secret' };

  const pending: InvitationPageResponse = {
    found: true,
    email: 'colleague@contoso.example',
    tenantName: 'contoso',
    status: 'pending',
    succeeded: false,
    portalUrl: '',
    message: '',
    account: '',
    canJoinWithAccount: false
  };

  const configure = () => {
    navigated = [];
    TestBed.configureTestingModule({
      imports: [InvitationPage],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: NAVIGATE, useValue: (url: string) => navigated.push(url) },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(link) } } }
      ]
    });

    fixture = TestBed.createComponent(InvitationPage);
    http = TestBed.inject(HttpTestingController);
  };

  afterEach(() => http.verify());

  it('describes the link, then accepts it with the name and password in the body', async () => {
    configure();
    fixture.detectChanges();
    await fixture.whenStable();

    const describe = http.expectOne('/api/invitations/describe');

    expect(describe.request.body).toEqual(link);
    describe.flush(pending);
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;

    expect(text).toContain('contoso');
    expect(text).toContain('colleague@contoso.example');
    // The secret is posted back and never shown.
    expect(text).not.toContain('MARKER');

    fixture.componentInstance.displayName.set('Colleague');
    fixture.componentInstance.password.set('a-password');
    fixture.componentInstance.onAccept();

    // ⚠ Cleared as soon as it is sent.
    expect(fixture.componentInstance.password()).toBe('');

    const accept = http.expectOne('/api/invitations/accept');

    expect(accept.request.body).toEqual({ ...link, displayName: 'Colleague', password: 'a-password' });
    accept.flush({
      ...pending,
      status: 'accepted',
      succeeded: true,
      portalUrl: 'http://localhost:4200/',
      message: 'Welcome to contoso.'
    });
    fixture.detectChanges();

    const portal = fixture.nativeElement.querySelector('a') as HTMLAnchorElement;

    expect(portal.getAttribute('href')).toBe('http://localhost:4200/');
    expect(fixture.nativeElement.textContent).toContain('Welcome to contoso.');
    expect(fixture.nativeElement.querySelector('form')).toBeNull();
  });

  it('joins with the signed-in account when its address is the invited one, with no name or password', async () => {
    configure();
    fixture.detectChanges();
    await fixture.whenStable();

    http.expectOne('/api/invitations/describe').flush({
      ...pending,
      account: 'colleague@contoso.example',
      canJoinWithAccount: true
    });
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain("You're signed in as");

    const join = [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')].find(x =>
      x.textContent?.includes('Join with this account')
    );

    expect(join).toBeDefined();
    join!.click();

    const accept = http.expectOne('/api/invitations/accept');

    expect(accept.request.body).toEqual({ ...link, withSignedInAccount: true });
    accept.flush({
      ...pending,
      status: 'accepted',
      succeeded: true,
      portalUrl: 'http://localhost:4200/',
      message: 'Welcome.'
    });
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Welcome.');
    expect(navigated).toEqual([]);
  });

  it('sends a person with an account elsewhere to sign in, and back to this link', async () => {
    configure();
    fixture.detectChanges();
    await fixture.whenStable();

    http.expectOne('/api/invitations/describe').flush(pending);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).not.toContain('Join with this account');

    fixture.componentInstance.onSignInFirst();

    expect(navigated).toHaveLength(1);

    const [path, query] = navigated[0].split('?');
    const returnUrl = new URLSearchParams(query).get('returnUrl')!;

    expect(path).toBe('/signin');
    expect(returnUrl.startsWith('/invitation?')).toBe(true);
    expect(Object.fromEntries(new URLSearchParams(returnUrl.split('?')[1]))).toEqual(link);
  });

  it('renders the host sentence for a used link and offers no form', async () => {
    configure();
    fixture.detectChanges();
    await fixture.whenStable();

    http.expectOne('/api/invitations/describe').flush({
      ...pending,
      status: 'accepted',
      message: 'This invitation has already been used. Sign in instead.'
    });
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('already been used');
    expect(fixture.nativeElement.querySelector('form')).toBeNull();
  });

  it('never renders a portal link that is not http or https', async () => {
    configure();
    fixture.detectChanges();
    await fixture.whenStable();

    http.expectOne('/api/invitations/describe').flush(pending);
    fixture.componentInstance.onAccept();
    http.expectOne('/api/invitations/accept').flush({
      ...pending,
      succeeded: true,
      portalUrl: 'javascript:alert(1)',
      message: 'Welcome.'
    });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('a')).toBeNull();
  });
});
