// RTMPE SDK — Editor/NetworkPrefabsWindow.cs
//
// The maintainer's view of the spawn prefab ledger. Open via:
// Window > RTMPE > Network Prefabs.
//
// Design constraints:
//  • THIN. Every decision this window appears to make is made in
//    NetworkPrefabsInventory or PrefabIdLedger, both of which compile and are
//    tested without an editor. What is left here is the editor's own vocabulary:
//    where the file sits, what the user selected, and how a failure is worded.
//    The rule is worth stating because the opposite is the default — a status
//    computed inline in OnGUI is a rule no test can reach.
//  • FAIL-CLOSED ON A LEDGER IT CANNOT READ. A file that will not parse is
//    reported and nothing is offered but a reload. Writing a fresh ledger over
//    one that failed to open would destroy every id the project's peers already
//    hold, and the failure that makes it tempting — a merge conflict — is
//    precisely when those ids matter.
//  • Every mutation is a read-modify-write of the whole file through
//    PrefabIdLedger.Serialize, and the reload afterwards is from disk, so what
//    the window shows is what a teammate will pull.
//
// ⛔ No Documentation control, unlike the Readiness window and the Conversion
// Wizard, and the difference is deliberate rather than an omission. Those two
// front a command-line pipeline with prerequisites — a .NET 8 SDK, a repository
// checkout — that cannot fit in a banner, so a reader who is stuck needs a page.
// Everything this window relies on is four sentences: what the ledger is, that
// it belongs in version control, what retiring an id costs, and where a
// duplicate comes from.
//
// 🚨 Three of the four used to appear only in a state the reader may never be
// in — the version-control line drew ONLY when no ledger existed, and the cost
// of retiring lived only inside the modal you had to have triggered already, so
// a healthy project saw one sentence and the claim above was false. The first
// two are now in the steady-state banner and the last two are where the
// decision is made. The prose form is in getting-started.md, which the reader
// arrives at through the SDK's own troubleshooting entry rather than through a
// control here.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RTMPE.Core;
using UnityEditor;
using UnityEngine;

namespace RTMPE.Editor
{
    /// <summary>
    /// Editor window over <c>rtmpe-prefabs.json</c>: what is registered, what
    /// needs a decision, and the generated constants a project spawns through.
    /// </summary>
    public sealed class NetworkPrefabsWindow : EditorWindow
    {
        private const string PrefPrefix = "RTMPE.Prefabs.";

        private PrefabLedgerDocument _ledger;
        private PrefabInventory _inventory;

        // Distinct from "no ledger yet": an absent file is an empty project and a
        // safe place to start, an unreadable one is a file somebody must look at.
        private string _loadError;
        private bool _ledgerExists;
        private bool _readForRepair;

        // ⚠️ In SessionState rather than in fields. Generate writes a .cs into
        // Assets/ and imports it, which compiles and reloads the domain — so a
        // plain field holding the outcome of that action is wiped by the action
        // itself, and the user is left with a blank window instead of "wrote …".
        private string Status
        {
            get => SessionState.GetString(PrefPrefix + "Status", string.Empty);
            set => SessionState.SetString(PrefPrefix + "Status", value ?? string.Empty);
        }

        private bool StatusIsError
        {
            get => SessionState.GetBool(PrefPrefix + "StatusIsError", false);
            set => SessionState.SetBool(PrefPrefix + "StatusIsError", value);
        }
        // Taken beside the inventory, from the same asset-database snapshot, and
        // replaced whenever it is. ⛔ Never null: a window that has not loaded
        // anything has nothing to advise about, which is an empty list rather
        // than a state every reader has to guard.
        private IReadOnlyList<PrefabMotionAdvisory> _motion = Array.Empty<PrefabMotionAdvisory>();

        private DateTime _writeTimeUtc;
        private Vector2 _scroll;
        private string _generatedNamespace;

        // ── Entry point ─────────────────────────────────────────────────────────

        [MenuItem("Window/RTMPE/Network Prefabs")]
        public static void Open()
        {
            var win = GetWindow<NetworkPrefabsWindow>(false, "RTMPE Prefabs", true);
            win.minSize = new Vector2(520, 360);
            win.Show();
        }

        // ── Lifecycle ───────────────────────────────────────────────────────────

        private void OnEnable()
        {
            _generatedNamespace = SessionState.GetString(
                PrefPrefix + "Namespace", NetworkPrefabsInventory.DefaultNamespace);
            Load();
        }

        private void OnDisable()
        {
            SessionState.SetString(PrefPrefix + "Namespace", _generatedNamespace ?? string.Empty);
        }

        // A ledger is a committed file, so the common way it changes is a pull or
        // a merge in another window — neither of which this process performs.
        private void OnFocus()
        {
            // ⚠️ Existence is half the question. Watching the write stamp alone
            // keeps showing the registrations of a ledger that has since been
            // deleted — and the next allocation would then write a fresh file
            // reissuing ids that peers already hold.
            bool exists = File.Exists(LedgerPath);
            if (exists != _ledgerExists
                || (exists && File.GetLastWriteTimeUtc(LedgerPath) != _writeTimeUtc))
            {
                Load();
            }
        }

        // Unity sends this when the Project or Hierarchy selection changes. Without
        // it a docked window keeps offering "— select a prefab —" after the user
        // has selected one, until the pointer happens to enter it: the selection
        // is state this window reads and does not own.
        private void OnSelectionChange() => Repaint();

        // ⚠️ And this when assets are added, deleted, moved or reimported. The
        // inventory is a SNAPSHOT of the asset database, so without it a deleted
        // prefab keeps rendering as registered, the Missing banner never appears,
        // and Generate writes that id into the constants under a path that is
        // gone. The ledger file is untouched by any of it, so the mtime watch in
        // OnFocus cannot see it.
        private void OnProjectChange()
        {
            if (_ledger != null)
            {
                _inventory = NetworkPrefabsInventory.Build(
                    _ledger, AssetDatabase.GUIDToAssetPath, Inspect);
                // ⚠️ Beside the inventory on every path that takes one. The
                // advisory is an asset-database reading exactly as the inventory
                // is, so leaving it behind here would keep naming a prefab whose
                // interpolator the developer has just added — which is the
                // callback that says so.
                _motion = MotionAdvisoriesFor(_inventory);
                // ⛔ The scan's answer describes the project as it was when it
                // ran, and this is the callback that says the project changed.
                // Keeping it would offer "allocate for all" over a list that may
                // name a prefab somebody has since deleted or registered.
                _discovered = Array.Empty<string>();
                _discoveryRan = false;
                Repaint();
            }
        }

        /// <summary>
        /// What one prefab asset carries, answered from the asset database.
        /// </summary>
        /// <remarks>
        /// ⛔ <c>GetComponents</c> on the ROOT, and non-null entries only —
        /// mirroring <c>SpawnManager.CreateLocal</c> exactly, because a check
        /// that disagrees with the runtime is worse than none. It looks no
        /// deeper than the root, so a prefab whose networked component sits on a
        /// child is refused at spawn; a validation that searched the children
        /// would call that prefab fine, which is the reassuring direction and
        /// the one direction a validation must never be wrong in. A missing
        /// script serialises as a null component, which is why the entries are
        /// tested rather than counted.
        /// <para>
        /// A path that loads nothing answers <c>Unknown</c> rather than
        /// <c>Inert</c>: an asset mid-import has not been measured, and saying
        /// it carries no component would be an accusation nobody made.
        /// </para>
        /// </remarks>
        private static PrefabSpawnability Inspect(string assetPath)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (root == null) return PrefabSpawnability.Unknown;

            var components = root.GetComponents<NetworkBehaviour>();
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] != null) return PrefabSpawnability.Networked;
            }
            return PrefabSpawnability.Inert;
        }

        /// <summary>
        /// The full names of the behaviours on one prefab's ROOT, or
        /// <see langword="null"/> when the asset would not load.
        /// </summary>
        /// <remarks>
        /// 🚨 <c>MonoBehaviour</c>, and the reason is the whole reason the motion
        /// advisory exists at all. <c>RTMPE.Sync.NetworkTransformInterpolator</c>
        /// derives from <c>UnityEngine.MonoBehaviour</c> — it PLAYS BACK a pose
        /// that arrived, it does not own one, so it is not a
        /// <c>NetworkBehaviour</c> and never has been. Read as
        /// <c>GetComponents&lt;NetworkBehaviour&gt;()</c> this list could not
        /// contain the interpolator under any configuration, so
        /// <c>ClassifyMotion</c> could never answer <c>Paired</c> and this window
        /// named every correctly-paired prefab in the project as broken.
        /// <para>
        /// ⛔ The TIGHTEST argument that can return both subjects, and not one
        /// step wider. <c>NetworkBehaviour</c> is itself a <c>MonoBehaviour</c>
        /// (<c>Runtime/Core/NetworkBehaviour.cs</c>), so this sees the sender as
        /// well as the receiver; <c>Component</c> or <c>Object</c> would also see
        /// both and would additionally drag in every Transform, Collider and
        /// Renderer on the root for a classifier that matches two full names.
        /// </para>
        /// <para>
        /// ⛔ Widening HERE and not in <see cref="Inspect"/>, which asks a
        /// different question and keeps <c>NetworkBehaviour</c>: that method
        /// mirrors <c>SpawnManager.CreateLocal</c>, and the runtime spawns a
        /// prefab only for a <c>NetworkBehaviour</c> on its root. Widening it
        /// would call a root carrying nothing but an interpolator
        /// <c>Networked</c> — a prefab the runtime refuses, reported as fine,
        /// which is the one direction a validation must never be wrong in.
        /// </para>
        /// <para>
        /// ⛔ A SIBLING of <see cref="Inspect"/> and never a widening of it. That
        /// method performs exactly one typed component read and a rule asserts it
        /// does — a second read folded in would double the cost of the answer the
        /// whole window is built on, for a question only the motion advisory asks.
        /// </para>
        /// <para>
        /// ⚠️ Widening the argument does NOT widen what is accused.
        /// <c>ClassifyMotion</c> matches two full names and ignores everything
        /// else, so the extra components this now returns change no verdict —
        /// what changed is that the verdict <c>Paired</c> became reachable.
        /// </para>
        /// <para>
        /// ⛔ <c>GetComponents</c> on the ROOT, and never <c>InChildren</c> or
        /// <c>InParent</c>. <c>NetworkBehaviour.CachedNetworkTransformInterpolator</c>
        /// is <c>GetComponent&lt;NetworkTransformInterpolator&gt;()</c> on the SAME
        /// GameObject, so an interpolator on a child is one the receive path will
        /// not find: a search that looked deeper would call a prefab fine that
        /// every remote replica freezes on, which is the reassuring direction and
        /// the one direction a validation must never be wrong in.
        /// </para>
        /// <para>
        /// A path that loads nothing answers <see langword="null"/> rather than an
        /// empty list, for the reason <see cref="Inspect"/> answers
        /// <c>Unknown</c> rather than <c>Inert</c>: an empty list is the claim
        /// "this root carries no motion", and nobody measured it.
        /// </para>
        /// <para>
        /// A null entry is walked past. A missing script serialises as one, and
        /// asking a null component for its type throws out of <c>OnGUI</c>.
        /// </para>
        /// </remarks>
        private static IReadOnlyList<string> RootComponentTypeNames(string assetPath)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (root == null) return null;

            // The counting rule lives in RemoteMotionRootReader, which this
            // window and the NetworkTransform inspector both read: two surfaces
            // answering the same question about the same object must not be able
            // to disagree.
            return RemoteMotionRootReader.MotionTypeNamesOn(
                root.GetComponents<MonoBehaviour>());
        }

        // The advisories for one inventory, or an empty list when there is no
        // inventory to ask about. ⛔ Taken WITH the inventory rather than while
        // drawing: every row costs an asset load, and OnGUI runs every frame.
        private static IReadOnlyList<PrefabMotionAdvisory> MotionAdvisoriesFor(
            PrefabInventory inventory)
            => inventory == null
                ? Array.Empty<PrefabMotionAdvisory>()
                : NetworkPrefabsInventory.MotionAdvisories(inventory, RootComponentTypeNames);

        private static string LedgerPath
            => Path.Combine(ReadinessArtifactData.ProjectRoot(), PrefabIdLedger.FileName);

        // ── Reading ─────────────────────────────────────────────────────────────

        // Reads the file and hands the bytes to the policy. ⚠️ The stamp is taken
        // BEFORE the contents: a write landing between the two then leaves a
        // stamp older than what was read, so the next focus reloads. Taken after,
        // the same race caches a new stamp over old contents and the window never
        // corrects itself.
        private void Load()
        {
            // The status describes the last action against the ledger as it then
            // stood. Re-reading the file makes it a claim about bytes that are no
            // longer there — a permission error stays on screen after the user has
            // fixed the permission and pressed Reload.
            Report(string.Empty, false);

            // 🚨 The read decides, not File.Exists. That predicate SWALLOWS every
            // error and answers false: a containing directory with no execute
            // permission, a path-length failure, and — the one a botched merge
            // actually produces — a DIRECTORY named rtmpe-prefabs.json all present
            // as "no ledger yet", which hands back an empty document, marks it
            // usable, and re-enables the allocation that would overwrite live ids.
            // Absent and unreadable are the distinction this whole window rests
            // on, so it is drawn where the file system answers it: the two
            // not-there exceptions mean absent, everything else means somebody has
            // to look.
            string text;
            try
            {
                _writeTimeUtc = File.GetLastWriteTimeUtc(LedgerPath);
                text = File.ReadAllText(LedgerPath);
                _ledgerExists = true;
            }
            catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException)
            {
                _ledgerExists = false;
                _writeTimeUtc = default(DateTime);
                Adopt(PrefabLedgerRead.Absent());
                return;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                _ledgerExists = true;
                // 🚨 The stamp is dropped, and that is the whole repair. Reading
                // metadata succeeds where reading contents fails — a sharing
                // violation from a sync client or a virus scanner is exactly that
                // split — so keeping the stamp leaves the window holding no
                // document while OnFocus compares equal and never retries. The
                // banner then outlives the lock by the rest of the session.
                _writeTimeUtc = default(DateTime);
                Adopt(PrefabLedgerRead.Unreadable(
                    "cannot read " + PrefabIdLedger.FileName + ": " + e.Message));
                return;
            }

            Adopt(PrefabLedgerRead.Of(text));
        }

        // ⚠️ Built into a local first, then all four fields move together. Build
        // reaches the asset database, which this window does not catch; a throw
        // part-way through four assignments would leave the new error beside the
        // PREVIOUS inventory, and the exception escapes OnGUI mid-layout.
        private void Adopt(PrefabLedgerRead read)
        {
            PrefabInventory resolved = read.IsUsable
                ? NetworkPrefabsInventory.Build(
                      read.Document, AssetDatabase.GUIDToAssetPath, Inspect)
                : null;

            // ⚠️ Into a local for the reason the inventory is: this reaches the
            // asset database, and a throw part-way through the assignments below
            // would leave the new error beside the PREVIOUS advisories.
            var motion = MotionAdvisoriesFor(resolved);

            _ledger = read.Document;
            _readForRepair = read.NeedsRepair;
            _loadError = read.Error;
            _inventory = resolved;
            _motion = motion;

            // 🚨 The discovery describes the ledger as it then stood, and this
            // is every path on which the window takes a different one — its own
            // writes included. The ledger lives at the project ROOT, not under
            // Assets, so writing it fires no asset callback and the clearing
            // OnProjectChange does could never reach it: after "allocate for
            // all", every path in the list had an id and the button still
            // offered to allocate for them.
            _discovered = Array.Empty<string>();
            _discoveryRan = false;
        }

        // Whether the file changed since the document the window is holding was
        // read from it. A mutation is a read-modify-write of the WHOLE file, so
        // anything that landed in between would be discarded by the save.
        private bool LedgerChangedSinceLoad()
        {
            // 🚨 `File.GetLastWriteTimeUtc` does NOT throw for a path that is not
            // there. It answers a sentinel — 1601-01-01 UTC — for a missing file
            // and for a missing directory alike, so a "not there" catch around it
            // never runs for the case it is written for. This method had one, and
            // the correct answer lived inside it: the reachable branch instead
            // reported absence as a change, and the stamp comparison agreed with
            // it because the sentinel never equals the zero DateTime that Load
            // records for an absent ledger.
            //
            // What that cost is the whole feature. Absence is the state EVERY
            // fresh project starts in, Commit refuses on a change, and the reload
            // it performs re-establishes exactly the state that made it refuse —
            // so the first allocation could never write the ledger, and the retry
            // the refusal asks for could never succeed. Reported from the field
            // against v13; shipped since v12.
            //
            // Absence and presence are therefore answered separately, because
            // they are different questions about different risks.
            if (!_ledgerExists)
            {
                // Absent when this window loaded. The only change that matters is
                // one ARRIVING — a pull, a branch switch, a teammate's sync —
                // because writing then would put a fresh document over a ledger
                // this window never read and reissue ids peers already hold.
                //
                // Asked by opening, not by stat and not by File.Exists. The stat
                // answers a sentinel, which is a value rather than a refusal; and
                // File.Exists swallows every error and answers false, so a
                // directory wearing the ledger's name and a permission problem
                // would both read as "still absent" and be written over. Load
                // draws the same distinction the same way, for the same reason.
                try
                {
                    using (File.OpenRead(LedgerPath))
                    {
                    }
                    return true;
                }
                catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException)
                {
                    // Still not there: this is the first allocation, and it is
                    // free to create the ledger.
                    return false;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // Something is at that path that will not open. Cannot tell,
                    // so assume it changed — WriteAtomically would fail anyway,
                    // and refusing here reports it as a conflict rather than as a
                    // write error, which is the safer of the two wrong answers.
                    return true;
                }
            }

            try
            {
                // Present when this window loaded, so the stamp is the question.
                // A ledger that has since vanished stats as the sentinel, which
                // is not the stamp it was read at — a change, correctly.
                return File.GetLastWriteTimeUtc(LedgerPath) != _writeTimeUtc;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // Cannot tell, so assume it did. Refusing a save costs a reload;
                // the alternative is overwriting a file we could not even stat.
                return true;
            }
        }

        // ── Writing ─────────────────────────────────────────────────────────────

        private void Commit(PrefabLedgerDocument updated, string success)
        {
            // 🚨 The file is re-checked HERE, on the far side of whatever the
            // caller did — a confirm dialog is a second window of unbounded
            // length, and a pull, a branch switch or a teammate's sync do not wait
            // for it to close. This is a read-modify-write of the whole ledger, so
            // committing a document decided from older bytes discards every
            // registration that arrived in between and re-issues ids peers already
            // hold. ConversionWizard asks the same question after its own modal,
            // for the same reason.
            if (LedgerChangedSinceLoad())
            {
                Load();
                Report(
                    PrefabIdLedger.FileName + " changed on disk while that was open, so nothing was "
                        + "written — the ledger has been reloaded. Check what is there now and "
                        + "repeat the operation if it is still what you want.",
                    true);
                return;
            }

            string text;
            try
            {
                text = PrefabIdLedger.Serialize(updated);
            }
            catch (ArgumentException e)
            {
                Report("refusing to write a ledger this tool could not read back: " + e.Message, true);
                return;
            }

            try
            {
                WriteAtomically(LedgerPath, text);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Report("cannot write " + PrefabIdLedger.FileName + ": " + e.Message, true);
                return;
            }

            Load();
            Report(success, false);
        }

        /// <summary>
        /// Writes through a sibling temporary file so an interrupted save cannot
        /// leave a truncated ledger behind.
        /// </summary>
        /// <remarks>
        /// This file is the only record of which prefab each wire id names, and a
        /// half-written one is worse than an old one: it fails the strict read and
        /// the repair read alike, so the window that could rebuild it refuses to
        /// act. <c>File.Replace</c> rather than a delete-then-move, which has a
        /// window in which neither file exists; it needs an existing destination,
        /// so the first-ever write moves instead.
        /// <para>
        /// ⛔ The guarantee is against an interrupted PROCESS, not an interrupted
        /// machine. Nothing here flushes the staging file to the device before the
        /// rename, so a power loss can still leave a zero-length ledger on a file
        /// system that reorders the two. Narrowed deliberately rather than left
        /// reading as durability.
        /// </para>
        /// </remarks>
        private static void WriteAtomically(string path, string text)
        {
            string staging = path + ".tmp";

            try
            {
                // ⚠️ Inside the try, not above it. WriteAllText can throw AFTER
                // creating and partly filling the file — a full disk is the
                // textbook case — and that is precisely when a truncated
                // `.tmp` is left beside a file people commit.
                File.WriteAllText(staging, text);

                if (File.Exists(path))
                {
                    File.Replace(staging, path, null);
                }
                else
                {
                    File.Move(staging, path);
                }
            }
            catch
            {
                // The staging file sits beside a file people commit. Leaving one
                // behind on a failed save invites it into the next commit, where
                // it reads as a second ledger. Cleared on the way out; the throw
                // is what the caller reports.
                TryDelete(staging);
                throw;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // Best effort by construction: this runs while another failure is
                // already propagating, and replacing that report with this one
                // would hide the reason the save did not happen.
            }
        }

        private void Report(string message, bool isError)
        {
            Status = message;
            StatusIsError = isError;
        }

        // ── Actions ─────────────────────────────────────────────────────────────

        // What the user highlighted, as the ledger's vocabulary. ⛔ Reads
        // `Selection.objects`, not `activeObject`: the button says "for
        // selection", and serving one of twelve leaves eleven prefabs without an
        // id and says nothing.
        private static List<PrefabCandidate> SelectedCandidates()
        {
            var candidates = new List<PrefabCandidate>();
            foreach (var selected in Selection.objects)
            {
                string assetPath = AssetDatabase.GetAssetPath(selected);

                // ⛔ The pure layer tests the EXTENSION, which is all it can do
                // without an editor — a `readme.txt` renamed to `.prefab` passes
                // that, imports as a DefaultAsset, and would burn a wire id on
                // something RegisterPrefab can never take. The type question
                // belongs here, where the asset database is; a candidate that
                // fails it is dropped, and the batch reports how many it dropped.
                if (!NetworkPrefabsInventory.IsPrefabAssetPath(assetPath)
                    || AssetDatabase.GetMainAssetTypeAtPath(assetPath) != typeof(GameObject))
                {
                    candidates.Add(new PrefabCandidate(null, null));
                    continue;
                }

                candidates.Add(
                    new PrefabCandidate(assetPath, AssetDatabase.AssetPathToGUID(assetPath)));
            }

            return candidates;
        }

        private void AllocateForSelection()
        {
            var outcome = NetworkPrefabsInventory.AllocateForEach(_ledger, SelectedCandidates());

            if (outcome.Updated == null)
            {
                Report(outcome.Message, outcome.IsError);
                return;
            }

            Commit(outcome.Updated, outcome.Message);
        }

        /// <summary>
        /// Every spawnable prefab in the project that holds no id yet.
        /// </summary>
        /// <remarks>
        /// ⚠️ Re-derived on demand rather than cached with the inventory: it
        /// scans every prefab in the project and loads each one, which is a cost
        /// worth paying when somebody asks and not once per repaint.
        /// </remarks>
        // ⛔ Scoped to Assets. `FindAssets` with no folders searches every
        // installed PACKAGE as well, so the unscoped call offered a project's
        // ledger the prefabs of libraries it does not own. An id allocated
        // against one of those is a write into the project's ledger naming an
        // asset the project cannot change, and it disappears with the package.
        //
        // ⚠️ Not this SDK: it ships no prefab, and its samples live under
        // `Samples~`, which the AssetDatabase never imports. The libraries this
        // covers are the ones that do — a registry package under
        // `Library/PackageCache` with prefabs in it.
        //
        // ⚠️ What it costs is an EMBEDDED package, which is in the project's own
        // repository and which an author may legitimately want registered. Only
        // discovery is scoped: a prefab under `Packages/` that already holds an
        // id still resolves and is still inspected, so nothing already
        // registered is lost.
        //
        // 🔑 The scenes window states the same boundary in as many words: only
        // this project's own files under Assets are read.
        private IReadOnlyList<string> UnregisteredSpawnables()
            => NetworkPrefabsInventory.UnregisteredSpawnables(
                _ledger,
                AssetDatabase.FindAssets("t:Prefab", new[] { AssetsFolder })
                    .Select(AssetDatabase.GUIDToAssetPath),
                AssetDatabase.AssetPathToGUID,
                Inspect);

        private const string AssetsFolder = "Assets";

        private void AllocateForAll(IReadOnlyList<string> paths)
        {
            var candidates = new List<PrefabCandidate>(paths.Count);
            foreach (string path in paths)
            {
                candidates.Add(new PrefabCandidate(path, AssetDatabase.AssetPathToGUID(path)));
            }

            var outcome = NetworkPrefabsInventory.AllocateForEach(_ledger, candidates);
            if (outcome.Updated == null)
            {
                Report(outcome.Message, outcome.IsError);
                return;
            }

            Commit(outcome.Updated, outcome.Message);
        }

        // Every operation that changes which number names which prefab leaves the
        // generated constants describing the old arrangement — and they still
        // compile, so the failure surfaces as a spawn that produces nothing, which
        // is the first entry in the SDK's own troubleshooting guide.
        private const string RegenerateReminder =
            " — regenerate " + NetworkPrefabsInventory.GeneratedTypeName
            + ".cs, the copy in your project still names the old id";

        private void Retire(PrefabRegistration registration)
        {
            string what = registration.AssetPath ?? ("GUID " + registration.Guid);
            if (!EditorUtility.DisplayDialog(
                    "Retire prefab id " + registration.Id,
                    "Id " + registration.Id + " (" + what + ") will be burned and never issued again.\n\n"
                        + "Any build already spawning it keeps working; this project stops naming it. "
                        + "The id is not reclaimed, because a peer that still holds it would spawn the "
                        + "wrong prefab.",
                    "Retire", "Cancel"))
            {
                return;
            }

            if (!PrefabIdLedger.TryRetire(_ledger, registration.Guid, out var updated, out string error))
            {
                Report(error, true);
                return;
            }

            Commit(updated, "retired id " + registration.Id + RegenerateReminder);
        }

        private void Reissue(PrefabRegistration registration)
        {
            if (!PrefabIdLedger.TryReissue(
                    _ledger, registration.Guid, out uint id, out var updated, out string error))
            {
                Report(error, true);
                return;
            }

            Commit(updated, "moved " + (registration.AssetPath ?? registration.Guid)
                + " from id " + registration.Id + " to " + id + RegenerateReminder);
        }

        private void GenerateConstants()
        {
            var result = NetworkPrefabsInventory.GenerateConstants(_inventory, _generatedNamespace);
            if (!result.IsValid)
            {
                Report(result.Error, true);
                return;
            }

            string absolute = Path.Combine(
                ReadinessArtifactData.ProjectRoot(), NetworkPrefabsInventory.GeneratedAssetPath);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(absolute));
                File.WriteAllText(absolute, result.Source);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Report("cannot write " + NetworkPrefabsInventory.GeneratedAssetPath + ": " + e.Message, true);
                return;
            }

            // ⚠️ Refresh, not ImportAsset alone. On the first Generate the parent
            // folders were made with Directory.CreateDirectory and the asset
            // database has never seen them, so importing a file inside them
            // registers nothing: the Project window stays empty and the constants
            // do not compile until something else triggers a refresh — which on a
            // project with Auto Refresh off is never. SettingsProvider, the other
            // in-Assets writer here, does the same after the same CreateDirectory.
            AssetDatabase.Refresh();
            AssetDatabase.ImportAsset(NetworkPrefabsInventory.GeneratedAssetPath);

            // ⛔ After the constants, and only when they were written. The two
            // come from one ledger and describe one fact — the id a project
            // types, and the prefab a player resolves it to — so a run that
            // wrote one and refused the other would leave a project whose code
            // names ids its registry does not carry. The refusals are all above:
            // a duplicate id, an unusable namespace, a name collision. Nothing
            // below can refuse.
            //
            // It also depends on what runs above it: CreateAsset needs the
            // folder to exist AND to be imported, and the Refresh two lines up
            // is what imports the directory Directory.CreateDirectory made.
            // ⚠️ Wrapped, like the constants write above it. An asset database
            // call can fail for reasons that have nothing to do with this
            // project — a version-control system holding the file read-only is
            // the ordinary one — and an exception escaping here takes the
            // report with it, so the user never learns the .cs WAS written.
            string registry;
            try
            {
                registry = WriteRegistry();
            }
            catch (Exception e)
            {
                Report("wrote " + NetworkPrefabsInventory.GeneratedAssetPath
                    + ", but the registry at " + NetworkPrefabsInventory.GeneratedRegistryPath
                    + " was not written: " + e.Message
                    + ". The constants and the registry are now out of step; press \"Generate "
                    + NetworkPrefabsInventory.GeneratedTypeName + ".cs and the prefab registry\" "
                    + "again.",
                    true);
                return;
            }

            Report("wrote " + NetworkPrefabsInventory.GeneratedAssetPath + " and " + registry,
                registry.Contains(ReplacedNotice, StringComparison.Ordinal));
        }

        /// Said when the asset at the registry's path could not be loaded as one
        /// and had to be replaced. Named so the caller can raise the report to an
        /// error without re-deciding what happened.
        private const string ReplacedNotice = "REPLACED";

        /// <summary>
        /// Write the generated registry asset from the same inventory the
        /// constants came from, and describe what it holds.
        /// </summary>
        /// <remarks>
        /// Which registrations become rows is decided in
        /// <see cref="NetworkPrefabsInventory.RegistryRows"/>, where it is
        /// reachable from a test. What is left here is the editor's own
        /// vocabulary: turning a path into a reference, and creating the asset
        /// the first time rather than the second.
        /// </remarks>
        private string WriteRegistry()
        {
            var rows = NetworkPrefabsInventory.RegistryRows(_inventory);
            var entries = new List<NetworkPrefabEntry>(rows.Count);

            // 🔑 Counted where the entry is ADDED, not from the ledger's tally
            // of unverified registrations. The two differ exactly when a row is
            // admitted and then dropped below — the ordinary shape of an import
            // still running — and a message reporting what it did not write is
            // worse than one reporting nothing.
            var uninspectedGuids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var registration in _inventory.Registrations)
            {
                if (registration.Status == PrefabRegistrationStatus.Unverified)
                    uninspectedGuids.Add(registration.Guid);
            }

            int unresolved  = 0;
            int uninspected = 0;
            foreach (var row in rows)
            {
                // ⛔ Resolved through the GUID, which is what the ledger is keyed
                // on and the one thing about a prefab that does not move. The
                // path beside it is the scan's snapshot, kept for the message.
                string live = AssetDatabase.GUIDToAssetPath(row.Guid);
                var prefab = string.IsNullOrEmpty(live)
                    ? null
                    : AssetDatabase.LoadAssetAtPath<GameObject>(live);
                // An asset deleted since the scan, or a GUID that now names
                // something which is not a prefab. Either way there is nothing
                // for a row to point at, and an empty row would be reported by
                // the runtime on every connect for as long as it stood.
                if (prefab == null)
                {
                    unresolved++;
                    continue;
                }
                // ⛔ Written as it stands, and counted. The prefab is loaded
                // right here and is not re-inspected: a row admitted because
                // nobody could answer for it during the scan is admitted again,
                // so the registry a project ships can differ by which second
                // Generate was pressed in. That is disclosed rather than
                // repaired — the count below reaches the report, and the
                // alternative is this method deciding which registrations
                // become rows, which is a policy that lives one layer down
                // where a test can reach it.
                entries.Add(new NetworkPrefabEntry(row.Id, prefab));
                if (uninspectedGuids.Contains(row.Guid)) uninspected++;
            }

            string path = NetworkPrefabsInventory.GeneratedRegistryPath;
            var asset = AssetDatabase.LoadAssetAtPath<NetworkPrefabRegistry>(path);

            // 🚨 Loading nothing is not the same as there being nothing there,
            // and the difference is destructive. LoadAssetAtPath answers null
            // for any asset it cannot produce as this type — one whose script
            // no longer resolves after a package was removed and re-added, one
            // a merge left unparseable — and CreateAsset DELETES what is at the
            // path before writing, so the replacement carries a NEW GUID.
            // Every NetworkSettings.prefabRegistry pointing at the old one then
            // resolves to nothing, the runtime treats an unassigned field as
            // the ordinary state it is, and no client can spawn anything.
            //
            // The path is the SDK's own, so replacing is still the right act —
            // refusing would leave a project unable to regenerate after a bad
            // merge. What was missing is that it be said out loud.
            bool occupied = !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path));
            bool replacing = asset == null && occupied;

            if (asset == null) asset = ScriptableObject.CreateInstance<NetworkPrefabRegistry>();
            asset.SetEntries(entries);

            // Created when nothing is there and when what is there cannot be
            // read; edited in place otherwise, which is what keeps the GUID.
            if (!occupied || replacing) AssetDatabase.CreateAsset(asset, path);
            else                        EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();

            return path + " (" + entries.Count + " prefab(s)"
                + (unresolved > 0
                    ? ", " + unresolved + " row(s) resolved to no prefab"
                    : string.Empty)
                + (uninspected > 0
                    ? ", " + uninspected + " written without a spawnability check"
                    : string.Empty)
                + (replacing
                    ? " — " + ReplacedNotice + ": the asset already at that path could not be "
                      + "read as a registry and was rewritten, so it carries a new GUID. "
                      + "Re-assign it to NetworkSettings.prefabRegistry."
                    : string.Empty)
                + ")";
        }

        // ── Drawing ─────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            DrawToolbar();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (_loadError != null)
            {
                EditorGUILayout.HelpBox(
                    _loadError + "\n\nNothing is offered until this file opens: writing a new ledger "
                        + "over one that failed to parse would discard every id already in use.",
                    MessageType.Error);
                EditorGUILayout.EndScrollView();
                return;
            }

            DrawState();
            EditorGUILayout.Space();
            DrawAllocate();
            EditorGUILayout.Space();
            DrawRegistrations();
            EditorGUILayout.Space();
            DrawDiscover();
            EditorGUILayout.Space();
            DrawGenerate();
            DrawStatus();

            EditorGUILayout.EndScrollView();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(70)))
            {
                Load();
                GUIUtility.ExitGUI();
            }

            GUILayout.FlexibleSpace();

            // The file name, not the absolute path: the toolbar is narrow, the
            // location never varies, and the sibling readiness window prints the
            // name for the same reason.
            GUILayout.Label(PrefabIdLedger.FileName, EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawState()
        {
            if (!_ledgerExists)
            {
                EditorGUILayout.HelpBox(
                    "No ledger yet. It is written the first time an id is allocated, and belongs in "
                        + "version control: it is the record of which prefab each spawn id names, and a "
                        + "project without it re-derives the numbers per machine.",
                    MessageType.Info);
                return;
            }

            if (_readForRepair)
            {
                EditorGUILayout.HelpBox(
                    _inventory.DuplicateCount + " registrations share an id with another — the shape a "
                        + "merge produces when two branches each allocated. Re-issue one of each pair. "
                        + "Until then clients disagree about what those numbers name, and constants "
                        + "cannot be generated.",
                    MessageType.Error);
                return;
            }

            if (_inventory.MissingCount > 0)
            {
                EditorGUILayout.HelpBox(
                    _inventory.MissingCount + " registrations name an asset this project no longer has. "
                        + "Their ids stay live for every peer already running; retire one only when no "
                        + "build still spawns it.",
                    MessageType.Warning);
                return;
            }

            if (_inventory.NotNetworkedCount > 0)
            {
                EditorGUILayout.HelpBox(
                    _inventory.NotNetworkedCount + " registrations name a prefab with no networked "
                        + "component on its root. Spawning one fails at runtime and destroys the "
                        + "instance, so the id is unusable until a NetworkBehaviour is added to the "
                        + "prefab root — and it is left out of the generated registry until then.",
                    MessageType.Warning);
                return;
            }

            // ⛔ Not a return: nothing is claimed about these rows either way,
            // so the summary below still describes the ledger. A row mid-import
            // must not read as a fault.
            if (_inventory.UnverifiedCount > 0)
            {
                EditorGUILayout.HelpBox(
                    _inventory.UnverifiedCount + " registrations could not be inspected — usually an "
                        + "import still running. Nothing is claimed about them either way, so they "
                        + "are offered to the generated registry rather than held back on the "
                        + "strength of an answer nobody got; whether each one reaches it is decided "
                        + "when it is written, and the report says so. They are inspected again as "
                        + "soon as the import finishes — the window reloads itself on a project "
                        + "change — and pressing Reload asks now.",
                    MessageType.Info);
            }

            // ⛔ Not a return either, and one box rather than a verdict on the
            // ledger: every prefab below is registered, spawnable and holds a
            // valid id, so PrefabInventory.IsClean does not know about this and
            // Generate is not refused over it. What is wrong is on the PREFAB.
            //
            // 🔑 The advisory the runtime raises for the same fault fires on the
            // RECEIVE path — so it is heard once a second client is in the room,
            // and a developer testing alone never hears it at all. This is the
            // one place the question is asked before anybody runs anything.
            if (_motion.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    _motion.Count + " registered prefabs carry "
                        + NetworkPrefabsInventory.NetworkTransformSimpleName
                        + " with no working "
                        + NetworkPrefabsInventory.NetworkTransformInterpolatorSimpleName
                        + " on the root. The interpolator is the RECEIVING half of remote motion: "
                        + "without it every other player's copy of the object stands still, and "
                        + "nothing says so until a second client joins. Add "
                        + NetworkPrefabsInventory.NetworkTransformInterpolatorSimpleName
                        + " to each prefab root — or, where one is already there and switched "
                        + "off, tick its checkbox rather than adding a second. The rows below "
                        + "name them.\n\n"
                        + "Only the prefab root is read, because that is where the runtime looks; "
                        + "a class of your own deriving from "
                        + NetworkPrefabsInventory.NetworkTransformSimpleName
                        + " is not recognised here.",
                    MessageType.Warning);
            }

            // 🔑 The count is the ledger's, and the second clause is the notice
            // above it. Saying "nothing outstanding" under a box that has just
            // asked for another pass is the summary contradicting the state it
            // is summarising, and a reader believes whichever of the two they
            // read last.
            EditorGUILayout.HelpBox(
                _inventory.Registrations.Count + " prefabs registered, "
                    + (_inventory.UnverifiedCount > 0
                        ? "nothing outstanding but the inspections above."
                        : "nothing outstanding.") + "\n\n"
                    + "Commit " + PrefabIdLedger.FileName + ": it is the record of which prefab "
                    + "each spawn id names, and a project without it re-derives the numbers per "
                    + "machine — which is the disagreement it exists to prevent.",
                MessageType.Info);
        }

        private void DrawAllocate()
        {
            EditorGUILayout.LabelField("Allocate", EditorStyles.boldLabel);

            var candidates = SelectedCandidates();
            int usable = candidates.Count(c => c.AssetPath != null);
            bool selectable = usable > 0;

            // ⚠️ The count of what was left out is appended in BOTH shapes. It
            // used to hang off the plural branch alone, so selecting one prefab
            // beside two materials showed the prefab's path and nothing else —
            // which is the silent drop this whole change is about, moved one case
            // over. Found by the test written for the other branch.
            string what = usable == 1
                ? candidates.First(c => c.AssetPath != null).AssetPath
                : usable + " prefabs";
            int others = candidates.Count - usable;

            EditorGUILayout.LabelField(
                "Selected",
                !selectable
                    ? "— select a prefab in the Project window —"
                    : others > 0
                        ? what + " (and " + others + " that are not prefabs)"
                        : what);

            using (new EditorGUI.DisabledScope(!selectable))
            {
                if (GUILayout.Button("Allocate id for selection"))
                {
                    AllocateForSelection();

                    // An action changes how many controls the rest of this pass
                    // draws, and IMGUI matches controls between the layout and
                    // repaint events by position. Abandoning the pass is the
                    // supported way out; finishing it against the new state is
                    // what produces the mismatched-control errors.
                    GUIUtility.ExitGUI();
                }
            }
        }

        // ⛔ Behind a button, not on every repaint.  The scan loads every prefab
        // in the project; running it from OnGUI would make the window's cost
        // scale with the project on every frame it is visible.  What is drawn
        // between presses is the last answer, and the count says when it was
        // taken by being absent until somebody asks.
        private void DrawDiscover()
        {
            EditorGUILayout.LabelField("Unregistered prefabs", EditorStyles.boldLabel);

            if (GUILayout.Button("Scan the project for spawnable prefabs with no id"))
            {
                // ⛔ In a `finally`, not after the work. A progress bar left up
                // by a throw is modal: it blocks every other window, has no
                // cancel of its own, and the only way out is restarting the
                // Editor. The scan loads every prefab under Assets, which on a
                // large project is several seconds of a window that does not
                // repaint — and the first thing a user does then looks like a
                // hang, so the conclusion they draw is about the tool.
                try
                {
                    EditorUtility.DisplayProgressBar(
                        DiscoveryTitle, "Loading every prefab under Assets…", 0.5f);
                    _discovered = UnregisteredSpawnables();
                    _discoveryRan = true;
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }

                GUIUtility.ExitGUI();
            }

            if (!_discoveryRan) return;

            if (_discovered.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Every prefab carrying a networked component already holds an id.",
                    MessageType.Info);
                return;
            }

            EditorGUILayout.HelpBox(
                _discovered.Count + " prefabs carry a networked component and hold no id. "
                    + "Without one they cannot be spawned over the network.",
                MessageType.Warning);

            // ⚠️ Bounded.  A project can hold thousands, and a window that draws
            // one row each stops responding; the button below acts on all of
            // them regardless, which is why the list is a preview rather than
            // the operand.
            for (int i = 0; i < _discovered.Count && i < DiscoveryPreviewRows; i++)
            {
                EditorGUILayout.LabelField(_discovered[i], EditorStyles.miniLabel);
            }
            if (_discovered.Count > DiscoveryPreviewRows)
            {
                EditorGUILayout.LabelField(
                    "… and " + (_discovered.Count - DiscoveryPreviewRows) + " more",
                    EditorStyles.miniLabel);
            }

            if (GUILayout.Button("Allocate an id for all " + _discovered.Count))
            {
                AllocateForAll(_discovered);
                GUIUtility.ExitGUI();
            }
        }

        private const string DiscoveryTitle = "RTMPE — looking for unregistered prefabs";

        private const int DiscoveryPreviewRows = 12;

        private IReadOnlyList<string> _discovered = Array.Empty<string>();
        private bool _discoveryRan;

        private void DrawRegistrations()
        {
            EditorGUILayout.LabelField("Registrations", EditorStyles.boldLabel);

            if (_inventory.Registrations.Count == 0)
            {
                EditorGUILayout.LabelField("None.", EditorStyles.miniLabel);
                return;
            }

            foreach (var registration in _inventory.Registrations)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();

                GUILayout.Label(
                    registration.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    EditorStyles.boldLabel,
                    GUILayout.Width(48));
                GUILayout.Label(
                    registration.AssetPath ?? ("missing — GUID " + registration.Guid),
                    EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();

                // ⛔ Which operations a row offers is policy, and it is asked of
                // the registration rather than derived here: a status compared
                // inline while drawing is a rule no test can reach, which is what
                // this file's own header says it does not do.
                if (registration.CanReissue && GUILayout.Button("Re-issue", GUILayout.Width(80)))
                {
                    Reissue(registration);
                    GUIUtility.ExitGUI();
                }

                if (registration.CanRetire && GUILayout.Button("Retire", GUILayout.Width(70)))
                {
                    Retire(registration);
                    GUIUtility.ExitGUI();
                }

                EditorGUILayout.EndHorizontal();

                // 🚨 Asked of the row, not chosen here.  This was a ternary on
                // Duplicate with everything else falling to "no asset in this
                // project carries this GUID" — so the moment a third state
                // existed, a prefab sitting right there in the project was
                // reported as absent.  An else-branch answers every question it
                // is asked, including the ones it has not heard of.
                string explanation = registration.Explanation;
                if (explanation != null)
                {
                    EditorGUILayout.LabelField(explanation, EditorStyles.wordWrappedMiniLabel);
                }

                // ⛔ Beside the status sentence rather than instead of it. The two
                // are different faults — one is what the LEDGER says about the
                // row, this is what the PREFAB carries — and a row can only ever
                // have this one when it has none of the others, so replacing the
                // explanation would have been an else-branch that never ran.
                // Asked of the advisory, not composed here.
                var advisory = MotionAdvisoryFor(registration.AssetPath);
                if (advisory != null)
                {
                    EditorGUILayout.LabelField(
                        advisory.Explanation, EditorStyles.wordWrappedMiniLabel);
                }

                EditorGUILayout.EndVertical();
            }
        }

        // Which advisory names this row, or null. A lookup rather than a decision:
        // whether a prefab earns one was settled in NetworkPrefabsInventory, and
        // the paths come from the same inventory the rows do.
        private PrefabMotionAdvisory MotionAdvisoryFor(string assetPath)
        {
            if (assetPath == null) return null;

            for (int i = 0; i < _motion.Count; i++)
            {
                if (string.Equals(_motion[i].AssetPath, assetPath, StringComparison.Ordinal))
                {
                    return _motion[i];
                }
            }

            return null;
        }

        private void DrawGenerate()
        {
            EditorGUILayout.LabelField("Generated constants", EditorStyles.boldLabel);
            _generatedNamespace = EditorGUILayout.TextField(
                new GUIContent("Namespace", "The namespace " + NetworkPrefabsInventory.GeneratedTypeName
                    + " is declared in."),
                _generatedNamespace);

            EditorGUILayout.LabelField("Writes", NetworkPrefabsInventory.GeneratedAssetPath);

            EditorGUILayout.LabelField("And", NetworkPrefabsInventory.GeneratedRegistryPath);

            if (GUILayout.Button("Generate " + NetworkPrefabsInventory.GeneratedTypeName
                + ".cs and the prefab registry"))
            {
                GenerateConstants();
                GUIUtility.ExitGUI();
            }
        }

        private void DrawStatus()
        {
            if (string.IsNullOrEmpty(Status))
            {
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(Status, StatusIsError ? MessageType.Error : MessageType.Info);
        }
    }
}
#endif
