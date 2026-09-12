// RTMPE SDK — Runtime/Core/ApiKeySource.cs
//
// Where a build gets its API key from.
//
// `NetworkManager.Connect(string)` takes the key as an argument and says
// nothing about where the caller found it. Every sample, walkthrough and setup
// step therefore converged on the one place Unity makes obvious — a
// `[SerializeField] string` typed into the Inspector — and that is the worst
// available answer to the question:
//
//   * a serialized field is written verbatim into the scene or prefab asset,
//     so the key enters the integrator's version control and stays in its
//     history after any later removal;
//   * it is present in the built player whether or not the developer
//     remembered to blank it before the build;
//   * it is shared with everyone the project file is shared with, which is a
//     larger set than the people entitled to the credential.
//
// The sources below are the supported alternatives, and none of them is stored
// in the project or in the artifact.
//
// ⚠️ THE COMMAND LINE IS VISIBLE TO OTHER ACCOUNTS ON THE SAME MACHINE.
// `ps -ef` and /proc/<pid>/cmdline are world-readable, so `--rtmpe-api-key
// <key>` leaks the credential to every user of a shared workstation or CI
// runner — the same reasoning Editor/ApiKeyStore.cs gives for never putting it
// in argv. It is offered because a single-user machine is the common case and
// the alternative there is worse; `--rtmpe-api-key-file <path>` names a file
// instead and is the correct choice anywhere the host is shared.
//
// ⚠️ What none of this claims. A key a client presents at handshake is a key
// the person running that client can obtain — from process memory, from the
// environment they themselves supplied, or by reading the traffic they
// themselves originate. Nothing here changes that, and the credential model is
// what would: a short-lived, per-player token minted by the integrator's own
// backend and handed to `Connect`, with the project key never leaving that
// backend. `SetProvider` is the seam that model plugs into. What the sources
// below remove is the copy in the asset, the copy in the repository history
// and the copy in the shipped artifact — three exposures that do not require
// the attacker to be the player.
//
// ⚠️ One source is a deliberate exception and carries its own controls: a key
// staged for a DEVELOPMENT build (DevelopmentApiKeyFile) is a copy in the
// project and in that build's artifact. A release build refuses to carry it and
// the runtime refuses to read it, and neither can refuse a development build
// somebody distributes — which is why the wizard, the build refusal and the
// documentation all say so.

using System;
using System.Collections.Generic;
using System.IO;

namespace RTMPE.Core
{
    /// <summary>
    /// Resolves the gateway API key from a source outside the built artifact.
    /// </summary>
    public static class ApiKeySource
    {
        // Declared in the order Resolve() consults them, so a reader of this
        // type and a reader of any document describing it are told the same
        // thing.
        // 🚨 Held by TheConstantsAreDeclaredInTheOrderResolveConsultsThem, and
        // NOT by the documentation guard a previous version of this comment
        // named: that rule reads C# comments and string literals, because the
        // shape it kept refusing was a call's argument list — so a run of const
        // declarations is outside it by design, and the citation was false.

        /// <summary>
        /// Command-line option naming a FILE that holds the key. Preferred over
        /// <see cref="CommandLineOption"/> on any machine with more than one
        /// account: a path in argv is not a credential in argv.
        /// </summary>
        public const string CommandLineFileOption = "--rtmpe-api-key-file";

        /// <summary>
        /// Command-line option read by <see cref="Resolve"/>. Both
        /// <c>--rtmpe-api-key VALUE</c> and <c>--rtmpe-api-key=VALUE</c> are
        /// accepted, as is the single-dash spelling every built-in Unity player
        /// argument uses (<c>-batchmode</c>, <c>-logFile</c>) — a developer who
        /// types the option the way the engine's own options are written must
        /// not silently get no key.
        /// </summary>
        public const string CommandLineOption = "--rtmpe-api-key";

        /// <summary>Environment variable read by <see cref="Resolve"/>.</summary>
        public const string EnvironmentVariableName = "RTMPE_API_KEY";

        private static Func<string> _provider;
        private static Func<string> _secondaryProvider;

        [ThreadStatic]
        private static bool _resolving;

        /// <summary>
        /// The failure that made the last <see cref="TryResolve"/> return
        /// <c>false</c> for a reason other than "no source supplied one", or
        /// <c>null</c>. A configured source that fails is not the same as an
        /// absent one, and a caller reporting "no API key" without this would
        /// send the operator to the wrong place.
        /// </summary>
        public static Exception LastError { get; private set; }

        /// <summary>
        /// Registers the source consulted before every other. An integrator
        /// registers whatever obtains a credential for this player.
        /// <para>In the Editor the setup wizard's vault sits one tier below this
        /// one, so a registration here wins whenever it has a key and never
        /// removes the vault — which is why entering Play mode needs no key in
        /// the scene whether or not a game has registered anything.</para>
        /// </summary>
        /// <param name="provider">
        /// Returns the key, or <c>null</c>/empty when it has none. Passing
        /// <c>null</c> clears the registration.
        /// </param>
        public static void SetProvider(Func<string> provider)
        {
            _provider = provider;
        }

        /// <summary>
        /// Registers the source consulted when <see cref="SetProvider"/>'s
        /// registration returns <c>null</c> or an empty string.
        /// </summary>
        /// <remarks>
        /// ⛔ Not when the primary THROWS. That is a configured source that
        /// failed, and it ends resolution rather than handing the question
        /// down — a backend a provider could not reach is the case where
        /// answering with a different credential is worst, not best.
        /// </remarks>
        /// <remarks>
        /// 🔑 There is one primary slot and the Editor used to hold it with the
        /// setup wizard's vault, which made the two registrations rivals: a game
        /// following the documented instruction took the slot, and from then on
        /// an empty answer from that game fell through to the command line and
        /// the environment and never back to the vault — so the Editor reported
        /// no API key while the wizard held a good one. Ranked below the
        /// primary rather than beside it, the vault cannot displace a
        /// credential the integrator chose and cannot be displaced by one that
        /// is not there yet.
        /// ⛔ Internal because its registrars are this package's own: the Editor
        /// assembly registers the wizard's vault, and a player registers the key
        /// staged for a development build. An integrator has the primary slot,
        /// and a second public one would be an invitation to build the
        /// precedence question into a game.
        /// </remarks>
        /// <param name="sourceName">
        /// How the tier names itself in a diagnostic. The two registrars are
        /// different things to a reader — a vault they filled in the Editor, a
        /// file they staged into a build — and a message that named the wrong
        /// one would send them to look at the wrong place.
        /// </param>
        internal static void SetSecondaryProvider(Func<string> provider, string sourceName = null)
        {
            _secondaryProvider = provider;
            _secondaryProviderName = string.IsNullOrEmpty(sourceName)
                ? DefaultSecondaryProviderName
                : sourceName;
        }

        private const string DefaultSecondaryProviderName = "the Setup Wizard's credential vault";

        private static string _secondaryProviderName = DefaultSecondaryProviderName;

        /// <summary>
        /// The key, or an empty string when no source supplied one.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// A configured source failed: a provider registered with
        /// <see cref="SetProvider"/> or <see cref="SetSecondaryProvider"/> threw,
        /// or a key file named on the command line could not be read, held no
        /// key, or was named with no usable path. Continuing to the next source
        /// would connect with a credential the integrator did not configure.
        /// </exception>
        public static string Resolve()
        {
            return Resolve(SafeCommandLineArgs(), Environment.GetEnvironmentVariable,
                           _provider, _secondaryProvider, _secondaryProviderName);
        }

        /// <summary>
        /// The key, when a source supplied one. Never throws: a
        /// <c>TryXxx</c> that throws for the condition it names is one every
        /// caller mishandles, and this one is called from a connect path.
        /// A configured source that failed leaves <see cref="LastError"/> set.
        /// </summary>
        /// <returns><c>true</c> when <paramref name="apiKey"/> is non-empty.</returns>
        public static bool TryResolve(out string apiKey)
        {
            LastError = null;
            try
            {
                apiKey = Resolve();
            }
            catch (InvalidOperationException e)
            {
                LastError = e;
                apiKey = string.Empty;
                return false;
            }
            return apiKey.Length > 0;
        }

        /// <summary>
        /// Whether a source supplied a key, without handing one back.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Asking <see cref="TryResolve"/> and ignoring the value is the same
        /// question with the answer attached: the caller then holds a named
        /// reference to the credential for the rest of the scope, and a
        /// credential in scope is a credential that can be logged, concatenated
        /// into a message, or measured — and its LENGTH is a disclosure too,
        /// because it narrows what a guess has to cover. A caller that only
        /// needs to know whether to carry on has nothing to do with the value,
        /// and this is the shape that lets it say so.
        /// </para>
        /// <para>
        /// Otherwise identical to <see cref="TryResolve"/>: it never throws, and
        /// a configured source that failed leaves <see cref="LastError"/> set.
        /// </para>
        /// </remarks>
        public static bool IsAvailable()
        {
            return TryResolve(out _);
        }

        // The resolution order, and why it is this one: a provider is code the
        // integrator wrote on purpose, while the command line and the
        // environment are ambient properties of whatever launched the process.
        // A deliberate decision outranks an ambient one.
        // ⛔ The secondary tier's NAME travels with the provider rather than
        // being read from the field: a caller that supplies its own provider and
        // takes the name from whatever was last registered anywhere in the
        // process names the wrong source, and the two registrars this SDK has
        // are different things to a reader.
        internal static string Resolve(IList<string> commandLineArgs,
                                       Func<string, string> environment,
                                       Func<string> provider,
                                       Func<string> secondaryProvider = null,
                                       string secondaryProviderName = null)
        {
            string fromProvider = Clean(Invoke(provider, "the provider registered with "
                                                          + "ApiKeySource.SetProvider"));
            if (fromProvider.Length > 0) return fromProvider;

            // Ranked here rather than after the ambient sources: a project that
            // has never registered a primary resolves exactly as it did when the
            // Editor held the one slot, because the vault still outranks argv
            // and the environment.
            // ⛔ Three states DO differ, and they are the point rather than a
            // side effect: a primary that answers empty, one that answers null,
            // and one cleared with SetProvider(null) reached argv before and
            // reach the vault now. The first two are the defect this seam
            // exists for. The third follows from them — clearing your own
            // registration says nothing about the Editor's — and it is worth
            // knowing when a test means to observe the no-key path, which in
            // the Editor now needs the vault emptied as well.
            // ⛔ Named for the reader, not for the registrar. The seam this tier
            // is registered through is internal, so a message telling somebody to
            // change what it returns names a method they cannot call and did not
            // write — and in the Editor the thing that actually failed is the
            // wizard's vault.
            string fromSecondary = Clean(Invoke(
                secondaryProvider,
                string.IsNullOrEmpty(secondaryProviderName)
                    ? DefaultSecondaryProviderName
                    : secondaryProviderName));
            if (fromSecondary.Length > 0) return fromSecondary;

            string fromCommandLine = Clean(FromCommandLine(commandLineArgs));
            if (fromCommandLine.Length > 0) return fromCommandLine;

            return Clean(FromEnvironment(environment));
        }

        private static string FromEnvironment(Func<string, string> environment)
        {
            if (environment == null) return null;

            // Reading the environment is a platform call like any other, and
            // the contract of this source is "no key from it", never a throw
            // out of a connection attempt.
            try
            {
                return environment(EnvironmentVariableName);
            }
            catch (NotSupportedException) { return null; }
            catch (System.Security.SecurityException) { return null; }
        }

        private static string Invoke(Func<string> provider, string source)
        {
            // A provider that throws is the integrator's bug, and the one
            // outcome that must not follow is falling through to the next
            // source: the connection would then be made with a different
            // credential than the one configured, and would report success. So
            // it surfaces, named, with the original exception preserved.
            if (provider == null) return null;

            // A provider that calls back into Resolve() would recurse until the
            // runtime aborts, and a StackOverflowException cannot be caught.
            // Re-entry is answered the same way an absent provider is.
            if (_resolving) return null;
            _resolving = true;
            try
            {
                return provider();
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    "The API key could not be read: " + source + " threw. A source that " +
                    "answers with null or an empty string is treated as having no key; " +
                    "one that throws stops resolution.", e);
            }
            finally
            {
                _resolving = false;
            }
        }

        private static string FromCommandLine(IList<string> args)
        {
            if (args == null) return null;

            // The file form is looked for across the whole vector before the
            // inline form, rather than whichever appears first: a launch line
            // carrying both should resolve to the one that does not put the
            // credential in a world-readable /proc entry.
            string path = FirstOptionValue(args, CommandLineFileOption);
            if (!string.IsNullOrWhiteSpace(path)) return ReadKeyFile(path);

            // Named with nothing usable after it: `--rtmpe-api-key-file $KEYFILE`
            // with the variable unset drops the argument entirely, and the option
            // can also be last on the line or carry an explicit empty value.
            // OptionValue declines to consume a token beginning with `-`, which
            // is right for the inline form — a launch line whose value was
            // dropped must not authenticate with `--fullscreen` — and cannot be
            // right here, because the two answers are not symmetric: read as "the
            // option is absent" this resolves to argv and then the environment,
            // and the argv form is the one whose documented problem is a
            // world-readable /proc entry.
            if (path != null || NamesOption(args, CommandLineFileOption))
            {
                throw new InvalidOperationException(
                    "The API-key file option " + CommandLineFileOption +
                    " was given with no usable path.");
            }

            return FirstOptionValue(args, CommandLineOption);
        }

        /// <summary>
        /// Whether <paramref name="option"/> appears at all, in either spelling
        /// and either form — a different question from what its value is, and the
        /// one that separates a dropped value from an absent option.
        /// </summary>
        private static bool NamesOption(IList<string> args, string option)
        {
            string shortForm = option.Substring(1);
            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];
                if (arg == null) continue;
                foreach (string name in new[] { option, shortForm })
                {
                    if (arg == name) return true;
                    if (arg.StartsWith(name + "=", StringComparison.Ordinal)) return true;
                }
            }
            return false;
        }

        private static string FirstOptionValue(IList<string> args, string option)
        {
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i] == null) continue;
                string value = OptionValue(args, ref i, option);
                if (value != null) return value;
            }
            return null;
        }

        /// <summary>
        /// The value of <paramref name="option"/> at <paramref name="index"/>,
        /// or <c>null</c> when this argument is not that option.
        /// </summary>
        private static string OptionValue(IList<string> args, ref int index, string option)
        {
            string arg = args[index];

            // Unity's own player arguments are single-dash, so both spellings
            // are recognised; the constants stay double-dash because that is
            // what the documentation gives.
            string shortForm = option.Substring(1);

            foreach (string name in new[] { option, shortForm })
            {
                if (arg.StartsWith(name + "=", StringComparison.Ordinal))
                    return arg.Substring(name.Length + 1);

                // The separated form takes the NEXT argument — but only when
                // there is one and it is not itself an option. A launch line
                // whose value was dropped would otherwise authenticate with
                // `--fullscreen` and send the operator to the dashboard.
                if (arg == name && index + 1 < args.Count)
                {
                    string next = args[index + 1];
                    if (next != null && !next.StartsWith("-", StringComparison.Ordinal))
                    {
                        index++;
                        return next;
                    }
                }
            }
            return null;
        }

        private static string ReadKeyFile(string path)
        {
            // A file the operator named and this cannot read is a configuration
            // error, not an absent source: falling through would use a
            // different credential.
            string contents;
            try
            {
                contents = File.ReadAllText(path);
            }
            catch (Exception e)
            {
                // ⛔ The cause is named by TYPE and not carried.  An exception
                // raised by the file API quotes the path it was given inside its
                // own message — absolutised, and a second time in the case of a
                // missing file — so attaching it republishes in full the
                // argument the message above is careful not to print, to every
                // caller that reaches LastError.ToString() or hands it to
                // Debug.LogException.  The type is the actionable half: absent,
                // unreadable and malformed are different repairs, and none of
                // the three names a path.
                throw new InvalidOperationException(
                    "The API-key file named by " + CommandLineFileOption +
                    " could not be read (" + e.GetType().Name + "): " +
                    DescribePath(path));
            }

            // A readable file holding no key fails the same way, and is reached
            // far more often: an interrupted write, or a secret mount that has
            // not populated yet. Read as "no key from this source" it resolves
            // to the environment instead — the single outcome that naming a file
            // exists to exclude.
            //
            // The test is "empty once trimmed", not "looks like a key". A file
            // holding a placeholder resolves as that placeholder and is refused
            // at the handshake instead, which is the right place for it: only
            // the gateway knows what a valid key is, and a client-side opinion
            // about the shape of one is a rule that expires when the format
            // changes.
            string key = Clean(contents);
            if (key.Length == 0)
            {
                throw new InvalidOperationException(
                    "The API-key file named by " + CommandLineFileOption +
                    " holds no key: " + DescribePath(path));
            }

            return key;
        }

        /// <summary>
        /// What stands in a log where the last segment of a configured path
        /// would otherwise be written.
        /// </summary>
        /// <remarks>
        /// Fixed text, carrying nothing derived from the value it replaces —
        /// not a character of it and not its length — so the marker reads the
        /// same whether the argument was a filename or the credential.
        /// </remarks>
        private const string RedactedLeaf = "<redacted>";

        /// <summary>
        /// What stands in a log where a configured path would otherwise be
        /// written whole, when the path itself cannot be written.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="RedactedLeaf"/> because the two answers have
        /// different repairs. Sharing one would give the same line to a mistyped
        /// credential, an ordinary relative filename and a directory carrying a
        /// character that cannot be logged — and the last of those is the only
        /// one the reader cannot work out for themselves.
        /// </remarks>
        private const string UnprintablePath =
            "<redacted: the path carries a character that cannot be logged>";

        /// <summary>
        /// A path as it may be written to a log.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The two options differ by one suffix, so naming the key where a path
        /// belongs is a one-token mistake — and the argument is then the
        /// credential itself, on its way into Player.log through
        /// <see cref="LastError"/>, which is what a user attaches to a bug
        /// report. What survives is the directory, when the argument has one:
        /// it is what tells a caller where the lookup went, and a mistyped key
        /// does not have one — the issued format is a fixed prefix followed by
        /// hexadecimal, carrying no separator, so
        /// <see cref="Path.GetDirectoryName"/> answers empty for one and the
        /// whole argument falls under the marker.
        /// </para>
        /// <para>
        /// ⚠️ That last step is a premise about the credential rather than a
        /// property of this method: a future format containing a separator would
        /// have everything before its last one published. A relative filename
        /// has no directory either, so the commonest invocation of all is
        /// diagnosed by the option name alone.
        /// </para>
        /// <para>
        /// ⛔ The leaf is replaced rather than shortened.
        /// <see cref="LogRedaction.Redact(string)"/> keeps a four-character
        /// prefix and, at four characters or fewer, returns the value entire;
        /// both are right for a session or player identifier, where a stable
        /// head is how a client log is matched against a server trace, and
        /// neither is right for a value that may be the secret — this package's
        /// standard for one being "not the key, not its length, not a prefix of
        /// it". A leaf carries no diagnosis a caller who typed it does not
        /// already hold.
        /// </para>
        /// </remarks>
        private static string DescribePath(string path)
        {
            try
            {
                string directory = Path.GetDirectoryName(path);

                // Absence first.  GetDirectoryName answers null — not empty —
                // for an argument that is nothing but separators, and for a
                // relative filename it answers empty; both mean there is no
                // directory to publish, and both must be settled before
                // anything reads the answer's characters.
                if (string.IsNullOrEmpty(directory)) return RedactedLeaf;

                // A directory carrying a character that reshapes a log entry is
                // dropped rather than cleaned: a cleaned path no longer names
                // what the caller typed, which sends them looking for something
                // that was never there.
                if (ReshapesALogEntry(directory)) return UnprintablePath;

                // Concatenated rather than composed through Path.Combine: the
                // marker is not a filename, and the runtimes that still screen
                // path arguments for invalid characters reject it — from inside
                // the construction of an error message, where the replacement
                // exception would displace the failure being reported.
                return directory + Path.DirectorySeparatorChar + RedactedLeaf;
            }
            catch (ArgumentException)
            {
                // A path the framework declines to parse is still a path this
                // must not print, and there is no directory to separate from it.
                return UnprintablePath;
            }
        }

        /// <summary>
        /// Whether <paramref name="value"/> carries a character that changes the
        /// shape of a log entry rather than appearing inside it.
        /// </summary>
        /// <remarks>
        /// The C0 block — an embedded NUL ends the entry at any sink parsing C
        /// strings, collapsing two distinct failures into one line — together
        /// with DEL, the C1 block, and the Unicode line and paragraph
        /// separators, which a Unicode-aware reader renders as a break and which
        /// therefore let one entry present as two. This is the class
        /// <see cref="LogRedaction"/> screens on the values it shortens; the
        /// directory reaches the log through no redactor and so states it here.
        /// </remarks>
        private static bool ReshapesALogEntry(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c < 0x20 || c == 0x7F
                    || (c >= 0x80 && c <= 0x9F)
                    || c == 0x2028 || c == 0x2029) return true;
            }

            return false;
        }

        private static string Clean(string value)
        {
            return value == null ? string.Empty : value.Trim();
        }

        /// <summary>
        /// The process argument vector. A seam, because the parameterless
        /// <see cref="Resolve"/> is what a built player calls and a test that
        /// only drives the internal overload proves nothing about whether the
        /// command line is wired to it at all.
        /// </summary>
        internal static Func<IList<string>> CommandLineArgs = Environment.GetCommandLineArgs;

        private static IList<string> SafeCommandLineArgs()
        {
            // Not every player hosts a command line — WebGL is the one that
            // ships today — and the contract of this source is "no key from
            // it", never an exception thrown out of Resolve().
            // PlatformNotSupportedException derives from NotSupportedException,
            // so the one clause covers both of the ways a runtime declines.
            try
            {
                return CommandLineArgs();
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }
    }
}
