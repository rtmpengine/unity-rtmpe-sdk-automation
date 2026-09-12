// RTMPE SDK — Runtime/Rooms/Lobbies/LobbyName.cs
//
// What the Room Service accepts as a lobby name, stated on the client side.
//
// The rule is not decoration. A lobby name is the trailing token of the NATS
// subject the Room Service publishes room-list updates on
// (`rtmpe.lobby.update.<project_id>.<lobby_name>`), so a name carrying a dot, a
// wildcard or whitespace is a corrupted subject rather than a rejected string,
// and the service refuses one before it can be used.
//
// ⚠️ Restating the rule here is a duplicate, and duplicates drift. It is
// deliberate for one reason: a name the service refuses does not come back as a
// refusal. The gateway answers the empty room list on any error from the lobby
// path, so the client sees a successful reply with no rooms, marks itself in the
// lobby, and waits for push updates that were never subscribed. A rule that
// costs a duplicated constant and turns a permanent silence into an immediate
// exception is worth the duplicate; `scripts/check-lobby-matchmaking-contract.sh`
// holds the two copies to each other.

using System;

namespace RTMPE.Rooms
{
    /// <summary>
    /// The lobby-name rule enforced by the Room Service.
    /// </summary>
    public static class LobbyName
    {
        /// <summary>
        /// Longest accepted name, in UTF-8 bytes. Mirrors
        /// <c>entities.MaxLobbyNameLen</c>.
        /// </summary>
        public const int MaxBytes = 32;

        /// <summary>
        /// The accepted alphabet, as it appears in the service's own error
        /// message, so a developer searching for either half finds the other.
        /// </summary>
        public const string Alphabet = "[A-Za-z0-9_-]";

        /// <summary>
        /// Whether <paramref name="name"/> is a name the Room Service will
        /// accept. Every accepted character is a single UTF-8 byte, so length
        /// in characters and length in bytes coincide.
        /// </summary>
        public static bool IsValid(string name)
        {
            return Describe(name) == null;
        }

        /// <summary>
        /// Why <paramref name="name"/> would be refused, or <c>null</c> when it
        /// would be accepted.
        /// </summary>
        /// <remarks>
        /// Returning the reason rather than a bare bool is what lets the call
        /// site name the offending character; a caller told only "invalid" has
        /// to guess between the three rules.
        /// </remarks>
        public static string Describe(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "must not be empty — there is no default lobby; " +
                       "name the lobby your players should meet in";

            // Counted in characters, which is the byte count for this alphabet:
            // an over-length name is refused by the length rule and anything
            // outside the alphabet by the character rule below, so no input
            // reaches the service with the two measures disagreeing.
            if (name.Length > MaxBytes)
                return $"length {name.Length} exceeds the maximum of {MaxBytes}";

            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool ok = (c >= 'A' && c <= 'Z')
                          || (c >= 'a' && c <= 'z')
                          || (c >= '0' && c <= '9')
                          || c == '_' || c == '-';
                if (!ok)
                    return $"character '{c}' at offset {i} is not in {Alphabet}";
            }
            return null;
        }

        /// <summary>
        /// Throws when <paramref name="name"/> is one the Room Service refuses.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// The name would be refused. The message names the rule and the
        /// offending position, because the alternative — sending it — produces
        /// a lobby that never delivers an update and never says why.
        /// </exception>
        internal static void Require(string name, string parameterName)
        {
            string reason = Describe(name);
            if (reason == null) return;
            throw new ArgumentException(
                $"Lobby name {reason}. The Room Service accepts up to {MaxBytes} " +
                $"characters from {Alphabet}.", parameterName);
        }
    }
}
