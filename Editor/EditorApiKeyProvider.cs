// RTMPE SDK — Editor/EditorApiKeyProvider.cs
//
// Hands the setup wizard's stored credential to the runtime.
//
// The wizard already keeps the key in the platform credential vault
// (`ApiKeyStore`), and nothing ever read it back at play time — so pressing
// Play in the Editor required the key a second time, in a serialized field,
// which is the one place it must not be. A stored credential the running game
// cannot see is a stored credential nobody uses.
//
// This file is the whole of the Editor's contribution to `ApiKeySource`: it
// registers a provider and holds no key of its own. Nothing here ships with a
// player — the Editor assembly is Editor-only at both the assembly definition
// and the preprocessor level.
//
// 🔑 The SECONDARY seam, not the primary one. A player build can be given a
// credential only by a provider the game registers, so that is what every page
// documenting a shipped title tells an integrator to write — and that code runs
// in the Editor as readily as in a player. Held in the same slot, the two
// registrations were rivals and the later one won: from the moment a game
// registered, an empty answer from it fell through to the command line and the
// environment and never back to the vault, so the Editor reported no API key
// while the wizard held a good one. Ranked below, the vault answers exactly
// when the game's own source has nothing, which in the Editor is most of the
// time and in a player is never — because nothing registers this there.

#if UNITY_EDITOR
using UnityEditor;
using RTMPE.Core;

namespace RTMPE.Editor
{
    /// <summary>
    /// Registers the Editor credential vault as the API-key provider.
    /// </summary>
    [InitializeOnLoad]
    internal static class EditorApiKeyProvider
    {
        // Runs on Editor load and after every domain reload, which includes
        // entering Play mode. With domain reloading disabled the static
        // registration survives instead of being re-made, and nothing else
        // writes this slot, so both configurations end with the vault in place.
        static EditorApiKeyProvider()
        {
            ApiKeySource.SetSecondaryProvider(ApiKeyStore.Load);
        }
    }
}
#endif
