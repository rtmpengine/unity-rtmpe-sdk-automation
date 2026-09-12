using System;
using System.Collections.Generic;

namespace RTMPE.SDK.Conversion.Core
{
    /// <summary>
    /// The authority role a project type plays, as inferred from its static
    /// signals. Advisory only: a role never rewrites source, transfers
    /// ownership, or changes what the runtime enforces.
    /// </summary>
    public enum AuthorityRole
    {
        /// <summary>
        /// A <c>NetworkBehaviour</c> holding replicated <c>NetworkVariable</c>
        /// state — the owner-write surface the runtime genuinely enforces
        /// (client flush gate + gateway <c>object_authority_ok</c>).
        /// </summary>
        Authoritative,

        /// <summary>
        /// A <c>NetworkBehaviour</c> whose logic is partitioned by ownership
        /// (owner guards, an RPC surface, or network lifecycle hooks) without
        /// replicated state of its own.
        /// </summary>
        OwnerPartitioned,

        /// <summary>
        /// A plain <c>MonoBehaviour</c> leaf with no authority surface and no
        /// scene-object wiring — HUD, status display, local-only presentation.
        /// </summary>
        Presentation,

        /// <summary>
        /// A plain <c>MonoBehaviour</c> that wires scene objects together
        /// (assigns component references, instantiates, looks components up)
        /// — connection/spawn bootstrap, not an authority.
        /// </summary>
        Orchestrator,

        /// <summary>
        /// The explicit fallback when the signals conflict or are absent —
        /// a classifier that cannot say "I don't know" would guess.
        /// </summary>
        Undetermined,
    }

    /// <summary>
    /// The deterministic, source-observable signals the classifier consumes.
    /// Extraction (which touches Roslyn symbols) lives in the analyzer layer;
    /// this record is plain data so the rubric itself stays dependency-free.
    /// </summary>
    public sealed class AuthoritySignals
    {
        public AuthoritySignals(
            bool inheritsNetworkBehaviour,
            int networkVariableCount,
            bool hasOwnerGuardedMember,
            bool hasRpcSurface,
            bool overridesNetworkLifecycle,
            bool wiresSceneObjects,
            IReadOnlyList<string> unguardedStateMutators = null,
            bool readPartially = false,
            bool writesTransform = false,
            bool declaresMotionReplicator = false)
        {
            if (networkVariableCount < 0) throw new ArgumentOutOfRangeException(nameof(networkVariableCount));

            ReadPartially = readPartially;
            WritesTransform = writesTransform;
            DeclaresMotionReplicator = declaresMotionReplicator;
            InheritsNetworkBehaviour = inheritsNetworkBehaviour;
            NetworkVariableCount = networkVariableCount;
            HasOwnerGuardedMember = hasOwnerGuardedMember;
            HasRpcSurface = hasRpcSurface;
            OverridesNetworkLifecycle = overridesNetworkLifecycle;
            WiresSceneObjects = wiresSceneObjects;
            UnguardedStateMutators = unguardedStateMutators ?? Array.Empty<string>();
        }

        /// <summary>
        /// A declaration on the type's own chain sits in a tree the extracting
        /// compilation does not carry, so the syntax-read signals below were
        /// taken over part of the chain. The symbol-read ones are whole.
        /// </summary>
        /// <remarks>
        /// ⛔ Carried rather than folded into the other signals, for the reason
        /// <see cref="AuthorityRole.Undetermined"/> exists: a base the reader could
        /// not open answers "no guard, no wiring, no mutator" in the same words a
        /// base with none would, and a rubric handed those words would classify
        /// on them. It is the reader's job to say it did not look.
        /// </remarks>
        public bool ReadPartially { get; }

        /// <summary>
        /// The type is a motion replicator, or requires one through
        /// <c>[RequireComponent]</c> — <c>NetworkTransform</c>,
        /// <c>NetworkRigidbody</c> or <c>NetworkRigidbody2D</c>, on its own chain.
        /// </summary>
        /// <remarks>
        /// ⚠️ It answers about the DECLARATION, never about an object. Unity acts
        /// on <c>[RequireComponent]</c> when a script is attached and when someone
        /// tries to remove the requirement — it audits no existing prefab — so a
        /// prefab that carried the script before the attribute was written has no
        /// replicator and will not gain one. The declaration settles the question
        /// for everything attached after it and for nothing attached before.
        /// ⛔ The type names are read as names, so a project type called
        /// <c>NetworkTransform</c> answers true here. That direction is SILENCE,
        /// which is the quiet failure rather than the safe one; it is accepted
        /// because the alternative — a semantic read — answers false for every
        /// headless score, where the SDK's own assembly is not referenced.
        /// </remarks>
        public bool DeclaresMotionReplicator { get; }

        /// <summary>The type derives from <c>RTMPE.Core.NetworkBehaviour</c>.</summary>
        public bool InheritsNetworkBehaviour { get; }

        /// <summary>Members typed as <c>NetworkVariableBase</c> subclasses, own chain.</summary>
        public int NetworkVariableCount { get; }

        /// <summary>A declared method opens with the canonical owner guard.</summary>
        public bool HasOwnerGuardedMember { get; }

        /// <summary>
        /// Methods a caller outside the type can reach that write replicated
        /// state without passing an ownership check, named so the advisory points
        /// at something rather than describing a possibility. Distinct from
        /// <see cref="HasOwnerGuardedMember"/>, which answers a question about
        /// the type: one guarded method says nothing about whether the mutator
        /// beside it is guarded, and on the ordinary networked component — a
        /// guarded Update plus a public damage or score entry point — the two
        /// answers disagree.
        /// </summary>
        public IReadOnlyList<string> UnguardedStateMutators { get; }

        /// <summary>
        /// The type touches the RPC surface — declares <c>[RtmpeRpc]</c>, or
        /// invokes <c>SendRpc</c>/<c>SendEnhancedRpc</c>/<c>RPC</c>, or
        /// references <c>RpcMethodId</c> (both the legacy and enhanced shapes).
        /// </summary>
        public bool HasRpcSurface { get; }

        /// <summary>Overrides a network lifecycle hook (<c>OnNetworkSpawn</c> etc.).</summary>
        public bool OverridesNetworkLifecycle { get; }

        /// <summary>
        /// The type wires scene objects: it assigns a component-typed member,
        /// holds a <c>GameObject</c> member, or acquires components in code
        /// (<c>GetComponent</c>/<c>AddComponent</c>/<c>Instantiate</c>…).
        /// Inspector-injected references that are only read or subscribed to
        /// are observation, not wiring.
        /// </summary>
        public bool WiresSceneObjects { get; }

        /// <summary>
        /// Whether the type writes a transform — a position, a rotation, a
        /// scale, or one of the helpers that moves one.
        /// </summary>
        /// <remarks>
        /// 🔴 The classification had no way to ask this, so a plain
        /// <c>MonoBehaviour</c> carrying the whole of a game's visible motion
        /// was filed as <c>Presentation</c> — "HUD, status display, local-only
        /// presentation" — and the project scored well with nothing replicated.
        /// Measured on an integrator's Snake build, where the movement lives on
        /// runtime-instantiated Head/Body segments rather than on the networked
        /// root.  ⛔ This signal answers WHETHER the object moves, never whether
        /// it should replicate: local-only motion is a legitimate decision, so
        /// what it buys is a question rather than a verdict.
        /// </remarks>
        public bool WritesTransform { get; }
    }

    /// <summary>The classifier's advisory outcome for one type.</summary>
    public sealed class AuthorityVerdict
    {
        public AuthorityVerdict(AuthorityRole role, IReadOnlyList<string> evidence, IReadOnlyList<string> recommendations)
        {
            Role = role;
            Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
            Recommendations = recommendations ?? throw new ArgumentNullException(nameof(recommendations));
        }

        public AuthorityRole Role { get; }

        /// <summary>The signals that produced the role, as stable human-readable lines.</summary>
        public IReadOnlyList<string> Evidence { get; }

        /// <summary>
        /// Advisory next steps, each honest about the bounded runtime: a
        /// Server-RPC route always states its unfilled backend cost, and a
        /// broadcast mutation is never advised.
        /// </summary>
        public IReadOnlyList<string> Recommendations { get; }
    }

    /// <summary>
    /// Who decides — the vocabulary an authority question is answered in.
    /// <para>
    /// Deliberately the words a game author already uses rather than the
    /// rubric's: the question exists because the code says nothing, so the only
    /// person who can answer it is the one holding the design, and they hold it
    /// in these terms. <see cref="AuthorityRole"/> describes what the code IS
    /// and is never written from an answer.
    /// </para>
    /// </summary>
    public enum AuthorityDecidedBy
    {
        /// <summary>The client that owns the object decides; the others are shown the result.</summary>
        Owner,

        /// <summary>One client — whoever is the room's host — decides for everybody.</summary>
        Host,

        /// <summary>The server decides; a client asks and waits.</summary>
        Server,

        /// <summary>Every client decides for itself and nothing has to agree.</summary>
        EachClient,
    }

    /// <summary>
    /// One answer a developer may give, with what choosing it costs here.
    /// <para>
    /// The options travel in the artifact rather than being restated by each
    /// surface that renders them: the editor window cannot reference this
    /// assembly, and a second copy of this vocabulary is a second statement of
    /// one fact — the failure this toolchain has closed repeatedly.
    /// </para>
    /// </summary>
    public sealed class AuthorityAnswerOption
    {
        public AuthorityAnswerOption(
            AuthorityDecidedBy decidedBy,
            string id,
            string label,
            string consequence,
            bool needsServerImplementation)
        {
            DecidedBy = decidedBy;
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Label = label ?? throw new ArgumentNullException(nameof(label));
            Consequence = consequence ?? throw new ArgumentNullException(nameof(consequence));
            NeedsServerImplementation = needsServerImplementation;
        }

        public AuthorityDecidedBy DecidedBy { get; }

        /// <summary>The stable token written into the project's answers file.</summary>
        public string Id { get; }

        /// <summary>What the developer reads when choosing.</summary>
        public string Label { get; }

        /// <summary>What choosing it means in this runtime, stated honestly.</summary>
        public string Consequence { get; }

        /// <summary>
        /// The answer cannot be delivered by Unity code alone: it needs a handler
        /// on the server. True for exactly one option, and the report says so
        /// beside the choice rather than after it has been made.
        /// </summary>
        public bool NeedsServerImplementation { get; }
    }

    /// <summary>
    /// One question the toolchain asks about one type, phrased from the
    /// project's own vocabulary.
    /// </summary>
    /// <remarks>
    /// ⛔ A question is not a verdict. It is asked only where the rubric returned
    /// <see cref="AuthorityRole.Undetermined"/> for the one reason a developer
    /// can settle — a networked type that declares no posture — and never where
    /// the reader failed to open the chain or the code contradicts the intent.
    /// </remarks>
    public sealed class AuthorityQuestion
    {
        public AuthorityQuestion(
            string typeName, string subject, string prompt, IReadOnlyList<AuthorityAnswerOption> options)
        {
            TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
            Subject = subject ?? throw new ArgumentNullException(nameof(subject));
            Prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
            Options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>The fully-qualified name of the type being asked about.</summary>
        public string TypeName { get; }

        /// <summary>
        /// The concern in the project's words, read from the type's own name.
        /// ⚠️ A phrasing aid and nothing else — it never reaches a verdict, and
        /// where the name says nothing recognisable it falls back to a neutral
        /// wording rather than inventing a subject.
        /// </summary>
        public string Subject { get; }

        /// <summary>The question as a surface should render it, verbatim.</summary>
        public string Prompt { get; }

        /// <summary>The answers offered, in fixed order.</summary>
        public IReadOnlyList<AuthorityAnswerOption> Options { get; }
    }

    /// <summary>One answer a project has recorded, as read from its answers file.</summary>
    public sealed class AuthorityAnswer
    {
        public AuthorityAnswer(string typeName, AuthorityDecidedBy decidedBy, string note = null)
        {
            TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
            DecidedBy = decidedBy;
            Note = note ?? string.Empty;
        }

        /// <summary>The fully-qualified type name the answer is about.</summary>
        public string TypeName { get; }

        public AuthorityDecidedBy DecidedBy { get; }

        /// <summary>The developer's own words beside the choice; empty when none.</summary>
        public string Note { get; }
    }

    /// <summary>How one project type refers to another in the dependency graph.</summary>
    public enum AuthorityEdgeKind
    {
        /// <summary>A <c>[RequireComponent(typeof(T))]</c> declaration.</summary>
        RequireComponent,

        /// <summary>A field or property typed as the target.</summary>
        Member,

        /// <summary>A <c>GetComponent&lt;T&gt;</c>-family or <c>AddComponent&lt;T&gt;</c> lookup.</summary>
        ComponentLookup,

        /// <summary>A read or event subscription on a target-typed expression.</summary>
        Observe,

        /// <summary>A method invocation on a target-typed expression.</summary>
        Invoke,

        /// <summary>A write to a plain (non-replicated) member of a target-typed expression.</summary>
        Write,

        /// <summary>
        /// A write to a target's replicated NetworkVariable value —
        /// <c>other.Variable.Value = x</c> — the one cross-object write that
        /// reaches another owner's networked state.
        /// </summary>
        WriteReplicated,
    }

    /// <summary>One directed code-reference edge between two project types.</summary>
    public sealed class AuthorityEdge
    {
        public AuthorityEdge(string from, string to, AuthorityEdgeKind kind)
        {
            From = from ?? throw new ArgumentNullException(nameof(from));
            To = to ?? throw new ArgumentNullException(nameof(to));
            Kind = kind;
        }

        /// <summary>The referring type's fully-qualified name.</summary>
        public string From { get; }

        /// <summary>The referred-to type's fully-qualified name.</summary>
        public string To { get; }

        public AuthorityEdgeKind Kind { get; }
    }
}
