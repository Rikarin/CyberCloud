/**
 * ⚠ **The layout gate: every class a rendered page uses must have a rule behind it.**
 *
 * This exists because of a defect that every gate in this repository passed. Both apps' stylesheets
 * were missing `@source '../../../node_modules/@xui'`, and Tailwind's automatic source detection
 * skips `node_modules`. So the utilities the apps' own templates ask for were compiled, and the
 * utilities only xUI's published templates ask for were not — 62 of the 115 classes on the portal's
 * rendered page had no rule. `flex-1` and `min-w-0` were among them, which is what
 * `@xui/dock-manager`'s panes fill their host with, so the workspace collapsed to two
 * content-sized boxes in the top-left corner of an otherwise empty 1280×720 page.
 *
 * ⚠ **Why the render-comparison gates could not see it.** `ssr-isolation.test.mjs` compares two
 * concurrent renders byte-for-byte, which is a real property and worth keeping — but two equally
 * collapsed layouts are byte-identical. Nothing in a suite that compares a render to another render
 * can notice that both are wrong in the same way. This compares the render to something else
 * entirely: the stylesheet that is supposed to serve it.
 *
 * ⚠ **Why this shape rather than a geometry assertion.** The obvious gate is "the main surface
 * fills a plausible fraction of the viewport", and it is the right instinct — but measuring it
 * needs a layout engine. jsdom has none (`clientWidth` is always 0 there), so the Jest suite cannot
 * express it, and a real one means adding a headless browser: a browser binary in the CI image, a
 * download step, and a class of flake that gets gates disabled rather than fixed. A screenshot diff
 * costs more again — a baseline image per viewport, per theme, regenerated and re-reviewed by a
 * human on every intentional design change, which is a standing tax on exactly the kind of change
 * this codebase makes most often.
 *
 * So this asserts the *cause* rather than the symptom, and gets something the geometry check would
 * not have given: no magic numbers at all. There is no pinned pixel count to go stale, no fraction
 * to re-tune when the layout changes, and no baseline to regenerate. The expected set is derived
 * from the render itself on every run, so it tracks the markup automatically — a component added
 * tomorrow is covered the day it is added, with nothing to update here.
 *
 * ⚠ **What it does not catch, stated plainly.** It proves every class *resolves*, not that the
 * result *fills the screen*. A genuine flex/min-height mistake written by hand would compile to
 * real CSS and pass while still collapsing. This closes the "markup references CSS that was never
 * emitted" hole, which is the one that actually happened and the one that is invisible by
 * construction — a missing class is not an error in HTML, in CSS, in Angular or in Tailwind. It is
 * not a general layout gate and should not be mistaken for one.
 */

import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Not a class at all, but a fragment of one.
 *
 * Arbitrary values may contain commas — `transition-[color,background-color,outline]` — and xUI
 * writes some of them with a space after the comma. Tailwind reads the whole bracket as a single
 * utility and emits a single rule; splitting the attribute on whitespace, as this check and a
 * browser both do, chops that utility into pieces that were never classes.
 *
 * ⚠ Only the *pieces* are excused, never a whole bracket utility. A fragment gives itself away by
 * unbalanced brackets or a trailing comma, so `transition-[color,`, `background-color,` and
 * `outline]` are skipped while `[&_svg]:size-3` — balanced, complete, and a real utility that needs
 * a real rule — is still checked. Excusing every token containing a bracket would have left
 * arbitrary-value utilities unguarded, which is most of what xUI's icon sizing uses.
 */
const isBracketFragment = (cls) => {
  const opens = (cls.match(/\[/g) ?? []).length;
  const closes = (cls.match(/\]/g) ?? []).length;

  return opens !== closes || cls.endsWith(',');
};

/**
 * Classes that correctly have no rule of their own.
 *
 * ⚠ Kept to markers and runtime plumbing on purpose. Every entry here is a hole in the check, so
 * the bar for adding one is that the class *cannot* have a rule — not that it happens not to.
 * Anything added because "that one is fine" would have been the line that let this defect back in.
 */
const isRuleless = (cls) =>
  // Tailwind's variant markers. `group` and `peer` exist to be referenced by `group-*:` and
  // `peer-*:` selectors on other elements; they emit nothing themselves, by design.
  cls === 'group' ||
  cls === 'peer' ||
  /^(group|peer)\/[A-Za-z0-9_-]+$/.test(cls) ||
  // Angular's runtime classes, written by the framework rather than by anyone's template.
  /^ng-/.test(cls) ||
  isBracketFragment(cls);

/**
 * Every class selector the emitted stylesheets define.
 *
 * ⚠ Read off the files on disk, not out of the rendered document. The production build inlines
 * *critical* CSS into a `<style>` element and defers the rest, so the document contains a subset by
 * design; checking the page against itself would call the deferred half missing.
 *
 * Tailwind escapes the characters that are illegal in a CSS identifier — `.w-0\.5`,
 * `.focus-visible\:outline-2`, `.\[\&_svg\]\:size-3` — so the escapes come back out to recover the
 * class as it is written in the markup.
 */
function definedClasses(browserDir) {
  const stylesheets = readdirSync(browserDir).filter((f) => f.endsWith('.css'));
  const defined = new Set();

  for (const sheet of stylesheets) {
    const css = readFileSync(join(browserDir, sheet), 'utf8');
    for (const m of css.matchAll(/\.((?:\\.|[A-Za-z0-9_-])+)/g)) {
      defined.add(m[1].replace(/\\(.)/g, '$1'));
    }
  }

  return { defined, stylesheetCount: stylesheets.length };
}

const ENTITIES = { amp: '&', lt: '<', gt: '>', quot: '"', '#39': "'" };

/**
 * Every class the rendered document actually puts on an element.
 *
 * `<script>` and `<style>` blocks come out first: the hydration state is JSON that can contain
 * anything, and the inlined critical CSS is full of selectors that are not markup.
 */
function usedClasses(html) {
  const markup = html
    .replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, '')
    .replace(/<style\b[^>]*>[\s\S]*?<\/style>/gi, '');

  const used = new Set();

  for (const attr of markup.matchAll(/\sclass="([^"]*)"/g)) {
    const value = attr[1].replace(/&(amp|lt|gt|quot|#39);/g, (_, e) => ENTITIES[e]);
    for (const token of value.split(/\s+/)) if (token) used.add(token);
  }

  return used;
}

/**
 * The classes on `html` that no stylesheet in `browserDir` defines, sorted.
 *
 * Throws rather than returning an empty list when there is nothing to compare — an empty result
 * must mean "checked and clean", never "found no stylesheet" or "found no markup", because both of
 * those would otherwise read as a pass.
 */
export function missingClassRules({ browserDir, html }) {
  const { defined, stylesheetCount } = definedClasses(browserDir);

  if (stylesheetCount === 0) {
    throw new Error(`the build emitted no stylesheet at all into ${browserDir}`);
  }

  const used = usedClasses(html);

  if (used.size === 0) {
    throw new Error('the rendered document carries no classes at all — is this the shell?');
  }

  return [...used].filter((cls) => !defined.has(cls) && !isRuleless(cls)).sort();
}

/** The failure message, kept here so both SSR suites report it identically. */
export function missingClassRulesMessage(missing, stylesheetPath) {
  return (
    `${missing.length} class(es) on the rendered page have no rule in the emitted stylesheet, so ` +
    `the browser drops them silently:\n    ${missing.join('\n    ')}\n\n  ` +
    `The usual cause is a Tailwind @source glob that does not cover the markup using them — ` +
    `see ${stylesheetPath}.`
  );
}
