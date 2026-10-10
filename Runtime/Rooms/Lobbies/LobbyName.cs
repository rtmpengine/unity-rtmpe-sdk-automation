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
    /// The rule for lobby names: 1 to <see cref="MaxBytes"/> characters from
    /// <see cref="Alphabet"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="LobbyManager.JoinLobby"/>, <see cref="LobbyManager.ListRooms"/> and
    /// matchmaking throw <see cref="ArgumentException"/> for a name that breaks it.
    /// </remarks>
    public static class LobbyName
    {
        /// <summary>
        /// The longest lobby name, in bytes. Every allowed character is one byte.
        /// </summary>
        public const int MaxBytes = 32;

        /// <summary>
        /// The characters a lobby name may contain: ASCII letters, digits, <c>_</c> and
        /// <c>-</c>.
        /// </summary>
        public const string Alphabet = "[A-Za-z0-9_-]";

        /// <summary>
        /// Whether <paramref name="name"/> is a valid lobby name.
        /// </summary>
        public static bool IsValid(string name)
        {
            return Describe(name) == null;
        }

        /// <summary>
        /// Returns why <paramref name="name"/> is not a valid lobby name, or
        /// <see langword="null"/> when it is valid.
        /// </summary>
        /// <remarks>
        /// The reason names the broken rule: an empty name, a name that is too long, or the
        /// first character outside <see cref="Alphabet"/> and its position.
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
