using System.Collections.Generic;
using RTMPE.SDK.Conversion.Core;

namespace RTMPE.SDK.Analysis
{
    /// <summary>The six weighted dimensions of the Network Readiness Score (§7).</summary>
    public enum ReadinessDimension
    {
        /// <summary>The type inherits <c>RTMPE.Core.NetworkBehaviour</c> (weight 25).</summary>
        Structural,

        /// <summary>Replicated state is held in a <c>NetworkVariable</c> (weight 20).</summary>
        State,

        /// <summary>An <c>Update</c> loop, if present, opens with the owner guard (weight 20).</summary>
        Ownership,

        /// <summary>The type's <c>[RtmpeRpc]</c> methods are free of RTMPE1001–1006 (weight 15).</summary>
        Rpc,

        /// <summary>The type is free of RTMPE1011/1020/1021 lifecycle faults (weight 10).</summary>
        Lifecycle,

        /// <summary>The Phase-5 rubric assigns the type a discernible authority role (weight 10).</summary>
        Authority,
    }

    /// <summary>
    /// The fixed per-dimension weights; they sum to 100. Re-baselined for the
    /// Phase-5 <see cref="ReadinessDimension.Authority"/> dimension — every
    /// pinned score and golden artifact was re-pinned in the same change.
    /// </summary>
    public static class DimensionWeights
    {
        public const int Structural = 25;
        public const int State = 20;
        public const int Ownership = 20;
        public const int Rpc = 15;
        public const int Lifecycle = 10;
        public const int Authority = 10;

        /// <summary>The weight carried by <paramref name="dimension"/>.</summary>
        public static int For(ReadinessDimension dimension)
        {
            switch (dimension)
            {
                case ReadinessDimension.Structural: return Structural;
                case ReadinessDimension.State: return State;
                case ReadinessDimension.Ownership: return Ownership;
                case ReadinessDimension.Rpc: return Rpc;
                case ReadinessDimension.Lifecycle: return Lifecycle;
                case ReadinessDimension.Authority: return Authority;
                default: return 0;
            }
        }
    }

    /// <summary>One dimension's outcome for a single type, with a human-readable reason.</summary>
    public sealed class DimensionVerdict
    {
        public DimensionVerdict(ReadinessDimension dimension, bool cleared, string detail)
        {
            Dimension = dimension;
            Cleared = cleared;
            Detail = detail;
        }

        public ReadinessDimension Dimension { get; }

        /// <summary>The weight this dimension carries toward the 0–100 type score.</summary>
        public int Weight => DimensionWeights.For(Dimension);

        /// <summary>True when the dimension is satisfied and contributes its full weight.</summary>
        public bool Cleared { get; }

        /// <summary>The weight earned: the full weight when cleared, otherwise zero.</summary>
        public int EarnedWeight => Cleared ? Weight : 0;

        /// <summary>Why the dimension was cleared or not — surfaced in the report and to-do list.</summary>
        public string Detail { get; }
    }

    /// <summary>The per-type breakdown: every dimension verdict and the summed 0–100 score.</summary>
    public sealed class TypeReadiness
    {
        public TypeReadiness(string typeName, IReadOnlyList<DimensionVerdict> dimensions)
        {
            TypeName = typeName;
            Dimensions = dimensions;
        }

        /// <summary>The type's fully-qualified metadata name (the stable sort key).</summary>
        public string TypeName { get; }

        /// <summary>The six dimension verdicts, always in <see cref="ReadinessDimension"/> order.</summary>
        public IReadOnlyList<DimensionVerdict> Dimensions { get; }

        /// <summary>The 0–100 score: the sum of every cleared dimension's weight.</summary>
        public int Score
        {
            get
            {
                int total = 0;
                foreach (var verdict in Dimensions)
                {
                    total += verdict.EarnedWeight;
                }

                return total;
            }
        }
    }

    /// <summary>
    /// One entry of the report's advisory authority block: a graph node's role,
    /// the signals behind it, and any honest recommendations. The node set is
    /// the dependency graph's (every concrete component type, MonoBehaviour
    /// orchestrators and presentation leaves included) — a strict superset of
    /// the scored <see cref="ReadinessReport.Types"/> list, kept as its own
    /// block so the scored-type table is untouched.
    /// </summary>
    public sealed class AuthorityInsight
    {
        public AuthorityInsight(
            string typeName,
            AuthorityRole role,
            IReadOnlyList<string> evidence,
            IReadOnlyList<string> recommendations,
            IReadOnlyList<string> dependsOnAuthority,
            AuthorityDecidedBy? declared = null,
            string declaredNote = null)
        {
            TypeName = typeName;
            Role = role;
            Evidence = evidence;
            Recommendations = recommendations;
            DependsOnAuthority = dependsOnAuthority;
            Declared = declared;
            DeclaredNote = declaredNote ?? string.Empty;
        }

        /// <summary>The type's fully-qualified name (the stable sort key).</summary>
        public string TypeName { get; }

        /// <summary>The rubric's advisory role.</summary>
        public AuthorityRole Role { get; }

        /// <summary>The signals that produced the role.</summary>
        public IReadOnlyList<string> Evidence { get; }

        /// <summary>Advisory next steps, each honest about the bounded runtime.</summary>
        public IReadOnlyList<string> Recommendations { get; }

        /// <summary>Authority-bearing nodes this type observes or references.</summary>
        public IReadOnlyList<string> DependsOnAuthority { get; }

        /// <summary>
        /// The authority the project DECLARED for this type, when it answered the
        /// question the rubric could not; null when it did not.
        /// </summary>
        /// <remarks>
        /// 🔑 Beside <see cref="Role"/> rather than inside it. The role says what
        /// the code IS and stays <see cref="AuthorityRole.Undetermined"/> while
        /// the code says nothing; this says what the project INTENDS. Folding the
        /// answer into the role would put a verdict the rubric never reached
        /// under the rubric's own name, which is the one thing
        /// <c>AUTHORITY_INFERENCE.md</c> forbids it to do.
        /// </remarks>
        public AuthorityDecidedBy? Declared { get; }

        /// <summary>The developer's own words beside the answer; empty when none.</summary>
        public string DeclaredNote { get; }
    }

    /// <summary>The whole-project readiness report: per-type breakdown, score, and to-do list.</summary>
    public sealed class ReadinessReport
    {
        public ReadinessReport(
            int projectScore,
            IReadOnlyList<TypeReadiness> types,
            IReadOnlyList<string> todo,
            IReadOnlyList<AuthorityInsight> authority,
            IReadOnlyList<AuthorityQuestion> questions = null,
            IReadOnlyList<RuntimeCheck> runtime = null)
        {
            ProjectScore = projectScore;
            Types = types;
            Todo = todo;
            Authority = authority;
            Questions = questions ?? System.Array.Empty<AuthorityQuestion>();
            // Never null and always five: a caller that supplies nothing gets the
            // state an unrun project is in, said out loud, rather than an absent
            // section a surface has to invent a reading for.
            Runtime = RuntimeVerification.Apply(runtime);
        }

        /// <summary>The project metric: the mean of every scored type's 0–100 score, rounded.</summary>
        public int ProjectScore { get; }

        /// <summary>Every scored <c>NetworkBehaviour</c> type, ordered by name.</summary>
        public IReadOnlyList<TypeReadiness> Types { get; }

        /// <summary>The ranked, actionable list of every uncleared dimension across the project.</summary>
        public IReadOnlyList<string> Todo { get; }

        /// <summary>The Phase-5 advisory authority block, over the graph node set, ordered by name.</summary>
        public IReadOnlyList<AuthorityInsight> Authority { get; }

        /// <summary>
        /// The authority questions this project has yet to be asked — one per
        /// type the rubric left <see cref="AuthorityRole.Undetermined"/> for the
        /// one reason a developer can settle. Carried whether or not the question
        /// has been answered, so a surface can show the current answer beside the
        /// alternatives instead of hiding the choice once it is made.
        /// </summary>
        public IReadOnlyList<AuthorityQuestion> Questions { get; }

        /// <summary>
        /// The second result: the five runtime checks and what a run said about
        /// each. Always all five, in the vocabulary's own order.
        /// <para>
        /// ⛔ Not a component of <see cref="ProjectScore"/> and never will be.
        /// The score is a mean over source-decided dimensions; folding a run's
        /// outcome into it would make one number mean two things and leave a
        /// developer unable to tell which half moved.
        /// </para>
        /// </summary>
        public IReadOnlyList<RuntimeCheck> Runtime { get; }
    }
}
