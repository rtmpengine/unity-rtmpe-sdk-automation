using System;
using System.Collections.Generic;

namespace RTMPE.SDK.Conversion.Core
{
    /// <summary>
    /// The second result: what a run of the converted game established, beside
    /// the static readiness score rather than inside it.
    /// <para>
    /// The score is a weighted sum over six source-decided dimensions, averaged
    /// over the scored types. Every one of those dimensions is a claim about
    /// code as written, so a project can reach 100% having never opened a
    /// socket. These five checks are the other half, and nothing in this
    /// assembly can clear one: an outcome arrives from a run or it stays
    /// <see cref="RuntimeCheckOutcome.NotTested"/>.
    /// </para>
    /// </summary>
    public static class RuntimeVerification
    {
        /// <summary>
        /// The file a project records its runtime outcomes in, beside the
        /// readiness artifact.
        /// <para>
        /// 🔑 Its own file, and an INPUT to the scan — the same reasoning that
        /// puts the authority answers in
        /// <see cref="AuthorityQuestionnaire.AnswerFileName"/>. The artifact is
        /// rewritten whole on every run, so an outcome stored there survives
        /// exactly until the next scan. The artifact carries the outcomes back
        /// out for the editor to render; this file is where they live.
        /// </para>
        /// </summary>
        public const string RecordFileName = "network-runtime-checks.json";

        /// <summary>
        /// The sentence that keeps the two results apart, carried in the
        /// artifact because the surfaces that render it — the editor window, the
        /// published Markdown — cannot reference this assembly.
        /// </summary>
        public const string SeparationNote =
            "the static score is decided from source and says nothing about a run; these five are "
            + "decided by a run and say nothing about the source. Neither number moves the other";

        /// <summary>
        /// What an outcome must carry to be an outcome rather than an assertion,
        /// quoted by the reader that refuses a record without it.
        /// </summary>
        public const string EvidenceNote =
            "a recorded outcome names who observed it, when, and the SDK version it was observed "
            + "against; without all three it is a claim with nothing behind it and is refused";

        private static readonly RuntimeCheckDefinition ConnectionCheck = new RuntimeCheckDefinition(
            RuntimeCheckId.Connection,
            "connection",
            "The client reaches the gateway",
            "a handshake completed against a deployed gateway: the API key was accepted, the "
            + "ephemeral key exchange finished, and a session exists");

        private static readonly RuntimeCheckDefinition SharedRoomCheck = new RuntimeCheckDefinition(
            RuntimeCheckId.SharedRoom,
            "shared-room",
            "Two clients are in one room",
            "both clients joined the same room and each was given a roster naming the other, with "
            + "exactly one of them marked host");

        private static readonly RuntimeCheckDefinition BothPlayersCheck = new RuntimeCheckDefinition(
            RuntimeCheckId.BothPlayers,
            "both-players",
            "Each player appears for the other",
            "each client's spawn reached the other and was recorded under its own owner — the "
            + "difference between being in a room and being visible in it");

        private static readonly RuntimeCheckDefinition SyncCheck = new RuntimeCheckDefinition(
            RuntimeCheckId.Sync,
            "sync",
            "State crosses between them",
            "a replicated value written by its owner arrived at the other client unchanged, and a "
            + "write to somebody else's object did not");

        private static readonly RuntimeCheckDefinition ReconnectCheck = new RuntimeCheckDefinition(
            RuntimeCheckId.Reconnect,
            "reconnect",
            "A dropped client comes back",
            "a session that ended was resumed with its reconnect token rather than by starting "
            + "over, and the room was still there to come back to");

        /// <summary>
        /// The five checks, in the order a first run meets them. Fixed and
        /// closed: a record naming anything else is refused rather than
        /// silently added, because a check nobody defined is a green row nobody
        /// can trace to a run.
        /// </summary>
        public static readonly IReadOnlyList<RuntimeCheckDefinition> Checks =
            new[] { ConnectionCheck, SharedRoomCheck, BothPlayersCheck, SyncCheck, ReconnectCheck };

        /// <summary>The definition of <paramref name="id"/>.</summary>
        public static RuntimeCheckDefinition DefinitionOf(RuntimeCheckId id)
        {
            foreach (var check in Checks)
            {
                if (check.Check == id) return check;
            }

            throw new ArgumentOutOfRangeException(nameof(id), id, "no such runtime check");
        }

        /// <summary>The wire id of <paramref name="id"/>.</summary>
        public static string IdOf(RuntimeCheckId id) => DefinitionOf(id).Id;

        /// <summary>
        /// Resolves a recorded check id. Ordinal and exact: the ids are a closed
        /// vocabulary written by a tool, so a near miss is a mistake to report
        /// rather than a spelling to absorb.
        /// </summary>
        public static bool TryParseId(string id, out RuntimeCheckId parsed)
        {
            foreach (var check in Checks)
            {
                if (string.Equals(check.Id, id, StringComparison.Ordinal))
                {
                    parsed = check.Check;
                    return true;
                }
            }

            parsed = default;
            return false;
        }

        /// <summary>Every check id, in order — for a message naming the alternatives.</summary>
        public static IReadOnlyList<string> CheckIds()
        {
            var ids = new List<string>(Checks.Count);
            foreach (var check in Checks) ids.Add(check.Id);
            return ids;
        }

        /// <summary>The wire id of <paramref name="outcome"/>.</summary>
        public static string IdOf(RuntimeCheckOutcome outcome)
        {
            switch (outcome)
            {
                case RuntimeCheckOutcome.NotTested: return "not-tested";
                case RuntimeCheckOutcome.Passed: return "passed";
                case RuntimeCheckOutcome.Failed: return "failed";
                default: throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "no such outcome");
            }
        }

        /// <summary>Resolves a recorded outcome.</summary>
        public static bool TryParseOutcome(string id, out RuntimeCheckOutcome outcome)
        {
            foreach (RuntimeCheckOutcome candidate in
                new[] { RuntimeCheckOutcome.NotTested, RuntimeCheckOutcome.Passed, RuntimeCheckOutcome.Failed })
            {
                if (string.Equals(IdOf(candidate), id, StringComparison.Ordinal))
                {
                    outcome = candidate;
                    return true;
                }
            }

            outcome = default;
            return false;
        }

        /// <summary>Every outcome id, in order — for a message naming the alternatives.</summary>
        public static IReadOnlyList<string> OutcomeIds()
            => new[]
            {
                IdOf(RuntimeCheckOutcome.NotTested),
                IdOf(RuntimeCheckOutcome.Passed),
                IdOf(RuntimeCheckOutcome.Failed),
            };

        /// <summary>
        /// The five checks as an unrun project holds them: every one
        /// <see cref="RuntimeCheckOutcome.NotTested"/>, with no observer, no
        /// time and no version.
        /// <para>
        /// ⛔ This is the ONLY state this assembly can produce. Applying a record
        /// on top of it is <see cref="Apply"/>, and a record is the only thing
        /// that carries an outcome — which is what makes "cannot be green
        /// without a run" a property of the code rather than a promise.
        /// </para>
        /// </summary>
        public static IReadOnlyList<RuntimeCheck> Untested()
        {
            var checks = new List<RuntimeCheck>(Checks.Count);
            foreach (var definition in Checks)
            {
                checks.Add(new RuntimeCheck(definition, RuntimeCheckOutcome.NotTested, null, null, null, null));
            }

            return checks;
        }

        /// <summary>
        /// The five checks with <paramref name="recorded"/> applied — the merge
        /// rule, pure so it can be driven without a file.
        /// <para>
        /// A recorded entry replaces the untested default for its own check and
        /// nothing else: the order is this vocabulary's, not the record's, and a
        /// check the record omits stays untested rather than disappearing. A
        /// record naming a check twice is the reader's to refuse; here the last
        /// entry would win, and nothing should ever reach that.
        /// </para>
        /// </summary>
        public static IReadOnlyList<RuntimeCheck> Apply(IReadOnlyList<RuntimeCheck> recorded)
        {
            if (recorded is null || recorded.Count == 0) return Untested();

            var byId = new Dictionary<RuntimeCheckId, RuntimeCheck>();
            foreach (var check in recorded)
            {
                if (check is null) continue;
                byId[check.Id] = check;
            }

            var merged = new List<RuntimeCheck>(Checks.Count);
            foreach (var definition in Checks)
            {
                merged.Add(byId.TryGetValue(definition.Check, out var check)
                    ? check
                    : new RuntimeCheck(definition, RuntimeCheckOutcome.NotTested, null, null, null, null));
            }

            return merged;
        }

        /// <summary>
        /// The one-line summary of the runtime result: how many of the five a
        /// run has established.
        /// </summary>
        /// <remarks>
        /// ⛔ Deliberately a count and never a percentage. A percentage beside a
        /// percentage is read as the same kind of number, and these two are not:
        /// one is a mean over types the scorer accepted, the other is five fixed
        /// questions with a yes, a no, or nothing behind them.
        /// </remarks>
        public static string Headline(IReadOnlyList<RuntimeCheck> checks)
        {
            if (checks is null) return "Runtime verification: not run";

            int passed = 0;
            int failed = 0;
            foreach (var check in checks)
            {
                if (check is null) continue;
                if (check.Outcome == RuntimeCheckOutcome.Passed) passed++;
                else if (check.Outcome == RuntimeCheckOutcome.Failed) failed++;
            }

            // ⚠️ The denominator is the VOCABULARY's, not the argument's. A caller
            // handing this the reader's output directly — a record naming one
            // check — would otherwise print "1 of 1 established", which reads as
            // a complete run. `ReadinessReport.Runtime` is always five, so no
            // production caller reaches that today; this is the API refusing to
            // be a foot-gun rather than a live repair.
            string headline = "Runtime verification: " + passed + " of " + Checks.Count + " established";
            return failed == 0 ? headline : headline + ", " + failed + " failed";
        }
    }

    /// <summary>The five things a run of a converted game can establish.</summary>
    public enum RuntimeCheckId
    {
        /// <summary>A handshake completed against a deployed gateway.</summary>
        Connection,

        /// <summary>Two clients hold one room and each sees the other on the roster.</summary>
        SharedRoom,

        /// <summary>Each client's spawned object reached the other.</summary>
        BothPlayers,

        /// <summary>A replicated value crossed, and a foreign write did not.</summary>
        Sync,

        /// <summary>A dropped session resumed on its token.</summary>
        Reconnect,
    }

    /// <summary>What a run said about one check — or that no run has said anything.</summary>
    public enum RuntimeCheckOutcome
    {
        /// <summary>No run has reported this check. The state every project starts in.</summary>
        NotTested,

        /// <summary>A run established it.</summary>
        Passed,

        /// <summary>A run tried and it did not hold.</summary>
        Failed,
    }

    /// <summary>One runtime check's fixed identity: its id, its title, and what passing it means.</summary>
    public sealed class RuntimeCheckDefinition
    {
        public RuntimeCheckDefinition(RuntimeCheckId id, string wireId, string title, string establishes)
        {
            Check = id;
            Id = wireId ?? throw new ArgumentNullException(nameof(wireId));
            Title = title ?? throw new ArgumentNullException(nameof(title));
            Establishes = establishes ?? throw new ArgumentNullException(nameof(establishes));
        }

        /// <summary>The id a record names this check by.</summary>
        public string Id { get; }

        /// <summary>The check itself.</summary>
        public RuntimeCheckId Check { get; }

        /// <summary>The short label a surface renders.</summary>
        public string Title { get; }

        /// <summary>What a pass means, in the game's own terms rather than the protocol's.</summary>
        public string Establishes { get; }
    }

    /// <summary>
    /// One check's current standing: the definition, the outcome, and — when a
    /// run reported one — the evidence that makes it an observation.
    /// </summary>
    public sealed class RuntimeCheck
    {
        public RuntimeCheck(
            RuntimeCheckDefinition definition,
            RuntimeCheckOutcome outcome,
            string observedBy,
            string observedAt,
            string sdkVersion,
            string detail)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            Outcome = outcome;
            ObservedBy = observedBy ?? string.Empty;
            ObservedAt = observedAt ?? string.Empty;
            SdkVersion = sdkVersion ?? string.Empty;
            Detail = detail ?? string.Empty;
        }

        public RuntimeCheckDefinition Definition { get; }

        /// <summary>The check this standing is about.</summary>
        public RuntimeCheckId Id => Definition.Check;

        public RuntimeCheckOutcome Outcome { get; }

        /// <summary>What ran it — a scenario name, or the developer. Empty when untested.</summary>
        public string ObservedBy { get; }

        /// <summary>When, as the record wrote it. Empty when untested.</summary>
        public string ObservedAt { get; }

        /// <summary>The SDK version it was observed against. Empty when untested.</summary>
        public string SdkVersion { get; }

        /// <summary>The observer's own words; empty when none.</summary>
        public string Detail { get; }
    }
}
