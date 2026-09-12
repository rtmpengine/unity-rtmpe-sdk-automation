// RTMPE SDK — Runtime/Core/RtmpeSdk.cs
//
// The package's own version, readable from the RUNTIME.
//
// Authority: clients/unity-sdk/Packages/com.rtmpe.sdk/package.json   ("version")
// Mirror:    this file                                                (C# copy)
//
// ⚠  SYNC RULE: `Version` below must equal package.json's `version` on every
//   release. Held by scripts/check-sdk-version-parity.sh, which reads this
//   file's constant through the shared comment scanner and refuses a tree where
//   the two disagree, where the constant has gone, or where more than one
//   declares itself.
//
// ── Why a checked-in constant rather than a generated one ────────────────────
// Unity compiles this package FROM SOURCE inside the consumer's project. We ship
// a tree, not a build output: there is no step of ours between `package.json`
// and the assembly a player links, so nothing of ours can stamp the value on the
// way through.
//
// The hooks that could write it all run in the consumer's EDITOR — an
// AssetPostprocessor, an IPreprocessBuildWithReport, a menu item — and each of
// them would have to write the generated file back into this directory, which is
// exactly where a resolved package is not writable: a registry or tarball
// package lives under `Library/PackageCache`, read-only by design. A hook that
// wrote somewhere else would produce a file that is not part of the package and
// so is not what a consumer's build compiles.
//
// So the value has to be committed either way. What is left to choose is whether
// it is committed here, where a reader finds it, or generated into a committed
// file with a longer path and the same failure mode. The honest form is the
// copy, plus a rule holding it to the authority — which is what
// `scripts/check-sdk-version-parity.sh` is.
//
// Modelled on Runtime/Core/NetworkConstants.cs, which mirrors a Rust file by
// hand under the same kind of rule and for the same reason: two languages, one
// fact, no shared declaration.

namespace RTMPE.Core
{
    /// <summary>
    /// What this build of the SDK is.
    /// </summary>
    /// <remarks>
    /// Readable from a Player build, which is the point: the version is
    /// otherwise recorded only in <c>package.json</c> — a file that ships inside
    /// the package tree and is not something a running game, a support thread or
    /// a crash report can quote. An integrator whose SDK version could not be
    /// established is an integrator whose report cannot be acted on.
    /// </remarks>
    public static class RtmpeSdk
    {
        /// <summary>
        /// This package's version, identical to <c>package.json</c>'s
        /// <c>version</c>.
        /// </summary>
        /// <remarks>
        /// ⚠️ Edited by hand on every release, together with the manifest and the
        /// documentation stamps. <c>scripts/check-sdk-version-parity.sh</c>
        /// refuses a tree where any of them disagree.
        /// </remarks>
        public const string Version = "1.0.5";
    }
}
