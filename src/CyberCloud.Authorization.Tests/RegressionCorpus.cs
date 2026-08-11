using CyberCloud.Authorization.Contracts;

namespace CyberCloud.Authorization.Tests;

/// <summary>One expected answer inside a <see cref="CorpusCase" />.</summary>
/// <param name="Object">The object, as <c>type:id</c>.</param>
/// <param name="Permission">The permission or relation name.</param>
/// <param name="Subject">The subject, as <c>type:id</c> or <c>type:id#relation</c>.</param>
/// <param name="Expected">What <c>Check</c> must answer.</param>
public sealed record CorpusExpectation(
    string Object,
    string Permission,
    string Subject,
    bool Expected
);

/// <summary>
///     One named authorization bug, as a tuple set and the answers it must produce.
/// </summary>
/// <param name="Name">
///     A stable, greppable name. Never renamed — a corpus entry is cited from incident notes.
/// </param>
/// <param name="Why">
///     What went wrong, in words, and what the wrong answer was. This field is the corpus; the rest
///     is scaffolding.
/// </param>
/// <param name="Tuples">The tuple set, in docs/plan/07 § The model's notation.</param>
/// <param name="Expectations">Every answer that must hold.</param>
public sealed record CorpusCase(
    string Name,
    string Why,
    IReadOnlyList<string> Tuples,
    IReadOnlyList<CorpusExpectation> Expectations
) {
    /// <summary>The tuples, parsed.</summary>
    public IReadOnlyList<RelationTuple> Parsed() => [.. Tuples.Select(x => RelationTuple.Parse(x).GetValueOrThrow())];

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
///     ⚠
///     <b>
///         THE REGRESSION CORPUS. docs/plan/07 § Testing: "every authorization bug ever found
///         becomes a named test with its tuple set checked in.
///         <b>
///             This corpus is the real asset; the
///             code is replaceable.
///         </b>
///         "
///     </b>
/// </summary>
/// <remarks>
///     <para>
///         <b>
///             How to add to it, in one paragraph, because a corpus nobody can add to stops
///             growing.
///         </b>
///         Add a <see cref="CorpusCase" /> to <see cref="Cases" /> with a name that will
///         still mean something in three years, a <c>Why</c> that says what the <i>wrong</i> answer
///         was, the tuple set in the <c>object#relation@subject</c> grammar, and the expectations.
///         Nothing else. <c>RegressionCorpusTests</c> runs every case twice: once against the
///         evaluator in memory, and once against the real grains on a real silo with real Redis and
///         real PostgreSQL. A case added here is therefore a test at both layers with no further
///         work.
///     </para>
///     <para>
///         <b>Every case runs against <see cref="CyberCloudSchema" />.</b> That is a deliberate
///         constraint rather than a limitation: a corpus entry that needed its own schema would be
///         a test of a schema nobody deploys, and the bugs worth keeping are the ones reachable from
///         the vocabulary the platform actually ships. A bug that genuinely needs another schema
///         belongs in <c>CheckEvaluatorTests</c>, where the schema can be written inline.
///     </para>
///     <para>
///         <b>What is in it today.</b> The seeds below are the defects and near-misses found while
///         building M1 — each one is a real thing that was wrong, or would have been wrong, in code
///         that existed at some point during this work. They are not illustrations.
///     </para>
/// </remarks>
public static class RegressionCorpus {
    /// <summary>Every case. Append-only in spirit: a case is fixed or explained, never deleted.</summary>
    public static IReadOnlyList<CorpusCase> Cases { get; } = [
        new(
            "inherited-owner-needs-no-tuple-at-the-resource",
            "docs/plan/07 § Azure RBAC row 3. A role assigned at the subscription must grant on "
            + "every resource under it with no tuple written per resource. A naive implementation "
            + "that materialised the grant downwards would pass a Check test and fail this one, "
            + "because the resource would have a tuple on it.",
            [
                "subscription:c1sub#owner@user:alice",
                "resourceGroup:c1rg#parent@subscription:c1sub",
                "resource:c1res#parent@resourceGroup:c1rg"
            ],
            [
                new("resource:c1res", "delete", "user:alice", true),
                new("resource:c1res", "write", "user:alice", true),
                new("resource:c1res", "read", "user:alice", true),
                new("resource:c1res", "delete", "user:mallory", false)
            ]
        ),

        new(
            "deny-assignment-removes-access-that-a-role-grants",
            "docs/plan/07 § Azure RBAC row 4 and § Caching across requests. ADDING the #suspended "
            + "tuple must REMOVE assignRole. The wrong answer is 'still allowed', which is what any "
            + "cache that assumes more tuples can only grant more will produce.",
            [
                "subscription:c2sub#owner@user:alice",
                "subscription:c2sub#suspended@user:alice"
            ],
            [
                new("subscription:c2sub", "assignRole", "user:alice", false),
                new("subscription:c2sub", "delete", "user:alice", true),
                new("subscription:c2sub", "read", "user:alice", true)
            ]
        ),

        new(
            "a-suspension-at-the-parent-does-not-inherit",
            "The observable consequence of confining negation to the same object. `suspended` is "
            + "direct-only, so suspending the parent does NOT suspend the child. Recorded here so "
            + "that it is a decision on the record rather than a surprise in an incident: anyone "
            + "who wants an inheriting deny is asking for a second invalidation mechanism, which "
            + "docs/plan/07 § Caching across requests refuses.",
            [
                "subscription:c3sub#owner@user:alice",
                "subscription:c3sub#suspended@user:alice",
                "resourceGroup:c3rg#parent@subscription:c3sub"
            ],
            [
                new("subscription:c3sub", "assignRole", "user:alice", false),
                new("resourceGroup:c3rg", "assignRole", "user:alice", true)
            ]
        ),

        new(
            "a-parent-cycle-terminates-and-denies",
            "docs/plan/07 § Check: cycles are broken by the memo. Two resource groups that are each "
            + "other's parent must not hang the check and must not grant. The wrong answers are a "
            + "stack overflow, a hang, and an allow.",
            [
                "resourceGroup:c4a#parent@resourceGroup:c4b",
                "resourceGroup:c4b#parent@resourceGroup:c4a"
            ],
            [
                new("resourceGroup:c4a", "read", "user:alice", false),
                new("resourceGroup:c4b", "read", "user:alice", false)
            ]
        ),

        new(
            "a-grant-inside-a-parent-cycle-is-still-found",
            "The other half of the cycle case, and the one a naive fix breaks: an implementation "
            + "that bails out on any cycle rather than returning 'false for this path' would deny "
            + "alice here, even though a real tuple grants her access on one of the two objects.",
            [
                "resourceGroup:c5a#parent@resourceGroup:c5b",
                "resourceGroup:c5b#parent@resourceGroup:c5a",
                "resourceGroup:c5b#owner@user:alice"
            ],
            [
                new("resourceGroup:c5a", "read", "user:alice", true),
                new("resourceGroup:c5b", "read", "user:alice", true),
                new("resourceGroup:c5a", "read", "user:mallory", false)
            ]
        ),

        new(
            "a-group-membership-cycle-terminates",
            "The same shape one level up. group:eng#member@group:ops#member and back — the userset "
            + "recursion of docs/plan/07 § Check step 3, closed into a loop. Nobody is a member, and "
            + "the walk must say so rather than run out of stack.",
            [
                "resourceGroup:c6rg#reader@group:c6eng#member",
                "group:c6eng#member@group:c6ops#member",
                "group:c6ops#member@group:c6eng#member"
            ],
            [
                new("resourceGroup:c6rg", "read", "user:alice", false)
            ]
        ),

        new(
            "nested-group-membership-resolves-without-an-index",
            "docs/plan/07 § The Leopard index is M2, and M1 has to be correct without it. Three "
            + "levels of group nesting must resolve by walking. The wrong answer is a deny, which "
            + "is what a one-level membership test produces.",
            [
                "resourceGroup:c7rg#reader@group:c7a#member",
                "group:c7a#member@group:c7b#member",
                "group:c7b#member@group:c7c#member",
                "group:c7c#member@user:carol"
            ],
            [
                new("resourceGroup:c7rg", "read", "user:carol", true),
                new("resourceGroup:c7rg", "read", "user:mallory", false)
            ]
        ),

        new(
            "a-userset-subject-is-not-its-object-half",
            "`group:eng` and `group:eng#member` are different subjects and must never collapse: one "
            + "is the group itself, the other is everyone in it. The wrong answer is that being "
            + "granted TO a group also grants to every member — or, in the other direction, that "
            + "the group object itself inherits its own members' access.",
            [
                "resourceGroup:c8rg#owner@group:c8eng",
                "group:c8eng#member@user:bob"
            ],
            [
                new("resourceGroup:c8rg", "read", "group:c8eng", true),
                new("resourceGroup:c8rg", "read", "user:bob", false)
            ]
        ),

        new(
            "a-diamond-does-not-double-count-or-lose-a-grant",
            "The shape docs/plan/07 § Check names as the reason the memo exists. Two resource "
            + "groups under one subscription, and a resource whose parent chain reaches it. The "
            + "memo must make the shared ancestor a single visit without turning the second visit "
            + "into a deny.",
            [
                "subscription:c9sub#owner@user:alice",
                "resourceGroup:c9left#parent@subscription:c9sub",
                "resourceGroup:c9right#parent@subscription:c9sub",
                "resource:c9res#parent@resourceGroup:c9left"
            ],
            [
                new("resource:c9res", "read", "user:alice", true),
                new("resourceGroup:c9right", "read", "user:alice", true)
            ]
        ),

        new(
            "role-hierarchy-is-one-directional",
            "owner implies contributor implies reader, and never the other way. The wrong answer is "
            + "a reader who can delete, which is the mistake a symmetric Rel(…) union produces.",
            [
                "resourceGroup:c10rg#reader@user:rachel",
                "resourceGroup:c10rg#owner@user:olivia"
            ],
            [
                new("resourceGroup:c10rg", "read", "user:rachel", true),
                new("resourceGroup:c10rg", "write", "user:rachel", false),
                new("resourceGroup:c10rg", "delete", "user:rachel", false),
                new("resourceGroup:c10rg", "read", "user:olivia", true),
                new("resourceGroup:c10rg", "write", "user:olivia", true),
                new("resourceGroup:c10rg", "delete", "user:olivia", true)
            ]
        ),

        new(
            "an-intersection-across-a-cycle-is-not-defeated-by-a-memoized-false",
            "⚠ FOUND BY CheckPropertyTests, on a generated graph, and not by any hand-written test. "
            + "An in-progress false returned to break a cycle is correct for the path it is on and "
            + "WRONG to write into the memo: the triple can become true later in the same request, "
            + "and an intersection that asks again gets the stale false. Expressed here against the "
            + "shipped schema: alice is a direct owner of the parent AND a direct reader of the "
            + "child, while the two are each other's ancestor through a cycle.",
            [
                "resourceGroup:c11a#parent@resourceGroup:c11b",
                "resourceGroup:c11b#parent@resourceGroup:c11a",
                "resourceGroup:c11b#owner@user:alice"
            ],
            [
                new("resourceGroup:c11a", "delete", "user:alice", true),
                new("resourceGroup:c11b", "delete", "user:alice", true),
                new("resourceGroup:c11a", "assignRole", "user:alice", true)
            ]
        ),

        new(
            "a-tuple-on-an-unknown-relation-is-inert-rather-than-a-wildcard",
            "A tuple naming a relation the schema does not compute must grant nothing. The wrong "
            + "answer is an allow — docs/plan/07 § The model's 'silent allow-everything', which it "
            + "calls the worse of the two failure modes.",
            [
                "resourceGroup:c12rg#suspended@user:alice"
            ],
            [
                new("resourceGroup:c12rg", "read", "user:alice", false),
                new("resourceGroup:c12rg", "delete", "user:alice", false),
                new("resourceGroup:c12rg", "assignRole", "user:alice", false)
            ]
        ),

        new(
            "a-group-granted-at-a-subscription-reaches-a-resource",
            "The two mechanisms composed: a userset subject AND inheritance, which is the shape "
            + "almost every real role assignment has. Neither feature is exercised by the other's "
            + "test, and the composition is where an implementation that resolves usersets only at "
            + "the queried object falls over.",
            [
                "subscription:c13sub#contributor@group:c13eng#member",
                "group:c13eng#member@user:bob",
                "resourceGroup:c13rg#parent@subscription:c13sub",
                "resource:c13res#parent@resourceGroup:c13rg"
            ],
            [
                new("resource:c13res", "write", "user:bob", true),
                new("resource:c13res", "delete", "user:bob", false)
            ]
        )
    ];
}
