using System.Collections.Generic;
using System.Linq;

namespace RTMPE.SDK.Conversion.Core
{
    /// <summary>
    /// The documented, reproducible authority rubric: a fixed first-match rule
    /// table over the intrinsic signals of one type. Pure and deterministic —
    /// the same signals always produce the same verdict, and connectivity never
    /// outweighs what the type itself declares (a high-fan-out MonoBehaviour
    /// with no network surface is an orchestrator, never an authority).
    ///
    /// Every recommendation is honest about the client-authoritative-with-relay
    /// runtime: NetworkVariable owner-writes and an RPC's declared caller are
    /// the surfaces the runtime enforces, a Server-targeted RPC executes only in
    /// the project's server function, and a broadcast mutation is never
    /// advised.
    /// </summary>
    public static class AuthorityClassifier
    {
        /// <summary>
        /// The Server-route honesty note (audit T2/T4): suggesting the seam
        /// always states that the seam is unfilled by default.
        /// </summary>
        public const string ServerRpcCostNote =
            "note: a Server-targeted Enhanced-RPC executes only in the project's server function "
            + "— an HTTPS endpoint its owner registers in the portal (Project → Server functions) — "
            + "and with none registered it resolves to RpcErrorUnknownMethod and the body runs on no node";

        /// <summary>
        /// The third way out for an unguarded mutator, named beside the two the
        /// advisory has always offered: a method whose body is MEANT to run on
        /// every receiver — a hit, a layout the sender decided — is neither
        /// guarded nor routed to a server; it is converted under the
        /// replica-apply designation, which the RPC generator checks by holding
        /// every call to it under an authority guard. Named here because the
        /// two older remedies both defeat that method, and this advisory is the
        /// one place such a method is pointed at at all: RTMPE2004 suggests the
        /// guarded shape alone, by decision.
        /// </summary>
        public const string ReplicaApplyPath =
            "or, for a method that applies state the sender decided, convert it as a "
            + "replica-apply Enhanced-RPC (the Conversion Wizard's Applies on receivers box; "
            + "make gen-rpc REPLICA_APPLY=1), which holds every call to it in its file under an "
            + "authority guard instead of guarding the body";

        /// <summary>
        /// The advisory for a networked type whose motion this reading can find no
        /// carrier for.  ⛔ Worded as the reading it is rather than as a fact about
        /// the object, because the two places a carrier can hide — a private member
        /// of a precompiled base, and a write into a component this type does not
        /// declare — are both outside what the rubric binds.
        /// </summary>
        private const string UnreplicatedMotionAdvice =
            "nothing this reading can see replicates the transform it moves — it reads this type "
            + "and the bases this project declares, so a private member of a precompiled base or "
            + "a write through another component is outside it; give the object a "
            + "NetworkTransform, replicate the position with a NetworkVariableVector3, or accept "
            + "the motion as local-only — and an object created at runtime must also be spawned "
            + "through SpawnManager.Spawn or it exists on no other client to move";

        // When that reading is worth printing: the type holds no carrier the
        // rubric can see.  A carrier is positional replicated state, replicated
        // state of any type that a transform write READS or that a transform
        // FEEDS, or an RPC surface.
        // 🔑 Not any replicated state: a score beside a mover carries nothing, and a
        // rule that took it as a carrier is silent at the ordinary next edit — the
        // one the posture advice itself asks for.  🔑 But a bool that turns a door
        // and a float that aims a turret do carry the motion, whatever their type,
        // and the reading stops for them on what the write reads, not on what the
        // member holds.
        // 🔑 An owner guard and a lifecycle override are not carriers either —
        // they decide WHO moves and WHEN, never whether the result leaves the
        // machine — so the reading survives them for the same reason.
        // ⛔ A declared motion replicator does end it, and it is the only statement
        // about the OBJECT the source carries.  ⚠️ It is a statement about what
        // Unity will attach from here on: the engine acts when a script is added
        // and when a requirement is removed, and audits no prefab that already
        // carries the script — so the suppression is right for everything built
        // after the declaration and optimistic for anything built before it.
        private static bool MotionHasNoVisibleCarrier(AuthoritySignals signals)
            => signals.WritesTransform
               && !signals.DeclaresMotionReplicator
               && signals.PositionalStateCount == 0
               && !signals.MovesFromReplicatedState
               && !signals.PublishesTransform
               && !signals.HasRpcSurface;

        /// <summary>
        /// The advisory for a type that creates, with <c>Instantiate</c>, an object
        /// the source proves carries a <c>NetworkBehaviour</c> — the classification
        /// half of RTMPE1030, which marks the call itself.
        /// </summary>
        public const string NetworkedInstantiationAdvice =
            "it creates an object carrying a NetworkBehaviour with Instantiate (RTMPE1030 marks the "
            + "call), which never puts it on the network: no other client sees it and none of its "
            + "variables replicate — spawn it with SpawnManager.Spawn from a registered prefab id";

        /// <summary>
        /// The advisory for a type that creates objects with <c>Instantiate</c>
        /// whose contents the source does not show, and spawns none — advice and
        /// never a role, because this is also exactly what a type that opens a UI
        /// panel looks like (decision S.9).
        /// </summary>
        public const string UnspawnedInstantiationAdvice =
            "it creates objects with Instantiate and spawns none through SpawnManager — an object other "
            + "players must see has to be spawned with SpawnManager.Spawn from a registered prefab; a UI "
            + "panel, an effect or anything else only this client needs is right as it is";

        // Whether a recommendation the verdict already carries tells the author
        // to spawn through SpawnManager: rule 0b's, rule 4b's, and the
        // unreplicated-motion reading's all do, and the instantiation advice
        // beside any of them would say it twice.
        private static bool AlreadySaysSpawn(string recommendation)
            => recommendation == NetworkedInstantiationAdvice
               || recommendation == UnreplicatedMoverAdvice
               || recommendation == UnreplicatedMotionAdvice;

        /// <summary>
        /// Rule 4b's advice: a type that moves a transform nothing replicates.
        /// It already tells the author to spawn the moving object through
        /// <c>SpawnManager.Spawn</c> rather than <c>Instantiate</c>, so a verdict
        /// carrying it is not given <see cref="UnspawnedInstantiationAdvice"/>
        /// as well — one instruction about spawning per verdict.
        /// </summary>
        public const string UnreplicatedMoverAdvice =
            "this type moves a transform and nothing replicates the movement — either put "
            + "the moving object on the network (a NetworkBehaviour carrying NetworkTransform, "
            + "spawned through SpawnManager.Spawn rather than Instantiate, so remote clients "
            + "see it move) or state that the motion is local-only; a mover is not "
            + "presentation, and this tool cannot tell the two apart";

        /// <summary>Applies the rubric to one type's intrinsic signals.</summary>
        public static AuthorityVerdict Classify(AuthoritySignals signals)
        {
            if (signals is null) throw new System.ArgumentNullException(nameof(signals));

            var verdict = ClassifyRole(signals, Describe(signals));

            // Advice on whatever the role is, never a role of its own: a prefab held
            // as a GameObject may be a Snake segment or a menu, and only its YAML
            // says which (plan §10/4, decision S.9).
            if (!signals.InstantiatesUnspawnedObjects || verdict.Recommendations.Any(AlreadySaysSpawn))
            {
                return verdict;
            }
            var recommendations = new List<string>(verdict.Recommendations) { UnspawnedInstantiationAdvice };
            return new AuthorityVerdict(verdict.Role, verdict.Evidence, recommendations);
        }

        private static AuthorityVerdict ClassifyRole(AuthoritySignals signals, IReadOnlyList<string> evidence)
        {

            // Rule 0 — the signals were taken over part of the chain. Every rule
            // below reads an ABSENCE as a fact ("no guard", "no wiring"), and an
            // absence over a base nobody opened is not one. Answered ahead of the
            // rubric so that a leaf whose readable half looks Authoritative is not
            // certified on a base whose unguarded mutator was never seen.
            if (signals.ReadPartially)
            {
                return new AuthorityVerdict(AuthorityRole.Undetermined, evidence, new[]
                {
                    "a base type on this chain is declared in a compilation this analysis does "
                    + "not carry, so its signals were read over part of the chain — reference "
                    + "that assembly as metadata, or classify it where its source is compiled",
                });
            }

            // Rule 0b — it creates an object carrying a NetworkBehaviour with
            // Instantiate, which never puts it on the network.  The one reading of
            // "creates objects and spawns none" that is certain — the compiler
            // bound the type that proves it — so it may decide the role where the
            // uncertain reading may only advise (decision S.9).  Ahead of the rules
            // below because what the type holds does not change what it creates:
            // an authoritative Gun that instantiates its Bullet prefab still leaves
            // every bullet on its own screen.
            if (signals.CreatesNetworkedObjectsLocally)
            {
                return new AuthorityVerdict(AuthorityRole.Undetermined, evidence, new[]
                {
                    NetworkedInstantiationAdvice,
                });
            }

            // Rule 1 — replicated state on a NetworkBehaviour: the owner-write
            // surface the runtime enforces at the client flush gate and the
            // gateway's object_authority_ok check.
            if (signals.InheritsNetworkBehaviour && signals.NetworkVariableCount > 0)
            {
                var recommendations = new List<string>();

                // Read per member, not per type. A guarded Update says nothing
                // about the public entry point beside it, and that pairing —
                // guarded loop, exposed damage or score method — is the ordinary
                // shape of a networked component, so a type-level test goes quiet
                // on precisely the population this advisory is written for.
                if (signals.UnguardedStateMutators.Count > 0)
                {
                    recommendations.Add(
                        string.Join(", ", signals.UnguardedStateMutators)
                        + " write(s) replicated state without an ownership check and can be called "
                        + "from outside the type — "
                        + "NetworkVariable owner-writes are runtime-enforced at flush and at the "
                        + "gateway, but the unguarded local write still runs on every client that "
                        + "calls it; add a leading `if (!IsOwner) return;` (RTMPE2003), route the "
                        + "mutation through a Server-targeted Enhanced-RPC (RTMPE2004) — "
                        + ServerRpcCostNote + " — " + ReplicaApplyPath);
                }

                // Replicated state the motion neither is nor reads is not a carrier
                // for it, so the reading applies here exactly as it does below.
                if (MotionHasNoVisibleCarrier(signals))
                {
                    recommendations.Add(UnreplicatedMotionAdvice);
                }

                return new AuthorityVerdict(AuthorityRole.Authoritative, evidence, recommendations);
            }

            // Rule 2 — owner-partitioned logic without replicated state of its own.
            if (signals.InheritsNetworkBehaviour
                && (signals.HasOwnerGuardedMember || signals.HasRpcSurface || signals.OverridesNetworkLifecycle))
            {
                return new AuthorityVerdict(
                    AuthorityRole.OwnerPartitioned,
                    evidence,
                    MotionHasNoVisibleCarrier(signals)
                        ? new[] { UnreplicatedMotionAdvice }
                        : System.Array.Empty<string>());
            }

            // Rule 3 — a bare NetworkBehaviour carries no discernible posture.
            if (signals.InheritsNetworkBehaviour)
            {
                var postureless = new List<string>(2)
                {
                    // ⚠️ Both conversion rules are named, because which one a
                    // reader will actually see depends on how their state is
                    // declared: RTMPE2002 fires on a field the author wrote,
                    // RTMPE2005 on an auto-property, whose state lives in a field
                    // the compiler wrote. Naming only the first sent an author
                    // looking for a lightbulb that cannot appear on their type.
                    "no discernible authority posture — add replicated NetworkVariable state "
                    + "(RTMPE2002, or RTMPE2005 where the state is an auto-property), a leading "
                    + "owner guard (RTMPE2003), or an Enhanced-RPC surface (RTMPE2004)",
                };

                // The posture sentence is insufficient for a type that moves: an
                // author who satisfies it in full still ships an object every
                // other client sees standing still.
                if (MotionHasNoVisibleCarrier(signals))
                {
                    postureless.Add(UnreplicatedMotionAdvice);
                }

                return new AuthorityVerdict(AuthorityRole.Undetermined, evidence, postureless);
            }

            // Rule 4 — networking signals on a non-NetworkBehaviour are incoherent:
            // the runtime discovers none of them outside a NetworkBehaviour subclass.
            if (signals.NetworkVariableCount > 0 || signals.HasOwnerGuardedMember || signals.HasRpcSurface)
            {
                return new AuthorityVerdict(AuthorityRole.Undetermined, evidence, new[]
                {
                    "networking signals on a non-NetworkBehaviour — the runtime discovers "
                    + "none of them here; rebase onto NetworkBehaviour (RTMPE2001)",
                });
            }

            // Rule 4b — it MOVES something, and nothing here replicates the
            // movement.  ⛔ Ahead of the wiring rule on purpose: a spawner that
            // also moves what it spawned is exactly the shape this rule exists
            // for, and unreplicated motion is the more consequential of the two
            // facts about it.
            //
            // 🔑 `Undetermined` rather than a verdict, because the answer is not
            // knowable from the source — a local-only mover is legitimate, and
            // so is one whose motion belongs on the wire.  What the author is
            // owed is the question and the two ways to settle it.
            //
            // ⛔ Read through the same predicate as the networked rules above, so
            // a type that declares its replicator is silent here for the reason it
            // is silent there.  Telling an author to put an object on the network
            // when the attribute above their class already does is the one piece
            // of advice that reads as a tool not having looked.
            if (MotionHasNoVisibleCarrier(signals))
            {
                return new AuthorityVerdict(AuthorityRole.Undetermined, evidence, new[]
                {
                    UnreplicatedMoverAdvice,
                });
            }

            // Rule 5 — scene-object wiring without any authority surface: the
            // connection/spawn bootstrap shape (the GameManager counterexample).
            if (signals.WiresSceneObjects)
            {
                return new AuthorityVerdict(AuthorityRole.Orchestrator, evidence, System.Array.Empty<string>());
            }

            // Rule 6 — a local-only leaf: presentation.
            return new AuthorityVerdict(AuthorityRole.Presentation, evidence, System.Array.Empty<string>());
        }

        // Stable, ordinal-stable evidence lines: the signals that are present,
        // in fixed declaration order, so two runs render identical bytes.
        private static IReadOnlyList<string> Describe(AuthoritySignals signals)
        {
            var evidence = new List<string>(10);
            evidence.Add(signals.InheritsNetworkBehaviour
                ? "inherits NetworkBehaviour"
                : "plain MonoBehaviour (not networked)");
            if (signals.NetworkVariableCount > 0)
            {
                evidence.Add(signals.NetworkVariableCount + " NetworkVariable member(s)");
            }
            if (signals.PositionalStateCount > 0)
            {
                evidence.Add(signals.PositionalStateCount + " positional NetworkVariable member(s)");
            }

            if (signals.ReadPartially) evidence.Add("a base on the chain could not be read");
            if (signals.HasOwnerGuardedMember) evidence.Add("owner-guarded member");
            if (signals.HasRpcSurface) evidence.Add("RPC surface");
            if (signals.OverridesNetworkLifecycle) evidence.Add("network lifecycle override");
            if (signals.WiresSceneObjects) evidence.Add("wires scene objects");

            // The motion signals, so a reading that was suppressed leaves the
            // reason it was suppressed beside it — a verdict two types share for
            // different reasons is otherwise indistinguishable on this line.
            if (signals.WritesTransform) evidence.Add("writes a transform");
            if (signals.MovesFromReplicatedState) evidence.Add("motion reads replicated state");
            if (signals.PublishesTransform) evidence.Add("motion is published to replicated state");
            if (signals.DeclaresMotionReplicator) evidence.Add("declares a motion replicator");
            if (signals.CreatesNetworkedObjectsLocally) evidence.Add("creates a networked object with Instantiate");
            if (signals.InstantiatesUnspawnedObjects) evidence.Add("creates objects with Instantiate and spawns none");
            return evidence;
        }
    }
}
