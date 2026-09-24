import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { InvitationPageResponse } from '../identity-api';
import { InvitationPage } from './invitation';

/**
 * The invitation page (#43): describe the link, take a name and a password, and render what the
 * host answered — never anything from the link itself.
 */
describe('the invitation page', () => {
  let fixture: ComponentFixture<InvitationPage>;
  let http: HttpTestingController;

  const link = { tenant: 'a'.repeat(32), invitation: 'b'.repeat(32), token: 'MARKER-secret' };

  const pending: InvitationPageResponse = {
    found: true,
    email: 'colleague@contoso.example',
    tenantName: 'contoso',
    status: 'pending',
    succeeded: false,
    portalUrl: '',
    message: ''
  };

  const configure = () => {
    TestBed.configureTestingModule({
      imports: [InvitationPage],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
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
