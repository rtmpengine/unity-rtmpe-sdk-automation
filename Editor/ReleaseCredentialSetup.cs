// RTMPE SDK — Editor/ReleaseCredentialSetup.cs
//
// The key a RELEASE build will have: what the Setup Wizard's production step
// says about it, and the provider script the step writes.
//
// The wizard's vault is the Editor's, and no build carries it.  A project the
// wizard has fully configured therefore produces a player with no credential,
// and the sources that remain are unequal: a provider registered in the
// project's own code reaches every player, while a file named on the launch
// line reaches only a process somebody launched that way and an environment
// variable only a machine somebody set it on — neither of which is a player's.  ReleaseCredentialPathAdvisory says
// so at build time; this step says it before the build, where the answer can
// still be chosen, and writes the one source a shipped game has.
//
// ⛔ The script it writes declares NO serialized field, and must not acquire
// one: Unity writes a serialized string into the scene asset — committed, and
// inside every build made from it — and it serializes a public field as
// readily as one marked [SerializeField].  The key travels through a static
// field the developer's own code fills, which Unity never serializes.
//
// ⛔ The decisions and the wording are here, pure, because the window that
// draws them is compiled by no test project: a rule about a step is a rule
// about its text, and the answer it gives is worth more than its shape.

namespace RTMPE.Editor
{
    internal static class ReleaseCredentialSetup
    {
        /// <summary>The class the written script declares — and, as Unity requires, its file name.</summary>
        internal const string ClassName = "RtmpeCredentialProvider";

        /// <summary>The written script's file name.</summary>
        internal const string FileName = ClassName + ".cs";

        /// <summary>
        /// Where the script is written — the folder the wizard already owns for
        /// the <c>NetworkSettings</c> asset it creates.
        /// </summary>
        internal const string ProjectFolder = "Assets/RTMPE";

        /// <summary>The written script, as an asset path — what a message shows.</summary>
        internal const string ProjectPath = ProjectFolder + "/" + FileName;

        /// <summary>
        /// Where the script actually is, derived from the project's own
        /// <c>Application.dataPath</c>; <see langword="null"/> for no path.
        /// </summary>
        internal static string AbsolutePath(string dataPath)
            => string.IsNullOrEmpty(dataPath)
                ? null
                : System.IO.Path.Combine(dataPath, "RTMPE", FileName);

        /// <summary>One source a player build can read its key from, as the step lists it.</summary>
        internal sealed class Source
        {
            internal Source(string name, string spelling, string use)
            {
                Name = name;
                Spelling = spelling;
                Use = use;
            }

            /// <summary>What the step calls it.</summary>
            internal string Name { get; }

            /// <summary>The call, option or variable, as the developer types it.</summary>
            internal string Spelling { get; }

            /// <summary>Who it reaches — the getting-started guide's own words.</summary>
            internal string Use { get; }
        }

        /// <summary>
        /// The three sources, in the order <c>ApiKeySource</c> consults them and
        /// with the reach the getting-started guide gives each.
        /// </summary>
        /// <remarks>
        /// ⛔ Three of the five rows of the guide's table under "Giving a player
        /// build its API key", in its order and with its words, held to it by a
        /// test: a step that ranked the sources differently from the document a
        /// developer reads next would be an instruction to debug a value that is
        /// never consulted. The two rows left out belong elsewhere — the staged
        /// development key is the previous step's control, and the argv form
        /// of the option is the file form's cousin for a machine nobody shares,
        /// which the guide names beside it.
        /// </remarks>
        internal static readonly Source[] Sources =
        {
            new Source(
                "A provider in your code",
                ReleaseCredentialPathAdvisory.ProviderRegistration,
                "the only one that reaches a player who just double-clicked the game you "
                + "shipped — the script this step writes is one"),
            new Source(
                "A file named on the launch line",
                RTMPE.Core.ApiKeySource.CommandLineFileOption + " <path>",
                "your own machine, a test rig, CI, a launcher you control"),
            new Source(
                "An environment variable",
                RTMPE.Core.ApiKeySource.EnvironmentVariableName,
                "the same — set for your whole session it reaches a build you double-clicked "
                + "yourself, but that is a setting on your machine and never on a player's"),
        };

        /// <summary>
        /// What the step says at the top, given every registration the
        /// project's scan found (empty for none), the ones a PLAYER build
        /// compiles, how many scripts it could not read, and whether it could
        /// look at all.
        /// </summary>
        /// <remarks>
        /// ⛔ The two lists are not the same list, and the step's whole claim
        /// is about the second. A script under an <c>Editor</c> folder, one in
        /// an editor-only assembly definition, and a call inside
        /// <c>#if UNITY_EDITOR</c> all register in Play mode and none of them
        /// is in the build — so a ✅ drawn from the first list is a sentence
        /// about Play mode wearing a build's clothes, and the build it promised
        /// comes out with no key source and no warning.
        /// <para>
        /// ⛔ A scan that could not finish is reported as exactly that, never
        /// as either answer: the build hook claims a provider on the same
        /// failure because a hook must not invent a fault, but a step is a
        /// conversation, and "I could not look" is the true sentence here. A
        /// script skipped inside a scan that did finish is named by count for
        /// the same reason: the answer stands for the files that were read.
        /// </para>
        /// </remarks>
        internal static string Status(
            string[] registrations, string[] compiledIntoPlayers, int unreadable, bool scanFailed)
        {
            if (scanFailed)
            {
                return "⛔ RTMPE could not read this project's scripts, so it cannot say what a "
                     + "release build will authenticate with. The scan looks under Assets/ for "
                     + "a call to " + ReleaseCredentialPathAdvisory.ProviderRegistration
                     + "; make the folder readable and press Re-check.";
            }

            // Scripts AND folders — the walk counts a folder it could not
            // list as one, whatever it held — so the count is named as both.
            string skipped = unreadable == 0
                ? string.Empty
                : " " + unreadable + (unreadable == 1 ? " script or folder" : " scripts or folders")
                  + " under Assets/ could not be read and " + (unreadable == 1 ? "was" : "were")
                  + " skipped; the answer stands for the rest.";

            if (compiledIntoPlayers != null && compiledIntoPlayers.Length > 0)
            {
                return "✅ A provider registration was found in " + compiledIntoPlayers[0] + ", so a "
                     + "release build will see it and the build will not warn. It registers only "
                     + "when the code that calls it runs: a component has to be on an active object "
                     + "in the scene that connects — the object carrying the NetworkManager is the "
                     + "right one — or the registration never happens." + skipped;
            }

            if (registrations != null && registrations.Length > 0)
            {
                return "⚠️ " + registrations[0] + " registers a provider, and a release build does "
                     + "not compile it: a script under an Editor folder, one in an assembly "
                     + "definition that names the Editor as its only platform or carries the "
                     + "UNITY_INCLUDE_TESTS constraint, and a call inside #if UNITY_EDITOR are all "
                     + "Editor-only — they register in Play mode and are absent from the player. "
                     + "Move the registration into a script the build compiles, or choose a source "
                     + "below." + skipped;
            }

            return "⚠️ A release build of this project has no API key source a build can see: "
                 + "nothing under Assets/ calls " + ReleaseCredentialPathAdvisory.ProviderRegistration
                 + " (a package of your own under Packages/, or a DLL, is outside this scan). "
                 + "Play mode works because the Editor reads the vault the previous step "
                 + "wrote to, and no build carries that vault — so a release build will "
                 + "connect only if whoever launches it names a key on the launch line ("
                 + RTMPE.Core.ApiKeySource.CommandLineFileOption + " <path>) or sets "
                 + RTMPE.Core.ApiKeySource.EnvironmentVariableName + " first, and on a "
                 + "player's machine nothing has set either. If this build is launched by a "
                 + "script or a service, that is expected and the build is fine; if it is for "
                 + "a person to open, choose a source below." + skipped;
        }

        /// <summary>
        /// What the step says above its script when other scripts than the
        /// wizard's own register a provider — before the write, and after it;
        /// empty when there are none, which is when the step draws nothing.
        /// </summary>
        /// <remarks>
        /// ⛔ The SDK keeps ONE registration — <c>ApiKeySource.SetProvider</c>
        /// is a single slot — so a second component registering in
        /// <c>Awake</c> replaces the first, and which one is asked is decided
        /// by the order two <c>Awake</c>s happened to run. Offering the button
        /// in silence beside an imported sample that already registers would
        /// invite exactly that, with the developer's <c>Supply</c> going to the
        /// component the SDK is no longer asking — and the moment the wizard's
        /// script exists beside such a sample is the moment two registrations
        /// exist, so the note stays up after the write too.
        /// </remarks>
        internal static string AnotherRegistrationNote(string[] otherRegistrations)
        {
            if (otherRegistrations == null || otherRegistrations.Length == 0) return string.Empty;

            bool several = otherRegistrations.Length > 1;
            return (several
                       ? "These scripts each register a provider: " + string.Join(", ", otherRegistrations) + "."
                       : otherRegistrations[0] + " already registers a provider.")
                 + " The SDK keeps one registration and the last Awake to run replaces the others, "
                 + "so if more than one such component runs in the scene that connects, only one of "
                 + "them is asked — keep one provider in that scene. If "
                 + (several ? "one is" : "it is") + " a sample you are about to remove, remove it "
                 + "and press Re-check.";
        }

        /// <summary>
        /// Said in place of the placement when the wizard's script is in the
        /// project but its code no longer calls the registration: wherever the
        /// component sits, it registers nothing, and a ✅ about where it sits
        /// would be a false one — the scan has the fact in hand.
        /// </summary>
        internal static string ScriptRegistersNothingNote(string script)
            => "⚠️ " + script + " no longer calls " + ReleaseCredentialPathAdvisory.ProviderRegistration
             + " in its code, so wherever its component sits, it registers nothing. Put the call back "
             + "in its Awake — " + ReleaseCredentialPathAdvisory.ProviderRegistration + "(ProvideKey) — or "
             + "register a provider from a component of your own, and press Re-check.";

        /// <summary>Where the written component stands, once the file is there.</summary>
        internal enum Placement
        {
            /// <summary>The script is written and Unity is compiling it.</summary>
            Compiling,

            /// <summary>The script is written and Unity has not produced its type.</summary>
            NotCompiled,

            /// <summary>The type exists and the scene has no NetworkManager to carry it.</summary>
            NoManager,

            /// <summary>The type exists and no active object in the open scene carries it.</summary>
            NotOnManager,

            /// <summary>The component is on the NetworkManager's object.</summary>
            OnManager,

            /// <summary>
            /// The type exists and is no longer a component, so nothing can
            /// carry it and its <c>Awake</c> never runs — the file was edited
            /// into something else.
            /// </summary>
            NotAComponent,

            /// <summary>
            /// The component is on an active object that is not the
            /// NetworkManager's — the developer's own placement, which the
            /// guide allows; said rather than "on no object", and no second
            /// copy is offered.
            /// </summary>
            OnAnotherObject,
        }

        /// <summary>
        /// How the key reaches the component when the Connection Bootstrap from
        /// step 3 connects on Start — the one sentence that reconciles "before
        /// anything connects" with the wizard's own default.
        /// </summary>
        internal const string SupplyBeforeTheBootstrapConnects =
            "With the Connection Bootstrap from step 3 connecting on Start, call Supply from an "
            + "Awake, or clear its Connect On Start and call its Connect() after Supply.";

        /// <summary>
        /// What the step says about the component's placement, given whether a
        /// release build compiles the script at all;
        /// <paramref name="carrier"/> names the object for
        /// <see cref="Placement.OnAnotherObject"/>.
        /// </summary>
        /// <remarks>
        /// ⚠️ A text scan cannot see any of this — the build's does not try —
        /// so a step that stopped at "the file is there" would be green over a
        /// component nothing ever runs.
        /// <para>
        /// ⛔ <paramref name="compiledIntoPlayers"/> is why this is not the
        /// placement alone. The two ✅ answers below end by promising what a
        /// RELEASE BUILD authenticates with, and the box above this one may
        /// have just said the build does not compile the script — an Editor
        /// folder, an editor-only assembly definition, a
        /// <c>#if UNITY_EDITOR</c> around the registration. One step said both,
        /// and the second sentence was the one a developer acted on.
        /// </para>
        /// </remarks>
        internal static string PlacementStatus(
            Placement placement, bool compiledIntoPlayers, string carrier = null)
        {
            string build = compiledIntoPlayers
                ? " and a release build authenticates with what you supplied."
                : " ⚠️ A release build does not compile this script, so what you supply reaches Play "
                  + "mode and no build — see the answer at the top of this step.";

            switch (placement)
            {
                case Placement.OnAnotherObject:
                    return "✅ " + ClassName + " is on \"" + carrier + "\", not on the NetworkManager — "
                         + "fine while that object is active in the scene that connects, since the "
                         + "registration runs from its Awake. Call " + ClassName + ".Supply(key) from "
                         + "your own code before anything connects" + build + " "
                         + SupplyBeforeTheBootstrapConnects;
                case Placement.Compiling:
                    return "Unity is compiling " + ClassName + " — this step updates when that finishes.";
                case Placement.NotCompiled:
                    return "Unity has not compiled " + ClassName + " from that file yet. If the Console "
                         + "shows a compile error, fix it; the file also has to declare a class of its "
                         + "own name, which is what Unity binds a component to. This step updates on "
                         + "the reload that follows.";
                case Placement.NoManager:
                    return "Add a NetworkManager on step 2 first — the provider goes on its object, "
                         + "which is in the scene that connects and outlives scene loads.";
                case Placement.NotAComponent:
                    return "⛔ " + ClassName + " is compiled but is no longer a MonoBehaviour, so it "
                         + "cannot be placed on an object and its Awake — the registration — never "
                         + "runs. Make it a MonoBehaviour again, or register a provider from a "
                         + "component of your own.";
                case Placement.OnManager:
                    return "✅ " + ClassName + " is on the NetworkManager. Call " + ClassName
                         + ".Supply(key) from your own code before anything connects" + build + " "
                         + SupplyBeforeTheBootstrapConnects
                         + " The SDK keeps one registration, so no other component in that scene "
                         + "should register a provider.";
                default:
                    return ClassName + " is compiled but on no active object in the open scene: its "
                         + "registration runs from Awake, so it has to be on one in the scene that "
                         + "connects.";
            }
        }

        /// <summary>
        /// Said beside a component placed on a NetworkManager that is a prefab
        /// instance: the press made an override of this scene's instance, and
        /// another scene using the prefab has no provider until the override
        /// is applied.
        /// </summary>
        internal const string PrefabInstanceNote =
            "The NetworkManager here is a prefab instance, so the component is an override of "
            + "this scene's copy: apply it to the prefab if other scenes use the same prefab.";

        /// <summary>The assembly the SDK's runtime compiles into.</summary>
        /// <remarks>
        /// One spelling, read by the note and by the question behind it, so a
        /// project that has done what the note asks cannot be told to do it
        /// again over a renamed assembly.
        /// </remarks>
        internal const string RuntimeAssemblyName = "RTMPE.SDK.Runtime";

        /// <summary>
        /// Said after the write when an assembly definition governs the folder
        /// and does NOT reference the SDK: Unity compiles the script into that
        /// assembly, which sees the SDK only if it references it — a compile
        /// error the step would otherwise only call "a compile error".
        /// </summary>
        /// <returns>
        /// <see langword="null"/> where the definition already references the
        /// runtime, and the script therefore compiles.
        /// </returns>
        /// <remarks>
        /// ⛔ The references are READ.  Said unconditionally, this told a
        /// project that had already added the reference to add it — and the
        /// step marked its own outcome unsettled for it, so a correct setup
        /// ended on a warning in the console. A note nobody needs is a note
        /// everybody learns to skip, including on the day it is true.
        ///
        /// <para>Unity writes a reference as the assembly's name or as
        /// <c>GUID:&lt;hex&gt;</c> — the Inspector's default is the guid — so both
        /// are resolved. A definition that cannot be read, and a guid that
        /// resolves to nothing, leave the note standing: the compile error is
        /// the worse outcome of the two.</para>
        /// </remarks>
        internal static string AssemblyDefinitionNote(
            string assemblyDefinitionPath, bool alreadyReferencesTheRuntime)
            => alreadyReferencesTheRuntime
                ? null
                : "⚠️ " + assemblyDefinitionPath + " governs this folder, so the script compiles into "
                    + "that assembly: add a reference to " + RuntimeAssemblyName + " in it, or the "
                    + "script does not compile.";

        /// <summary>
        /// The alert the step carries while a development build of this
        /// project is being given the key.
        /// </summary>
        /// <remarks>
        /// Shown only while the injection is switched on: that is the one case
        /// in which a development build carries the key by design, and the
        /// step is where "I will just send the tester a development build" is
        /// decided. With the switch off a development build has no more key
        /// than a release one, and the alert would be noise.
        /// </remarks>
        internal static string DevelopmentBuildHandedOverWarning()
            => "⛔ Do not hand a development build of this project to anyone while \""
             + DevelopmentApiKeyStaging.StageButton + "\" is on: that build carries this "
             + "project's API key by design, so giving it to a tester, a publisher or a store "
             + "gives them the key. Anything that leaves your desk is built without Development "
             + "Build — it then has no key to leak, and reads one from the source you choose here.";

        /// <summary>What writing the provider script did.</summary>
        internal enum Write
        {
            /// <summary>The script was written; Unity has yet to compile it.</summary>
            Written,

            /// <summary>The file is already in the project, and it is left as it is.</summary>
            AlreadyThere,

            /// <summary>
            /// A type of that name is compiled from somewhere other than a
            /// script of that name under <c>Assets/</c> — a DLL, or a class
            /// declared inside another file; a second declaration would not
            /// compile.
            /// </summary>
            NameTaken,

            /// <summary>The file could not be written.</summary>
            CouldNotWrite,
        }

        /// <summary>
        /// The wizard's script wherever it is under <paramref name="dataPath"/>,
        /// as the path Unity shows (<c>Assets/…</c>), or <see langword="null"/>:
        /// at <see cref="ProjectPath"/> when it is there, else the first script
        /// of that name the walk finds, in ordinal order.
        /// </summary>
        /// <remarks>
        /// 🔑 The script is the DEVELOPER's from the moment it is written, and
        /// moving a file is routine; Unity requires a MonoBehaviour's file to
        /// carry its class name, so the name is what follows it. Bound to the
        /// path alone, the step contradicted itself after a move — "found in
        /// Assets/Scripts/…" above a refusal to write "beside a type compiled
        /// from somewhere else". Folders Unity never imports are not walked,
        /// and a folder that cannot be read is skipped, both as the
        /// registration walk does.
        /// </remarks>
        internal static string ScriptPathIn(string dataPath)
        {
            if (string.IsNullOrEmpty(dataPath)) return null;
            if (System.IO.File.Exists(AbsolutePath(dataPath))) return ProjectPath;

            int unreadable = 0;
            return ScriptPathAmong(dataPath, ReleaseCredentialPathAdvisory.ScriptsUnder(dataPath, ref unreadable));
        }

        /// <summary>
        /// <see cref="ScriptPathIn"/> read off a walk already taken —
        /// <paramref name="scripts"/> as <c>ScriptsUnder</c> lists them — so the
        /// wizard, which asks the registration question of the same list, walks
        /// the project once per scan.
        /// </summary>
        internal static string ScriptPathAmong(string dataPath, System.Collections.Generic.IEnumerable<string> scripts)
        {
            if (string.IsNullOrEmpty(dataPath)) return null;

            var named = new System.Collections.Generic.List<string>();
            foreach (string script in scripts)
            {
                if (string.Equals(System.IO.Path.GetFileName(script), FileName, System.StringComparison.Ordinal))
                    named.Add(ReleaseCredentialPathAdvisory.ProjectPathOf(dataPath, script));
            }

            if (named.Count == 0) return null;
            if (named.Contains(ProjectPath)) return ProjectPath;
            named.Sort(System.StringComparer.Ordinal);
            return named[0];
        }

        /// <summary>
        /// Write the provider script into the project, and answer what that did.
        /// </summary>
        /// <remarks>
        /// <para>
        /// ⛔ Never over an existing file, whatever it holds: the script is the
        /// developer's the moment it is written — the guide tells them to edit
        /// it — and a wizard that rewrote it on a second press would take their
        /// fetch with it. A script of that name anywhere else under
        /// <c>Assets/</c> is the same file moved, and is left where it is.
        /// </para>
        /// <para>
        /// ⛔ And never beside a type that already carries the name from
        /// somewhere else.  Unity compiles every script under Assets/ into one
        /// assembly unless told otherwise, so a second
        /// <c>RtmpeCredentialProvider</c> is a compile error that stops every
        /// script in the project, on a press that was meant to help.  The
        /// caller answers whether one is compiled, because that is a question
        /// about loaded assemblies and this file asks none.
        /// </para>
        /// </remarks>
        /// <param name="dataPath">The project's <c>Application.dataPath</c>.</param>
        /// <param name="typeOfThatNameIsCompiled">
        /// Whether a type named <see cref="ClassName"/> is already compiled in the
        /// project — asked only when no script of that name is under
        /// <c>Assets/</c>, since a script being there is the ordinary way for
        /// the type to be compiled.
        /// </param>
        /// <param name="newLine">
        /// The line ending the project writes new scripts with — the Editor's
        /// setting, so the file opens in the developer's IDE without a
        /// "mixed line endings" prompt; <see langword="null"/> keeps the
        /// template's own.
        /// </param>
        internal static Write WriteProvider(string dataPath, bool typeOfThatNameIsCompiled, string newLine = null)
        {
            string path = AbsolutePath(dataPath);
            if (path == null) return Write.CouldNotWrite;

            string existing;
            try
            {
                existing = ScriptPathIn(dataPath);
            }
            catch (System.Exception)
            {
                // The Assets folder itself could not be read (a folder inside it
                // is skipped, never thrown). Nothing can be said about what is
                // there, and a write into a folder that cannot be listed is the
                // same fault by another name — said as the write's own outcome,
                // never as a name that was found.
                return Write.CouldNotWrite;
            }

            if (existing != null) return Write.AlreadyThere;
            if (typeOfThatNameIsCompiled) return Write.NameTaken;

            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, WithLineEndings(ProviderScript, newLine));
                return Write.Written;
            }
            catch (System.Exception)
            {
                // ⛔ By outcome, never by exception: a file API quotes the path
                // it was handed, and the developer is going to be shown this.
                return Write.CouldNotWrite;
            }
        }

        /// <summary>
        /// <paramref name="text"/> with every line ending as
        /// <paramref name="newLine"/>; unchanged for <see langword="null"/>.
        /// </summary>
        internal static string WithLineEndings(string text, string newLine)
        {
            if (newLine == null) return text;
            return text.Replace("\r\n", "\n").Replace("\n", newLine);
        }

        /// <summary>What the step says beside the button after a write.</summary>
        internal static string WriteStatus(Write outcome)
        {
            switch (outcome)
            {
                case Write.Written:
                    return "Wrote " + ProjectPath + ". Unity is compiling it; when that finishes, "
                         + "add it to the object carrying your NetworkManager — the button "
                         + "appears here — and call " + ClassName + ".Supply(key) from your own "
                         + "code, with the key your launcher or backend obtained, before anything "
                         + "connects. " + SupplyBeforeTheBootstrapConnects;
                case Write.AlreadyThere:
                    return "A script named " + FileName + " is already in the project, and the wizard "
                         + "does not overwrite it — it is yours to edit.";
                case Write.NameTaken:
                    return "⛔ A type named " + ClassName + " is already compiled in this project "
                         + "from somewhere other than a script of that name under Assets/ — a DLL, "
                         + "or a class declared inside another file — and a second declaration would "
                         + "not compile. Use yours — it needs a call to "
                         + ReleaseCredentialPathAdvisory.ProviderRegistration
                         + " — or rename it and press again.";
                default:
                    return "⛔ RTMPE could not write " + ProjectPath + ". Create the provider by "
                         + "hand: the getting-started guide shows it under \"Giving a player build "
                         + "its API key\", and the Two Player Room sample ships it as "
                         + "TwoPlayerCredentials.";
            }
        }

        /// <summary>
        /// The script the step writes: the Two Player Room sample's
        /// <c>TwoPlayerCredentials</c>, without the sample's readout and under a
        /// name of its own, so importing that sample later does not declare the
        /// class twice.
        /// </summary>
        /// <remarks>
        /// ⛔ Held by tests to the properties that make it safe rather than to
        /// the sample's bytes: it declares no instance field, its one field is
        /// static, it registers through the token the build scan looks for, it
        /// asks <c>IsAvailable</c> and never <c>TryResolve</c>, it never logs
        /// the key, and it compiles against the real <c>ApiKeySource</c>.
        /// </remarks>
        internal const string ProviderScript =
@"// RtmpeCredentialProvider.cs — written by Window > RTMPE > Setup Wizard.
//
// Supplies the RTMPE SDK with the API key your own code obtained, from outside
// every scene and prefab asset. The wizard never overwrites this file; edit it
// freely.
//
// The key the Setup Wizard stores is read only in the Editor, so a player build
// needs a source of its own. This component registers a provider with
// ApiKeySource before anything connects, and your code hands it the key with
// RtmpeCredentialProvider.Supply(key). Where the key comes from — a launcher,
// your account backend, a file beside the player — is up to your game.

using RTMPE.Core;
using UnityEngine;

/// <summary>
/// Registers this project's API key source with the SDK, and takes the key
/// from your own code through <see cref=""Supply""/>.
/// </summary>
public sealed class RtmpeCredentialProvider : MonoBehaviour
{
    // Static, so the key is never serialised into a scene or prefab asset, and
    // so Supply may be called before the scene carrying this component loads.
    private static string s_key = string.Empty;

    private void Awake()
    {
        // Register first: every Awake in a scene runs before any Start, and the
        // Connection Bootstrap connects from Start.
        ApiKeySource.SetProvider(ProvideKey);

        // IsAvailable reports whether any source has a key without exposing it.
        if (ApiKeySource.IsAvailable()) return;

        // Expected when your code supplies the key later, before connecting.
        Debug.Log(
            ""[RtmpeCredentialProvider] No API key is in hand yet. If your code calls ""
            + ""RtmpeCredentialProvider.Supply(key) before connecting, this is expected; ""
            + ""otherwise nothing will connect""
            + (Application.isEditor
                ? "" — in the Editor, store one with Window > RTMPE > Setup Wizard: this ""
                  + ""component asked that vault and it had none.""
                : "" unless this player is launched with "" + ApiKeySource.CommandLineFileOption
                  + "" <path> or with "" + ApiKeySource.EnvironmentVariableName + "" set."")
            + (ApiKeySource.LastError == null
                ? string.Empty
                : "" A configured source failed: "" + ApiKeySource.LastError.Message));
    }

    /// <summary>The provider registered with the SDK.</summary>
    /// <remarks>
    /// An empty string means ""no key"", and the SDK asks the next source. An
    /// exception ends resolution and is reported as a failed source.
    /// </remarks>
    private static string ProvideKey() => s_key;

    /// <summary>
    /// Hands the SDK the key your own code obtained. Call it before a connection
    /// is attempted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The SDK asks a provider for a <c>string</c> and does not wait for one, so
    /// fetch the key first, call <see cref=""Supply""/> with it, and then connect.
    /// With RtmpeConnectionBootstrap, clear its Connect On Start option and call
    /// its Connect() after this.
    /// </para>
    /// <para>
    /// A backend that releases the key this way should release it only to
    /// players it has authenticated.
    /// </para>
    /// </remarks>
    /// <param name=""key"">
    /// The key. <c>null</c> is stored as empty, the same as never calling this.
    /// </param>
    public static void Supply(string key)
    {
        s_key = key ?? string.Empty;
    }
}
";
    }
}
