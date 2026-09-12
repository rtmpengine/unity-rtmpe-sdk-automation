using System.Collections.Generic;

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
    /// runtime: NetworkVariable owner-writes are the one genuinely enforced
    /// surface, a Server-targeted RPC executes only in a backend handler the
    /// developer registers, and a broadcast mutation is never advised.
    /// </summary>
    public static class AuthorityClassifier
    {
        /// <summary>
        /// The Server-route honesty note (audit T2/T4): suggesting the seam
        /// always states that the seam is unfilled by default.
        /// </summary>
        public const string ServerRpcCostNote =
            "note: a Server-targeted Enhanced-RPC executes only in a backend handler "
            + "registered via RegisterServerRpc — with none registered it resolves to "
            + "RpcErrorUnknownMethod and the body runs on no node";

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

        // When that reading is worth printing.  A type with no replicated state and
        // no RPC surface holds no mechanism that could carry motion off the client.
        // 🔑 An owner guard and a lifecycle override are not carriers — they decide
        // WHO moves and WHEN, never whether the result leaves the machine — so the
        // reading survives them, which is what keeps it from disappearing the
        // moment an author acts on the advice one rule above.
        // ⛔ A declared motion replicator does end it, and it is the only statement
        // about the OBJECT the source carries.  ⚠️ It is a statement about what
        // Unity will attach from here on: the engine acts when a script is added
        // and when a requirement is removed, and audits no prefab that already
        // carries the script — so the suppression is right for everything built
        // after the declaration and optimistic for anything built before it.
        // ⚠️ The state clause is redundant while both call sites below sit under
        // the rule that claims a networked type carrying replicated state; it is
        // written so the predicate is true to its name rather than to its position.
        private static bool MotionHasNoVisibleCarrier(AuthoritySignals signals)
            => signals.WritesTransform
               && !signals.DeclaresMotionReplicator
               && signals.NetworkVariableCount == 0
               && !signals.HasRpcSurface;

        /// <summary>Applies the rubric to one type's intrinsic signals.</summary>
        public static AuthorityVerdict Classify(AuthoritySignals signals)
        {
            if (signals is null) throw new System.ArgumentNullException(nameof(signals));

            var evidence = Describe(signals);

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
                        + "calls it; add a leading `if (!IsOwner) return;` (RTMPE2003) or route the "
                        + "mutation through a Server-targeted Enhanced-RPC (RTMPE2004) — "
                        + ServerRpcCostNote);
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
                    "this type moves a transform and nothing replicates the movement — either put "
                    + "the moving object on the network (a NetworkBehaviour carrying NetworkTransform, "
                    + "spawned through SpawnManager.Spawn rather than Instantiate, so remote clients "
                    + "see it move) or state that the motion is local-only; a mover is not "
                    + "presentation, and this tool cannot tell the two apart",
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
            var evidence = new List<string>(6);
            evidence.Add(signals.InheritsNetworkBehaviour
                ? "inherits NetworkBehaviour"
                : "plain MonoBehaviour (not networked)");
            if (signals.NetworkVariableCount > 0)
            {
                evidence.Add(signals.NetworkVariableCount + " NetworkVariable member(s)");
            }

            if (signals.ReadPartially) evidence.Add("a base on the chain could not be read");
            if (signals.HasOwnerGuardedMember) evidence.Add("owner-guarded member");
            if (signals.HasRpcSurface) evidence.Add("RPC surface");
            if (signals.OverridesNetworkLifecycle) evidence.Add("network lifecycle override");
            if (signals.WiresSceneObjects) evidence.Add("wires scene objects");
            return evidence;
        }
    }
}
