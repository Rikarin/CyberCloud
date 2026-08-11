# Code Documentation Style

How we write XML doc comments and code comments in Cyber Cloud.

The rules come from the [Microsoft Writing Style Guide](https://learn.microsoft.com/en-us/style-guide/welcome/),
read firsthand — [Top 10 tips](https://learn.microsoft.com/en-us/style-guide/top-10-tips-style-voice),
[Developer content](https://learn.microsoft.com/en-us/style-guide/developer-content/),
[Reference documentation](https://learn.microsoft.com/en-us/style-guide/developer-content/reference-documentation),
[Formatting developer text elements](https://learn.microsoft.com/en-us/style-guide/developer-content/formatting-developer-text-elements),
[Verbs](https://learn.microsoft.com/en-us/style-guide/grammar/verbs) and
[Word choice](https://learn.microsoft.com/en-us/style-guide/word-choice/).

Where that guide and this file disagree, this file wins — it is the guide applied to *our* code, and
§ [Where we deviate](#where-we-deviate) says exactly where and why.

## The voice, in one line

**Warm and relaxed, crisp and clear, ready to lend a hand.** The guide is explicit that this applies
to developer content too: assume the reader understands programming, skip the basics, and spend the
words on what is specific to *this* code.

## The ten rules

Straight from the guide, with what each means for a doc comment.

| # | Rule | In a doc comment |
|---|---|---|
| 1 | **Bigger ideas, fewer words** | Shorter is always better. Cut every word that carries no information |
| 2 | **Write like you speak** | Read it aloud. If it sounds like a specification, rewrite it |
| 3 | **Project friendliness** | Contractions are fine — *it's*, *you'll*, *doesn't* |
| 4 | **Get to the point fast** | Lead with what the member *does*. The caveat comes second |
| 5 | **Be brief** | Enough to decide confidently, nothing more |
| 6 | **When in doubt, don't capitalize** | Sentence-style capitalization everywhere except code identifiers |
| 7 | **End punctuation in the right places** | Full sentences get periods. Fragments in `<param>` don't need one |
| 8 | **Remember the last comma** | Oxford comma: *tenants, subscriptions, and resources* |
| 9 | **Don't be spacey** | One space after a period. No spaces around an em dash—like this |
| 10 | **Revise weak writing** | Start with a verb. Cut *there is*, *there are*, and unnecessary *you can* |

## Tense, mood, and voice

- **Present tense.** "Returns the shard" — not *will return*.
- **Indicative mood** for descriptions; **imperative** only for instructions.
- **Active voice**, with two sanctioned exceptions the guide names: avoiding text that blames the
  reader, and emphasising the receiver of an action. `When the caller passes a stale etag, the write
  is rejected` is fine.

## XML doc comments

The guide's [reference-documentation](https://learn.microsoft.com/en-us/style-guide/developer-content/reference-documentation)
sections map onto XML tags like this.

### `<summary>` — what it does, without repeating its name

One or two sentences. Third-person present indicative, not second person and not imperative — the
guide's own example is *"Moves the entity represented by a `Record` to another location."*

Explain what the element does or represents **without restating the identifier**.

```csharp
// ✗ Restates the name and says nothing.
/// <summary>Gets the durable shard for a tenant.</summary>
public string DurableShardFor(TenantId tenant)

// ✓ Says what it means and what it guarantees.
/// <summary>
///     Returns the PostgreSQL shard that holds this tenant's durable state. Assignment is permanent,
///     so the same tenant always resolves to the same shard even after shards are added.
/// </summary>
public string DurableShardFor(TenantId tenant)
```

### `<param>` — never just the type or the name again

The guide is blunt: *"Don't just repeat the words in the parameter name or the data type."* Say what
a *valid* value looks like and what the parameter changes.

```csharp
// ✗
/// <param name="cancellationToken">The cancellation token.</param>

// ✓
/// <param name="canonicalPath">
///     The resource's canonical path. Pass <see cref="ResourceId.CanonicalPath" />, never
///     <see cref="ResourceId.Path" /> — the provider namespace is case-preserving, so two spellings
///     of one resource would claim two index entries.
/// </param>
```

### `<returns>` — describe the value, and name the condition for a `bool`

For a Boolean, describe the condition it reports rather than writing *true or false*.

```csharp
// ✗
/// <returns>True or false.</returns>

// ✓
/// <returns><c>true</c> if the name was free and is now claimed by this caller.</returns>
```

### `<exception>` — the condition, not the type restated

List each exception with *when* it happens.

```csharp
/// <exception cref="UnauthorizedAccessException">
///     The caller's tenant differs from the target's and no <c>ICrossTenantAuthorizer</c> allows the
///     crossing. The message names both tenants.
/// </exception>
```

### `<remarks>` — where the hard-won detail goes

The guide defines remarks as *"important details that may not be obvious from its syntax, parameters,
or return value"* — comparisons with similar elements, and potential issues in use. That is exactly
where this codebase's traps belong. Keep `<summary>` short and put the depth here.

## Formatting code elements

| Element | Convention | In XML docs |
|---|---|---|
| Types, methods, properties, fields, parameters, constants, keywords | Code style | `<see cref="..." />` when it resolves, `<c>...</c>` when it doesn't |
| A parameter of the current member | Code style | `<paramref name="..." />` |
| A type parameter | Code style | `<typeparamref name="..." />` |
| Literal values | Code style | `<c>true</c>`, `<c>null</c>`, `<c>"Null"</c>` |
| Capitalization of any code element | **Follow the code** | — |
| A new term you're about to define | Italic on first mention | `<em>` or plain prose |
| File name extensions | All lowercase | `.slnx`, `.editorconfig` |

Prefer `<see cref="..." />` over `<c>` wherever the symbol resolves: it survives renames and becomes a
link. Use `<c>` for things the compiler can't see — a JSON key, a shell flag, an environment variable.

## Word choice

- **Avoid jargon** and Latin abbreviations. Write *for example* not *e.g.*, *that is* not *i.e.*, and
  *and so on* not *etc.*
- **Same thing, same word.** Don't alternate between *tenant id* and *tenant identifier*.
- **US spelling.**
- Cut **please**, **simply**, **easy**, **just**, and **obviously**. If it were easy the reader
  wouldn't be reading the comment; saying so only stings when they're stuck.
- Cut **note that**, **it should be noted that**, and **in order to** — the last one is always *to*.

## Where we deviate

Three places, each deliberate.

**1. Summaries are third person, not second.** The guide's general advice is to write to *you*. Its
own reference-documentation examples are third-person descriptive, and .NET's conventions match.
Prose in `<remarks>` may address the reader directly; `<summary>` does not.

**2. We keep the ⚠ marker.** This codebase marks a non-obvious constraint with a ⚠ in comments and in
`docs/plan`. It is not in the style guide, and it stays: a reader scanning for the reason something
looks strange finds it in one pass. Use it for a real trap — something that has cost time, or would.
Don't decorate ordinary prose with it.

**3. Long comments are allowed, and sometimes required.** "Be brief" governs the *summary*. Several
findings in this repo — the cyclic-false memo rule, the `Orleans.Multitenant` encoding, the
`UseLocalhostClustering` split-brain — cost hours to establish and take a paragraph to state. Losing
them to brevity would be a bad trade. Put them in `<remarks>` or a block comment at the point of use,
and say **what breaks** if the rule is ignored, not just what the rule is.

The test: every sentence earns its place by telling the reader something they cannot get from the
signature. A long comment that passes that test is fine. A short one that fails it is not.

## Citing the plan

Cite `docs/plan` by **section**, never by line number:

```csharp
// ✓ docs/plan/05 § The two tiers          ✓ docs/plan/02 § ADR-003
// ✗ docs/plan/05:98                       ✗ docs/plan/02:212
```

Line numbers rot on the next doc edit, and a citation that has silently drifted to the wrong section
is worse than none. This has already happened twice here.

## What not to document

- **Restating the code.** `// increment i` earns nothing.
- **Implementation detail a caller can't act on**, in public docs. The guide says to review generated
  reference docs and strip internal detail.
- **Commented-out code.** Delete it; that's what version control is for.
- **Changelogs in comments.** The commit message is the changelog.

## Checking your work

Read the summary aloud. If it sounds like a person explaining the code to a colleague, it's right. If
it sounds like it was generated from the signature, it is — rewrite it.
