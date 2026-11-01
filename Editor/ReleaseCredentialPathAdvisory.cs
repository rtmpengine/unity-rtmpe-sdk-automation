// RTMPE SDK — Editor/ReleaseCredentialPathAdvisory.cs
//
// What a RELEASE player will have to authenticate with, decided while the build
// is still cancellable — and asked earlier than that by the Setup Wizard's
// production step, through the same walk and the same reading of each script
// (ScriptsUnder, RegistrationsAmong), so the step and the build read one
// project the same way.
//
// 🔑 The sources a player can use are not equal, and only one of them is visible
// at build time. A provider registered with ApiKeySource.SetProvider is code in
// the project, so it can be looked for; --rtmpe-api-key-file, --rtmpe-api-key
// and RTMPE_API_KEY are launch-time choices this hook cannot see; and the
// development-build key is, by construction, not in a release build.
//
// ⛔ So the only honest statement is a conditional one: a release build with no
// provider in the project WILL have no credential unless somebody launches it
// with an argument or an environment variable — and on a player's machine
// nothing has set either. That sentence was learned the expensive way, from an
// integrator who built three times before anyone could say it.
//
// ⚠️ A WARNING and never a refusal. A launch-line deployment — a dedicated
// server, a kiosk, a CI harness — is a legitimate shape that this check cannot
// distinguish from a mistake, and a build hook that refuses correct work is one
// somebody removes.

namespace RTMPE.Editor
{
    internal static class ReleaseCredentialPathAdvisory
    {
        /// <summary>
        /// Whether this build is owed the warning.
        /// </summary>
        /// <remarks>
        /// ⛔ Release builds only. A development build has the injected key when
        /// the project opted in, and when it did not it is told so by the
        /// player's own runtime report — which names the switch. Warning there
        /// too would spend the reader's attention on the case that already has
        /// an answer.
        /// </remarks>
        internal static bool ShouldWarn(bool development, bool providerRegisteredInProject)
            => !development && !providerRegisteredInProject;

        /// <summary>The token a project's own scripts are searched for.</summary>
        /// <remarks>
        /// ⚠️ The registration call, not the type: naming `ApiKeySource` alone
        /// matches a file that merely mentions it — a comment, a using, the
        /// advisory text itself — and a check satisfied by prose is a check that
        /// never fires.
        /// </remarks>
        internal const string ProviderRegistration = "ApiKeySource.SetProvider";

        /// <summary>
        /// Every script under <paramref name="dataPath"/> — the project's
        /// <c>Assets</c> folder — whose CODE calls the registration, as the
        /// paths Unity shows for them (<c>Assets/…</c>, forward slashes), in
        /// ordinal order; empty when none does.
        /// </summary>
        /// <remarks>
        /// <para>
        /// 🔑 One walk, asked by the build hook and by the wizard's production
        /// step, so the two answer from one fact: a step that reported a
        /// provider the build then warned about, or the reverse, would be two
        /// readings of one project disagreeing in front of the developer.
        /// Every match rather than the first, sorted: the SDK keeps ONE
        /// registration, so two scripts that register are a fact the wizard has
        /// to say, and "first" by a file system's enumeration order is a
        /// different file on every machine.
        /// </para>
        /// <para>
        /// ⛔ The call, in code — never the words. A bare text search answered
        /// yes to two of this package's own samples, which NAME the call in a
        /// comment and in a runtime message without making it (Basic
        /// Connection, Player Spawn Flow), so importing the first sample a
        /// newcomer is offered turned the wizard's step green over a project
        /// that registers nothing. Comments and the contents of string and
        /// character literals are blanked in one pass before the call shape
        /// (<c>ApiKeySource . SetProvider (</c>, whitespace free) is looked for.
        /// </para>
        /// <para>
        /// ⚠️ Still a reading of text, and it says so: a call under
        /// <c>#if UNITY_EDITOR</c> or <c>#if false</c>, in an <c>Editor</c>
        /// folder or another file excluded from the player's assemblies, or in
        /// a component on no object in any scene counts as one; a type of the
        /// developer's own named <c>ApiKeySource</c> does too. That is the right
        /// direction for a WARNING — it errs towards silence, and a warning
        /// that cried wolf on a project which does register one would be
        /// switched off by the person who most needs it — which is why the
        /// path is answered rather than a bool: the wizard shows WHICH file
        /// satisfied the scan, and adds what a text reading cannot see about
        /// its own script (compiled, and on the manager). Files and folders
        /// Unity never imports — a name ending in <c>~</c>, starting with
        /// <c>.</c>, or <c>cvs</c> — are not read or walked, because a script
        /// there is compiled by nothing.
        /// </para>
        /// <para>
        /// ⛔ Outside the walk, and stated where the wizard says "nothing
        /// registers": a package of the project's own under <c>Packages/</c>
        /// and a precompiled DLL. Throws only when the root itself cannot be
        /// walked, and each caller decides what that means: the build hook
        /// claims a provider IS registered (a hook must not invent a fault out
        /// of its own failure), the wizard says it could not look. A folder or
        /// file inside that cannot be read is skipped and counted instead.
        /// </para>
        /// </remarks>
        internal static string[] RegistrationsIn(string dataPath, out int unreadable)
            => RegistrationsIn(dataPath, out unreadable, out _);

        /// <summary>
        /// The registrations under <paramref name="dataPath"/>, and which of
        /// them a PLAYER build compiles.
        /// </summary>
        /// <remarks>
        /// 🔑 Both lists, rather than one filtered: they answer different
        /// questions and a caller that had only the second would lose the
        /// first. What a release build authenticates with is the second; what
        /// competes for the SDK's single provider slot in Play mode is the
        /// first, and an Editor-only registration competes there exactly as
        /// well as any other.
        /// </remarks>
        internal static string[] RegistrationsIn(
            string dataPath, out int unreadable, out string[] compiledIntoPlayers)
        {
            unreadable = 0;
            if (string.IsNullOrEmpty(dataPath))
            {
                compiledIntoPlayers = new string[0];
                return new string[0];
            }

            System.Collections.Generic.IEnumerable<string> scripts = ScriptsUnder(dataPath, ref unreadable);
            return RegistrationsAmong(dataPath, scripts, ref unreadable, out compiledIntoPlayers);
        }

        /// <summary>
        /// The reading half of <see cref="RegistrationsIn(string, out int)"/>: of the scripts in
        /// <paramref name="scripts"/> — absolute paths under
        /// <paramref name="dataPath"/>, as <see cref="ScriptsUnder"/> lists them
        /// — the ones whose code calls the registration, as the paths Unity
        /// shows, in ordinal order; a script that cannot be read is skipped and
        /// counted in <paramref name="unreadable"/>.
        /// </summary>
        /// <remarks>
        /// Separate from the walk so a caller with a second question about the
        /// same project — the wizard also looks for its own script by name —
        /// walks once and hands the one list to both, rather than listing the
        /// tree twice for one press.
        /// </remarks>
        internal static string[] RegistrationsAmong(
            string dataPath, System.Collections.Generic.IEnumerable<string> scripts, ref int unreadable)
        {
            return RegistrationsAmong(dataPath, scripts, ref unreadable, out _);
        }

        /// <summary>
        /// The reading half of <see cref="RegistrationsIn(string, out int, out string[])"/>,
        /// which also decides — per script — whether a player build compiles
        /// the registration it found.
        /// </summary>
        /// <remarks>
        /// Three things put a registration outside a player build, and all
        /// three are asked here because only here are all three in hand: the
        /// folder Unity compiles into the Editor assembly, the assembly
        /// definition nearest the script, and the <c>#if</c> region the call
        /// sits in. <see cref="PlayerBuildScope"/> holds the rules; this
        /// carries the file to them.
        /// </remarks>
        internal static string[] RegistrationsAmong(
            string dataPath, System.Collections.Generic.IEnumerable<string> scripts, ref int unreadable,
            out string[] compiledIntoPlayers)
        {
            var found = new System.Collections.Generic.List<string>();
            var inPlayers = new System.Collections.Generic.List<string>();
            foreach (string file in scripts)
            {
                string text;
                try
                {
                    text = System.IO.File.ReadAllText(file);
                }
                catch (System.Exception)
                {
                    unreadable++;
                    continue;
                }

                if (!RegistersAProvider(text, out bool survivesTheDirectives)) continue;

                string projectPath = ProjectPathOf(dataPath, file);
                found.Add(projectPath);

                if (survivesTheDirectives
                    && PlayerBuildScope.CompiledAtAll(projectPath)
                    && !GoverningAssemblyIsEditorOnly(dataPath, file, projectPath, ref unreadable))
                {
                    inPlayers.Add(projectPath);
                }
            }

            found.Sort(System.StringComparer.Ordinal);
            inPlayers.Sort(System.StringComparer.Ordinal);
            compiledIntoPlayers = inPlayers.ToArray();
            return found.ToArray();
        }

        /// <summary>
        /// Whether the assembly <paramref name="file"/> belongs to is one no
        /// player build carries.
        /// </summary>
        /// <remarks>
        /// ⛔ The DEFINITION first, the folder second, and never both: Unity's
        /// special folder names decide only for the predefined assemblies. Once
        /// an assembly definition governs a folder, that definition is the
        /// whole answer and an <c>Editor</c> subfolder inside its scope is an
        /// ordinary folder — which is why every package, this one included,
        /// ships a separate Editor definition rather than relying on the name.
        /// Asking both, and refusing on either, reported a ⚠️ for
        /// <c>Assets/Game/Editor/Credentials.cs</c> under an all-platform
        /// <c>Game.asmdef</c>: a file the player carries, with a remedy the
        /// author could not act on.
        /// </remarks>
        private static bool GoverningAssemblyIsEditorOnly(
            string dataPath, string file, string projectPath, ref int unreadable)
        {
            // ⛔ A definition this could not READ is counted, not swallowed.
            // Unreadable answers "no definition", which reads as the default
            // assembly and lets the step draw a ✅ — and without the count the
            // step's "N scripts or folders could not be read, the answer stands
            // for the rest" never appears, so the ✅ is printed with no hedge at
            // all. An editor-only definition behind a permission then reads as
            // a registration the build compiles.
            string definition = NearestAssemblyDefinition(dataPath, file, out bool couldNotRead);
            if (couldNotRead) unreadable++;

            return definition == null
                ? PlayerBuildScope.UnderAnEditorFolder(projectPath)
                : PlayerBuildScope.EditorOnlyAssembly(definition);
        }

        /// <summary>
        /// The assembly definition governing <paramref name="file"/> — the
        /// nearest <c>.asmdef</c> at or above it and below
        /// <paramref name="dataPath"/>, or the one an <c>.asmref</c> in the way
        /// names — or <see langword="null"/> when there is none, or none that
        /// can be read.
        /// </summary>
        /// <remarks>
        /// ⛔ Nearest wins and the walk stops there: Unity assigns a script to
        /// the closest definition above it, so a definition further up governs
        /// nothing once one is found. A folder that cannot be listed and a file
        /// that cannot be read both answer "no definition", which reads as the
        /// default assembly — the direction that never invents an exclusion.
        /// <para>
        /// ⚠️ An <c>.asmref</c> stops the search as an <c>.asmdef</c> does, and
        /// it names its assembly rather than declaring it: skipping the shape
        /// walked past a folder assigned to an editor-only assembly and read it
        /// as the default one, which is the ✅ direction. The named definition
        /// is looked for by its <c>name</c> under the project.
        /// </para>
        /// </remarks>
        private static string NearestAssemblyDefinition(
            string dataPath, string file, out bool couldNotRead)
        {
            couldNotRead = false;
            string root = System.IO.Path.GetFullPath(dataPath)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            string directory;
            try
            {
                directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file));
            }
            catch (System.Exception)
            {
                couldNotRead = true;
                return null;
            }

            while (!string.IsNullOrEmpty(directory) && directory.Length >= root.Length)
            {
                string[] definitions;
                try
                {
                    definitions = System.IO.Directory.GetFiles(directory, "*.asmdef");
                }
                catch (System.Exception)
                {
                    couldNotRead = true;
                    return null;
                }

                if (definitions.Length > 0)
                {
                    System.Array.Sort(definitions, System.StringComparer.Ordinal);
                    try
                    {
                        return System.IO.File.ReadAllText(definitions[0]);
                    }
                    catch (System.Exception)
                    {
                        couldNotRead = true;
                        return null;
                    }
                }

                string[] references;
                try
                {
                    references = System.IO.Directory.GetFiles(directory, "*.asmref");
                }
                catch (System.Exception)
                {
                    couldNotRead = true;
                    return null;
                }

                if (references.Length > 0)
                {
                    System.Array.Sort(references, System.StringComparer.Ordinal);
                    return DefinitionNamed(root, ReferencedAssemblyName(references[0]));
                }

                if (string.Equals(directory, root, System.StringComparison.Ordinal)) return null;

                string parent = System.IO.Path.GetDirectoryName(directory);
                if (string.Equals(parent, directory, System.StringComparison.Ordinal)) return null;
                directory = parent;
            }

            return null;
        }

        /// <summary>
        /// Unity's own spelling of a file under the <c>Assets</c> folder — the
        /// folder's name, then the file's path under it, forward slashes —
        /// rather than a machine-specific absolute path: this is shown in a
        /// window and in a warning.
        /// </summary>
        internal static string ProjectPathOf(string dataPath, string file)
        {
            string under = file.Substring(dataPath.Length)
                .TrimStart(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
                .Replace(System.IO.Path.DirectorySeparatorChar, '/');
            return System.IO.Path.GetFileName(
                dataPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))
                + "/" + under;
        }

        /// <summary>
        /// The <c>.cs</c> files under <paramref name="root"/>, walked the way
        /// Unity imports: a file or folder Unity hides (<see cref="Hidden"/>)
        /// is not read or entered. The root that cannot be listed throws; a
        /// folder inside it that cannot be listed is counted and skipped. The
        /// whole walk happens in this call, so the list can be read more than
        /// once.
        /// </summary>
        internal static System.Collections.Generic.IEnumerable<string> ScriptsUnder(string root, ref int unreadable)
        {
            var files = new System.Collections.Generic.List<string>();
            var pending = new System.Collections.Generic.Stack<string>();
            pending.Push(root);
            bool atRoot = true;

            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                string[] scripts;
                string[] folders;
                try
                {
                    scripts = System.IO.Directory.GetFiles(directory);
                    folders = System.IO.Directory.GetDirectories(directory);
                }
                catch (System.Exception)
                {
                    if (atRoot) throw;
                    unreadable++;
                    continue;
                }
                finally
                {
                    atRoot = false;
                }

                foreach (string script in scripts)
                {
                    // Every file, the extension judged here: a `*.cs` pattern
                    // is case-sensitive on a case-sensitive file system, and
                    // Unity compiles `Reg.CS` wherever it runs.
                    string name = System.IO.Path.GetFileName(script);
                    if (name.EndsWith(".cs", System.StringComparison.OrdinalIgnoreCase) && !Hidden(name)) files.Add(script);
                }

                foreach (string folder in folders)
                {
                    if (!Hidden(System.IO.Path.GetFileName(folder))) pending.Push(folder);
                }
            }

            return files;
        }

        /// <summary>
        /// Unity's rule for an asset it never imports, file or folder alike: a
        /// name ending in <c>~</c>, starting with <c>.</c>, or <c>cvs</c> in
        /// any case — the tool that rule exists for writes <c>CVS</c>. A script
        /// under one is compiled by nothing, so it is not a registration a
        /// build could see.
        /// </summary>
        private static bool Hidden(string name)
            => name.EndsWith("~", System.StringComparison.Ordinal)
               || name.StartsWith(".", System.StringComparison.Ordinal)
               || string.Equals(name, "cvs", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether one script's code calls the registration: the token's two
        /// identifiers around a dot, followed by an argument list, outside every
        /// comment and literal — or the method alone under a <c>using static</c>
        /// of the type, or through an alias the file declares for it; a
        /// verbatim identifier (<c>@SetProvider</c>) is the identifier.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The shape is derived from <see cref="ProviderRegistration"/> rather
        /// than spelled a second time, so the token a message names and the
        /// token the walk looks for cannot part.
        /// </para>
        /// <para>
        /// ⚠️ The two respellings C# allows for a qualified call are read —
        /// <c>using static RTMPE.Core.ApiKeySource;</c> then <c>SetProvider(…)</c>,
        /// and <c>using Keys = RTMPE.Core.ApiKeySource;</c> then
        /// <c>Keys.SetProvider(…)</c> — because a project that registers this
        /// way and is told it does not would have its build warn every time,
        /// which is the warning somebody switches off. Unicode escapes in
        /// identifiers are decoded for the same reason — AFTER the comments
        /// and literals are gone, because the compiler decodes none inside a
        /// verbatim string (<c>@"\u0022"</c> is six characters of text) or a
        /// comment, and decoded ahead of the lexer that string became
        /// <c>@"""</c> and swallowed the file up to its next quote. A type of
        /// the developer's own named <c>ApiKeySource</c> with a
        /// <c>SetProvider</c> method reads as a registration; that is the
        /// reading of text this is.
        /// </para>
        /// </remarks>
        internal static bool RegistersAProvider(string source)
            => RegistersAProvider(source, out _);

        /// <summary>
        /// Whether the assembly definition at <paramref name="path"/> lists
        /// <paramref name="assemblyName"/> among its references.
        /// </summary>
        /// <param name="textOfGuid">
        /// Answers a <c>GUID:&lt;hex&gt;</c> reference with the referenced
        /// definition's TEXT, or null where it names nothing.
        /// </param>
        /// <remarks>
        /// ⛔ <see langword="false"/> for a definition that cannot be read and
        /// for a guid that resolves to nothing: the caller's note is a remedy
        /// for a compile error, and an unread file is no evidence that the
        /// remedy is unnecessary.
        ///
        /// <para>Unity writes a reference as the assembly's NAME or as its
        /// GUID, and the Inspector's default is the guid — so a reading of the
        /// names alone would answer "no reference" for most projects, which is
        /// the false warning this exists to end.</para>
        ///
        /// <para>⛔ The guid is answered with TEXT rather than with a path.  A
        /// path the asset database resolves is a project-relative one whose
        /// bytes need not be there: an SDK installed the shipped way — a
        /// <c>.tgz</c> through Package Manager — lives under
        /// <c>Library/PackageCache</c> and answers <c>Packages/com.rtmpe.sdk/…</c>,
        /// which <c>File.Exists</c> says nothing about.  Reading it through the
        /// asset database instead is the caller's to do, and this stays a reader
        /// of what it is handed.</para>
        /// </remarks>
        internal static bool AssemblyDefinitionReferences(
            string path, string assemblyName, System.Func<string, string> textOfGuid)
        {
            string json = ReadTextOrNull(path);
            if (json == null || string.IsNullOrEmpty(assemblyName)) return false;

            var references = System.Text.RegularExpressions.Regex.Match(
                json, "\"references\"\\s*:\\s*\\[(?<entries>[^\\]]*)\\]");
            if (!references.Success) return false;

            foreach (System.Text.RegularExpressions.Match entry
                in System.Text.RegularExpressions.Regex.Matches(
                    references.Groups["entries"].Value, "\"(?<value>[^\"]*)\""))
            {
                string value = entry.Groups["value"].Value;
                if (value == assemblyName) return true;

                if (!value.StartsWith("GUID:", System.StringComparison.Ordinal)) continue;
                if (textOfGuid == null) continue;

                string referenced;
                try
                {
                    referenced = textOfGuid(value.Substring("GUID:".Length));
                }
                catch (System.Exception)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(referenced)) continue;

                var named = System.Text.RegularExpressions.Regex.Match(
                    referenced, "\"name\"\\s*:\\s*\"(?<name>[^\"]*)\"");
                if (named.Success && named.Groups["name"].Value == assemblyName) return true;
            }

            return false;
        }

        // A file's text, or null when it is not there or cannot be read — which
        // is not evidence of anything.
        private static string ReadTextOrNull(string path)
        {
            try
            {
                return string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)
                    ? null
                    : System.IO.File.ReadAllText(path);
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The assembly an <c>.asmref</c> names, or <see langword="null"/> when
        /// it cannot be read.
        /// </summary>
        private static string ReferencedAssemblyName(string path)
        {
            string json;
            try
            {
                json = System.IO.File.ReadAllText(path);
            }
            catch (System.Exception)
            {
                return null;
            }

            var named = System.Text.RegularExpressions.Regex.Match(
                json, "\"reference\"\\s*:\\s*\"(?<name>[^\"]*)\"");
            if (!named.Success) return null;

            // Unity writes either the assembly's name or `GUID:<hex>`. A guid
            // names a file this walk cannot resolve without the asset
            // database, and an unresolvable reference is no evidence.
            string reference = named.Groups["name"].Value;
            return reference.StartsWith("GUID:", System.StringComparison.Ordinal) ? null : reference;
        }

        /// <summary>
        /// The assembly definition under <paramref name="root"/> whose
        /// <c>name</c> is <paramref name="name"/>, or <see langword="null"/>.
        /// </summary>
        private static string DefinitionNamed(string root, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            try
            {
                foreach (string path in System.IO.Directory.EnumerateFiles(
                    root, "*.asmdef", System.IO.SearchOption.AllDirectories))
                {
                    string json = System.IO.File.ReadAllText(path);
                    var declared = System.Text.RegularExpressions.Regex.Match(
                        json, "\"name\"\\s*:\\s*\"(?<name>[^\"]*)\"");
                    if (declared.Success && declared.Groups["name"].Value == name) return json;
                }
            }
            catch (System.Exception)
            {
                return null;
            }

            return null;
        }

        /// <summary>
        /// Whether <paramref name="source"/> registers a provider, and whether
        /// at least one of its registrations is in code a PLAYER build
        /// compiles.
        /// </summary>
        /// <remarks>
        /// 🔑 One reader for both, because they are one question asked twice:
        /// a second pass looking for the same call under a different rule would
        /// be a second spelling of the shapes above, and the two would drift on
        /// the first alias somebody writes.
        /// <para>
        /// ⛔ The conditional-compilation half is the file's own
        /// <c>#if</c> regions and nothing else — the folder the file sits in
        /// and the assembly definition above it are the caller's to ask, since
        /// neither is in the text.
        /// </para>
        /// </remarks>
        internal static bool RegistersAProvider(string source, out bool compiledIntoPlayers)
        {
            compiledIntoPlayers = false;
            if (source == null) return false;

            // The cheap question first: a file that never spells the method
            // name — not even escaped — cannot match, and most files never do.
            if (source.IndexOf(RegistrationMethod, System.StringComparison.Ordinal) < 0
                && source.IndexOf("\\u", System.StringComparison.Ordinal) < 0
                && source.IndexOf("\\U", System.StringComparison.Ordinal) < 0)
            {
                return false;
            }

            string code = UnicodeEscapesDecoded(CodeOnly(source)).Replace("@", string.Empty);
            bool found = false;

            foreach (int at in RegistrationSites(code))
            {
                found = true;
                if (PlayerBuildScope.SurvivesWithoutUnityEditor(code, at))
                {
                    compiledIntoPlayers = true;
                    return true;
                }
            }

            return found;
        }

        /// <summary>
        /// Where each registration sits in <paramref name="code"/> — the three
        /// spellings a call can take, in one enumeration so a caller asking
        /// about their position asks about all of them.
        /// </summary>
        private static System.Collections.Generic.IEnumerable<int> RegistrationSites(string code)
        {
            foreach (System.Text.RegularExpressions.Match call in RegistrationCall.Matches(code))
            {
                yield return call.Index;
            }

            if (UsingStaticOfTheType.IsMatch(code))
            {
                foreach (System.Text.RegularExpressions.Match bare in BareRegistrationCall.Matches(code))
                {
                    yield return bare.Index;
                }
            }

            foreach (System.Text.RegularExpressions.Match alias in AliasOfTheType.Matches(code))
            {
                var aliased = new System.Text.RegularExpressions.Regex(
                    @"\b" + System.Text.RegularExpressions.Regex.Escape(alias.Groups["alias"].Value)
                    + @"\s*\.\s*" + System.Text.RegularExpressions.Regex.Escape(RegistrationMethod)
                    + ArgumentsOfARegistration,
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                foreach (System.Text.RegularExpressions.Match call in aliased.Matches(code))
                {
                    yield return call.Index;
                }
            }
        }

        private static readonly string RegistrationType =
            ProviderRegistration.Substring(0, ProviderRegistration.IndexOf('.'));

        private static readonly string RegistrationMethod =
            ProviderRegistration.Substring(ProviderRegistration.IndexOf('.') + 1);

        /// <summary>The namespace the type lives in, for the two respellings a <c>using</c> allows.</summary>
        private const string RegistrationNamespace = "RTMPE.Core";

        /// <summary>
        /// The argument list that follows the method name — any but the ones
        /// that CLEAR the registration: <c>null</c> or <c>default</c>, bare,
        /// null-forgiven (<c>null!</c>), cast either way
        /// (<c>(Func&lt;string&gt;)null</c>, <c>null as Func&lt;string&gt;</c>),
        /// parenthesised, named (<c>provider: null</c>) or spelled
        /// <c>default(T)</c>. A clear is the opposite of registering, and
        /// reading it as one would turn the step green on a script that
        /// unregisters. An identifier that merely starts with one of the words
        /// (<c>nullProvider</c>) is an argument like any other.
        /// </summary>
        private const string ArgumentsOfARegistration =
            @"\s*\((?!\s*(?:\w+\s*:\s*)?\(?\s*(?:\([^()]*\)\s*)?\(?\s*(?:null|default(?:\s*\([^()]*\))?)\s*\)?\s*\)?\s*!?(?:\s+as\s+[^()]+?)?\s*\))";

        private static readonly System.Text.RegularExpressions.Regex RegistrationCall =
            new System.Text.RegularExpressions.Regex(
                @"\b" + System.Text.RegularExpressions.Regex.Escape(RegistrationType)
                + @"\s*\.\s*" + System.Text.RegularExpressions.Regex.Escape(RegistrationMethod)
                + ArgumentsOfARegistration,
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private static readonly System.Text.RegularExpressions.Regex BareRegistrationCall =
            new System.Text.RegularExpressions.Regex(
                @"(?<![\w.])" + System.Text.RegularExpressions.Regex.Escape(RegistrationMethod)
                + ArgumentsOfARegistration,
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private static readonly System.Text.RegularExpressions.Regex UsingStaticOfTheType =
            new System.Text.RegularExpressions.Regex(
                @"\busing\s+static\s+(?:global\s*::\s*)?" + QualifiedTypePattern() + @"\s*;",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private static readonly System.Text.RegularExpressions.Regex AliasOfTheType =
            new System.Text.RegularExpressions.Regex(
                @"\busing\s+(?<alias>\w+)\s*=\s*(?:global\s*::\s*)?" + QualifiedTypePattern() + @"\s*;",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary><c>RTMPE . Core . ApiKeySource</c>, whitespace free, as a pattern.</summary>
        private static string QualifiedTypePattern()
        {
            string[] parts = (RegistrationNamespace + "." + RegistrationType).Split('.');
            var pattern = new System.Text.StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) pattern.Append(@"\s*\.\s*");
                pattern.Append(System.Text.RegularExpressions.Regex.Escape(parts[i]));
            }

            return pattern.ToString();
        }

        /// <summary>
        /// <paramref name="source"/> with every <c>\uXXXX</c> and
        /// <c>\U0000XXXX</c> escape replaced by its character: C# admits them
        /// in identifiers, so <c>\u0041piKeySource</c> is the same call to the
        /// compiler. Applied to code alone — <see cref="CodeOnly"/> first —
        /// since inside a literal or a comment the compiler reads no
        /// identifier; and once, as the compiler does, so <c>\u005Cu0041</c>
        /// is a backslash and four characters, not an <c>A</c>.
        /// </summary>
        private static string UnicodeEscapesDecoded(string source)
            => System.Text.RegularExpressions.Regex.Replace(
                source, @"\\u([0-9A-Fa-f]{4})|\\U0000([0-9A-Fa-f]{4})",
                m => ((char)System.Convert.ToInt32(
                    (m.Groups[1].Success ? m.Groups[1] : m.Groups[2]).Value, 16)).ToString());

        /// <summary>
        /// <paramref name="source"/> with every comment removed and the contents
        /// of every string and character literal blanked, in ONE pass.
        /// </summary>
        /// <remarks>
        /// 🚨 One pass, because two expressions are beaten in either order: with
        /// comments stripped first a <c>//</c> inside a string ends the line and
        /// the code after it; with literals blanked first a <c>"</c> inside a
        /// block comment pairs with the next quote in code and swallows the
        /// comment's end. A scanner that knows which of the two it is inside
        /// cannot be desynchronised by either. Verbatim strings (<c>@"…"</c>,
        /// <c>$@"…"</c>, <c>@$"…"</c>) may span lines and double their quotes;
        /// ordinary and interpolated strings end at the line; an interpolation
        /// hole is blanked with its string — except that a string literal
        /// inside a hole ends the blanking at its OPENING quote: that literal's
        /// text is then read as code, and its closing quote is taken for the
        /// start of another literal, which blanks everything after it up to
        /// the string's own end — the rest of that hole and every later hole
        /// with it. A stated limit rather than a place a registration is
        /// written: what can surface as code is the text of a string nested in
        /// a hole, never the code around it. A directive line (<c>#region</c>,
        /// <c>#if</c>) is a message to the compiler and is skipped whole, so
        /// what it says cannot open a comment or a literal here; the arms of an
        /// <c>#if</c> are all read, as stated above.
        /// </remarks>
        internal static string CodeOnly(string source)
        {
            var sb = new System.Text.StringBuilder(source.Length);
            int n = source.Length;
            int i = 0;
            while (i < n)
            {
                char c = source[i];

                // A line comment.
                if (c == '/' && i + 1 < n && source[i + 1] == '/')
                {
                    while (i < n && source[i] != '\n') i++;
                    sb.Append(' ');
                    continue;
                }

                // A directive line, of which the KEYWORD and — for a
                // conditional — its expression are kept, and the rest dropped.
                //
                // ⛔ The rest has to go: `#region Auth /* helpers` is a message
                // to the compiler and was a comment opener here, and `#region
                // Don't` was a character literal. A conditional's EXPRESSION
                // cannot hold either — it is symbols, parentheses and operators
                // — and it is kept because a reader of this text has to be able
                // to tell which code a player build compiles, which is a
                // question only the conditionals answer.
                //
                // ⚠️ A directive may carry a trailing comment, and the
                // expression may not contain a '/' at all — so the kept span
                // ends at the first one. Keeping the whole line put
                // `#endif // ApiKeySource.SetProvider(…) is Editor-only here`
                // into the scanned text, where it read as a registration in a
                // region already closed: a file that registers nowhere earned
                // the step's ✅ and silenced the build's warning.
                if (c == '#')
                {
                    int lineEnd = i;
                    while (lineEnd < n && source[lineEnd] != '\n') lineEnd++;

                    int cursor = i + 1;
                    while (cursor < lineEnd && (source[cursor] == ' ' || source[cursor] == '\t')) cursor++;
                    int wordStart = cursor;
                    while (cursor < lineEnd && char.IsLetter(source[cursor])) cursor++;
                    string keyword = source.Substring(wordStart, cursor - wordStart);

                    if (keyword == "if" || keyword == "elif" || keyword == "else" || keyword == "endif")
                    {
                        int kept = i;
                        while (kept < lineEnd && source[kept] != '/') kept++;
                        sb.Append(source, i, kept - i);
                    }
                    else
                    {
                        sb.Append(' ');
                    }

                    i = lineEnd;
                    continue;
                }

                if (c == '/' && i + 1 < n && source[i + 1] == '*')
                {
                    int end = source.IndexOf("*/", i + 2, System.StringComparison.Ordinal);
                    i = end < 0 ? n : end + 2;
                    sb.Append(' ');
                    continue;
                }

                int verbatim = VerbatimPrefixLength(source, i);
                if (verbatim > 0)
                {
                    i += verbatim;
                    while (i < n)
                    {
                        if (source[i] == '"')
                        {
                            if (i + 1 < n && source[i + 1] == '"')
                            {
                                i += 2;
                                continue;
                            }

                            break;
                        }

                        i++;
                    }

                    i++;
                    sb.Append(' ');
                    continue;
                }

                if (c == '"' || (c == '$' && i + 1 < n && source[i + 1] == '"'))
                {
                    if (c == '$') i++;
                    i++;
                    while (i < n && source[i] != '"' && source[i] != '\n')
                    {
                        i += source[i] == '\\' && i + 1 < n ? 2 : 1;
                    }

                    i++;
                    sb.Append(' ');
                    continue;
                }

                if (c == '\'')
                {
                    i++;
                    while (i < n && source[i] != '\'' && source[i] != '\n')
                    {
                        i += source[i] == '\\' && i + 1 < n ? 2 : 1;
                    }

                    i++;
                    sb.Append(' ');
                    continue;
                }

                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }

        private static int VerbatimPrefixLength(string source, int i)
        {
            int n = source.Length;
            if (i + 1 < n && source[i] == '@' && source[i + 1] == '"') return 2;
            if (i + 2 < n && source[i] == '$' && source[i + 1] == '@' && source[i + 2] == '"') return 3;
            if (i + 2 < n && source[i] == '@' && source[i + 1] == '$' && source[i + 2] == '"') return 3;
            return 0;
        }

        /// <summary>What the warning says.</summary>
        internal static string Message()
            => "[RTMPE] This release build has no API key source that a build can see. "
             + "No script THIS BUILD COMPILES calls " + ProviderRegistration + " — one under an "
             + "Editor folder, one in an assembly definition built for the Editor alone or "
             + "constrained to test builds, and one inside #if UNITY_EDITOR all register in Play "
             + "mode and are in no player — and a release "
             + "build carries no key of its own, so the player will authenticate only if "
             + "it is launched with --rtmpe-api-key-file <path> or --rtmpe-api-key <key>, "
             + "or with RTMPE_API_KEY set — and a double-clicked game on a player's machine has "
             + "none of those set. If this build is for a person to open, register a provider with "
             + ProviderRegistration + " before the connection starts; if it is launched by "
             + "a script or a service, this is expected and the build is fine.";
    }
}
