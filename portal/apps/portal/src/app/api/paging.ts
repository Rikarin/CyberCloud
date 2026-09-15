/**
 * `$skipToken` out of a `nextLink`, or `null` when the link carries none.
 *
 * ⚠ The token is all that is taken from the link, never the link itself. `nextLink` is an absolute
 * URL the server chose, and a URL the server chose must not be put in front of the user's bearer
 * token — so the next page is fetched through the same verb on the portal's own origin, with the
 * token read out of the link's query string. Shared by every page that pages, so the rule is
 * written once.
 */
export function skipTokenOf(nextLink: string): string | null {
  try {
    return new URL(nextLink, 'https://placeholder.invalid').searchParams.get('$skipToken');
  } catch {
    return null;
  }
}
