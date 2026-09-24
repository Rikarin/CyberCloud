import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { ApiCallError } from '../../app/api/http-transport';
import { links } from '../../app/routes/portal-links';

/**
 * The three identity pages' own navigation — docs/plan/20 § The pages that are not generated,
 * "Identity admin". A tab strip rather than a rail entry, because the portal has no rail yet and
 * the three pages are one area: the organisation's people, its registered applications, and the
 * signed-in person's own sessions.
 */
@Component({
  selector: 'cc-identity-nav',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, RouterLinkActive],
  template: `
    <nav class="border-border mt-3 flex gap-4 border-b text-sm" [attr.aria-label]="label">
      @for (tab of tabs; track tab.link) {
        <a
          class="text-foreground-muted -mb-px border-b-2 border-transparent px-1 pb-2"
          routerLinkActive="text-foreground border-primary font-medium"
          [ariaCurrentWhenActive]="'page'"
          [routerLink]="tab.link"
          >{{ tab.label }}</a
        >
      }
    </nav>
  `
})
export class IdentityNav {
  protected readonly label = $localize`:@@identity.nav:Identity`;
  protected readonly tabs = [
    { link: links.identityMembers(), label: $localize`:@@identity.nav.members:Members` },
    { link: links.identityApplications(), label: $localize`:@@identity.nav.applications:Applications` },
    { link: links.identitySessions(), label: $localize`:@@identity.nav.sessions:My sessions` }
  ];
}

/** An instant as the reader's own clock shows it, or the raw value if it doesn't parse. */
export function when(iso: string | undefined): string {
  if (iso === undefined || iso.length === 0) return '—';
  const at = new Date(iso);
  return Number.isNaN(at.getTime()) ? iso : at.toLocaleString();
}

/** The sentence a refused call shows — the platform's own message when it sent one. */
export function messageOf(error: unknown): string {
  return error instanceof ApiCallError ? error.error.message : error instanceof Error ? error.message : String(error);
}
