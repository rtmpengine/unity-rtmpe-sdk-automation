using System;
using System.Collections.Generic;

namespace RTMPE.SDK.Conversion.Core
{
    /// <summary>
    /// The questions the toolchain asks when the rubric cannot answer, and the
    /// fixed vocabulary they are answered in.
    /// <para>
    /// <see cref="AuthorityClassifier"/> returns <see cref="AuthorityRole.Undetermined"/>
    /// rather than guessing. Asking is the other way out of that fallback, and
    /// it is not a guess either: the answer comes from the person holding the
    /// design. Pure and deterministic, like the rubric beside it — the same type
    /// name and signals always produce the same question.
    /// </para>
    /// </summary>
    public static class AuthorityQuestionnaire
    {
        /// <summary>
        /// The file a project records its answers in, beside the readiness
        /// artifact.
        /// <para>
        /// 🔑 Its own file, and an INPUT to the scan. The artifact is rewritten
        /// whole on every run, so an answer stored there survives exactly until
        /// the next scan — which is the run that was supposed to read it. The
        /// artifact carries the answers back out for the editor to render; this
        /// file is where they live.
        /// </para>
        /// </summary>
        public const string AnswerFileName = "network-authority-answers.json";

        /// <summary>
        /// The one option whose answer cannot be delivered from Unity: the
        /// backend handler is Go, in the Room Service, and nothing registers one
        /// today.
        /// </summary>
        public const string ServerImplementationNote =
            "this is the one answer that needs code outside the Unity project: a handler registered "
            + "with RegisterServerRpc in the Room Service (Go), bound to this method's id — with none "
            + "registered the call resolves to RpcErrorUnknownMethod and the body runs on no node";

        private static readonly AuthorityAnswerOption OwnerOption = new AuthorityAnswerOption(
            AuthorityDecidedBy.Owner,
            "owner",
            "the player who owns this object decides; everybody else is shown the result",
            "the one authority this runtime enforces: a NetworkVariable is written by its owner and "
            + "refused anywhere else, at the client flush gate and again at the gateway's "
            + "object_authority_ok check. Hold the state in a NetworkVariable (RTMPE2002, or RTMPE2005 "
            + "where it is an auto-property) and open every method that writes it with "
            + "`if (!IsOwner) return;` (RTMPE2003)",
            needsServerImplementation: false);

        private static readonly AuthorityAnswerOption HostOption = new AuthorityAnswerOption(
            AuthorityDecidedBy.Host,
            "host",
            "one player — whoever is the room's host — decides for everybody",
            "a convention rather than an enforcement: the host is a client like any other, and what "
            + "the runtime checks is ownership, not who is host. It holds only while the host also "
            + "OWNS the object — gate the decision on IsMasterClient, reached through "
            + "NetworkManager.TryGetInstance(out var manager) rather than through Instance, which is "
            + "null after OnApplicationQuit and off the main thread; and keep the object owned by the "
            + "host, because a peer that owns it can still write it, and the host changes when the "
            + "host leaves",
            needsServerImplementation: false);

        private static readonly AuthorityAnswerOption ServerOption = new AuthorityAnswerOption(
            AuthorityDecidedBy.Server,
            "server",
            "the server decides; a client asks and waits",
            "send it as a Server-targeted Enhanced-RPC (RTMPE2004) — " + ServerImplementationNote,
            needsServerImplementation: true);

        private static readonly AuthorityAnswerOption EachClientOption = new AuthorityAnswerOption(
            AuthorityDecidedBy.EachClient,
            "each-client",
            "every client works it out for itself; nothing has to agree",
            "then nothing here replicates and no authority is owed. The type is still a "
            + "NetworkBehaviour, which costs it a spawn and an identity the runtime need not give it "
            + "— keep that only if the component is on a networked object for some other reason; "
            + "otherwise a plain MonoBehaviour is the honest declaration",
            needsServerImplementation: false);

        /// <summary>The four answers, in fixed order.</summary>
        public static readonly IReadOnlyList<AuthorityAnswerOption> Options =
            new[] { OwnerOption, HostOption, ServerOption, EachClientOption };

        /// <summary>The option carrying <paramref name="decidedBy"/>.</summary>
        public static AuthorityAnswerOption OptionFor(AuthorityDecidedBy decidedBy)
        {
            foreach (var option in Options)
            {
                if (option.DecidedBy == decidedBy) return option;
            }

            throw new ArgumentOutOfRangeException(nameof(decidedBy));
        }

        /// <summary>The stable token <paramref name="decidedBy"/> is written as.</summary>
        public static string IdOf(AuthorityDecidedBy decidedBy) => OptionFor(decidedBy).Id;

        /// <summary>
        /// Reads an answer token. Ordinal and exact: an id is machine-written and
        /// machine-read, so a near miss is a mistake to report rather than a
        /// spelling to accommodate.
        /// </summary>
        public static bool TryParseId(string id, out AuthorityDecidedBy decidedBy)
        {
            foreach (var option in Options)
            {
                if (string.Equals(option.Id, id, StringComparison.Ordinal))
                {
                    decidedBy = option.DecidedBy;
                    return true;
                }
            }

            decidedBy = default;
            return false;
        }

        /// <summary>Every answer token, in option order — for a parser's error text.</summary>
        public static IReadOnlyList<string> AnswerIds()
        {
            var ids = new List<string>(Options.Count);
            foreach (var option in Options) ids.Add(option.Id);
            return ids;
        }

        /// <summary>
        /// The question for one type, or <see langword="null"/> where there is
        /// nothing a developer can settle.
        /// </summary>
        /// <remarks>
        /// 🔑 The rubric decides this, not a second copy of it: a question is
        /// asked only where <see cref="AuthorityClassifier.Classify"/> itself
        /// returned <see cref="AuthorityRole.Undetermined"/>, and the two causes
        /// an answer must not touch are excluded by the single signal that
        /// defines each.
        /// <para>
        /// ⛔ <see cref="AuthoritySignals.ReadPartially"/> — the reader did not
        /// open the whole chain. An answer there would certify code nobody has
        /// looked at, which is worse than the silence it replaces; the remedy is
        /// to reference the assembly, not to declare an intent.
        /// </para>
        /// <para>
        /// ⛔ A type that does not inherit <c>NetworkBehaviour</c> — the runtime
        /// discovers none of its networking signals, so no declaration makes the
        /// intent reachable. The remedy is RTMPE2001, and stating an intent over
        /// it would buy a cleared dimension for code that cannot run.
        /// </para>
        /// </remarks>
        public static AuthorityQuestion For(string typeName, AuthoritySignals signals)
        {
            if (typeName is null) throw new ArgumentNullException(nameof(typeName));
            if (signals is null) throw new ArgumentNullException(nameof(signals));

            if (AuthorityClassifier.Classify(signals).Role != AuthorityRole.Undetermined) return null;
            if (signals.ReadPartially || !signals.InheritsNetworkBehaviour) return null;

            string subject = Subject(typeName);
            return new AuthorityQuestion(typeName, subject, "Who decides " + subject + "?", Options);
        }

        /// <summary>
        /// The distribution of responsibilities one answer proposes: what each
        /// party is answerable for, and where the work lands.
        /// </summary>
        public static IReadOnlyList<string> Responsibilities(AuthorityDecidedBy decidedBy)
        {
            switch (decidedBy)
            {
                case AuthorityDecidedBy.Owner:
                    return new[]
                    {
                        "the owning client writes the value, and is the only writer the runtime admits",
                        "every other client reads the replicated value and never writes it",
                        "the server relays it and validates nothing about it",
                    };
                case AuthorityDecidedBy.Host:
                    return new[]
                    {
                        "the host client decides — and must also own the object, or the runtime "
                        + "refuses nobody",
                        "every other client reads it; one that owns the object can still write it, "
                        + "host or not",
                        "the server relays it; it knows which session is the room's master and "
                        + "spends that only on host-only room operations, never on this write",
                    };
                case AuthorityDecidedBy.Server:
                    return new[]
                    {
                        "the server decides, in a handler this project has yet to write",
                        "every client asks with a Server-targeted Enhanced-RPC and waits for the answer",
                        "⚠️ " + ServerImplementationNote,
                    };
                case AuthorityDecidedBy.EachClient:
                    return new[]
                    {
                        "every client decides for itself and nothing is replicated",
                        "no ownership is owed, and no NetworkVariable is needed",
                    };
                default:
                    throw new ArgumentOutOfRangeException(nameof(decidedBy));
            }
        }

        /// <summary>
        /// The evidence line the report carries beside a declared authority, so
        /// every surface that already renders evidence says where the answer came
        /// from without being changed.
        /// </summary>
        public static string DeclarationEvidence(AuthorityDecidedBy decidedBy)
            => "authority declared by the project: " + OptionFor(decidedBy).Label;

        // The concern, read from the type's own name.
        //
        // ⚠️ A phrasing aid, and the one place in this file that is not exact. It
        // decides how a question READS and never what is asked, of whom, or what
        // an answer means — a name it does not recognise falls back to a neutral
        // wording rather than to a guess, and the type is named beside the
        // question by every surface that renders it. Ordered most specific first,
        // first match wins — which decides real collisions: ItemSpawner reads as
        // an item rather than a spawn, RespawnTimer as a round rather than a
        // respawn. Either reading is defensible; the ordering is what makes the
        // choice the same one on every run.
        //
        // 🔑 Matched against the identifier's WORDS, never as a substring. A
        // substring rule reads Skill as "kill", Steam as "team", Runtime as
        // "run", Lookup as "look" and Claim as "aim" — all measured, all common
        // Unity type names, and every one of them puts a confidently wrong noun
        // in front of the developer in the sentence that is supposed to be in
        // their own vocabulary. A missed match costs the neutral wording, which
        // is what the fallback is for; a wrong match costs trust in the question.
        private static readonly (string Subject, string[] Markers)[] Subjects =
        {
            ("when the round starts and ends",
                new[] { "round", "match", "wave", "phase", "countdown", "timer", "clock" }),
            ("the score",
                new[] { "score", "kill", "frag", "leaderboard", "rank" }),
            ("health and damage",
                new[] { "health", "damage", "hurt", "armor", "armour", "shield" }),
            ("whether a hit counts",
                new[] { "weapon", "gun", "shoot", "fire", "bullet", "projectile", "ammo", "reload",
                    "collision", "collider" }),
            ("who is holding an item",
                new[] { "inventory", "item", "pickup", "loot", "equip" }),
            ("when this spawns, and where",
                new[] { "spawn", "respawn" }),
            ("which side a player is on",
                new[] { "team", "party", "squad", "lobby" }),
            ("this object's movement",
                new[] { "move", "motion", "walk", "run", "jump", "velocity", "position", "transform",
                    "look", "aim" }),
        };

        // The endings a marker may carry and still be the same word. Deliberately
        // short: every entry here is a way for a marker to reach further, and the
        // reason the rule exists is that reaching too far is the expensive
        // direction. "ment" earns its place on its own — PlayerMovement is the
        // canonical Unity spelling of the one concern this table most needs.
        private static readonly string[] WordEndings = { "s", "es", "ing", "er", "ers", "ment", "ments" };

        private const string NeutralSubject = "what this component does";

        private static string Subject(string typeName)
        {
            var words = Words(SimpleName(typeName));
            foreach (var (subject, markers) in Subjects)
            {
                foreach (string marker in markers)
                {
                    foreach (string word in words)
                    {
                        if (IsWord(word, marker)) return subject;
                    }
                }
            }

            return NeutralSubject;
        }

        private static bool IsWord(string word, string marker)
        {
            if (string.Equals(word, marker, StringComparison.Ordinal)) return true;
            if (word.Length <= marker.Length || !word.StartsWith(marker, StringComparison.Ordinal)) return false;

            string ending = word.Substring(marker.Length);
            foreach (string candidate in WordEndings)
            {
                if (string.Equals(ending, candidate, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        // The identifier's words, lower-cased: split on anything that is not a
        // letter or digit, and at a lower-to-upper transition, with an acronym run
        // ending one word before the capital that starts the next (HTTPServer is
        // two words, not one). Generic arity arrives here as `<T>` and falls out
        // with the punctuation.
        private static IReadOnlyList<string> Words(string name)
        {
            var words = new List<string>();
            var current = new System.Text.StringBuilder();

            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (!char.IsLetterOrDigit(c))
                {
                    Flush(words, current);
                    continue;
                }

                bool startsWord = char.IsUpper(c)
                    && current.Length > 0
                    && (!char.IsUpper(name[i - 1])
                        || (i + 1 < name.Length && char.IsLower(name[i + 1])));

                if (startsWord) Flush(words, current);
                current.Append(char.ToLowerInvariant(c));
            }

            Flush(words, current);
            return words;
        }

        private static void Flush(List<string> words, System.Text.StringBuilder current)
        {
            if (current.Length == 0) return;

            words.Add(current.ToString());
            current.Length = 0;
        }

        // The leaf of a metadata name. Deliberately not shared with the editor's
        // own short-name rule: that one exists to keep two WINDOWS rendering the
        // same label and lives in an assembly Unity compiles; this one is an
        // internal detail of how a question is worded, and the two cannot
        // reference each other. A divergence changes a question's wording and
        // nothing else.
        private static string SimpleName(string typeName)
        {
            int lastDot = typeName.LastIndexOf('.');
            return lastDot >= 0 && lastDot + 1 < typeName.Length
                ? typeName.Substring(lastDot + 1)
                : typeName;
        }
    }
}
