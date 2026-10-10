// RTMPE SDK — Runtime/Core/ReconnectTokenLife.cs
//
// What the gateway said about the reconnect token it last issued, measured
// against this client's own clock from the last moment the gateway is known to
// have held the session.
//
// The lifetime of a reconnect token is the gateway's to know: it is derived
// from the server's session timeout (600 s under the shipped defaults, 180 s
// on a regional pod) and is renewed server-side without the client being told.
// Until the SessionAck carried it, the SDK held a token with no expiry of any
// kind — so a player who backgrounded the app past the server's window spent
// the whole bounded reconnect ladder, five attempts against a ten-second
// watchdog with jittered backoff between them, discovering that a fresh
// handshake was needed: about a minute of "reconnecting" for a resume that was
// never going to succeed (S4-19).
//
// ⛔ The gateway's refusal cannot be what shortens it.  A HandshakeError
// arrives on an unauthenticated frame — no session key exists at that point of
// the flow, by construction — and the SDK deliberately declines to end an
// attempt on one, because a party that can reach this socket would otherwise
// hold a one-datagram kill switch on every connect; a production gateway also
// collapses every error category onto the generic code, so nothing in the
// frame identifies the cause anyway.  What the client can act on without
// trusting anyone is its own clock, which is this file.
//
// 🔑 Measured from the gateway's LAST WORD, not from the issue (audit P1-M6).
// The gateway renews the token of every session it still holds whenever half
// its life has gone (`renew_live_sessions`), so a token's age since the
// SessionAck says nothing once the session has outlived the stated life — which
// against the live gateway's three minutes is nearly every real game.  Read that
// way, every such drop got one attempt instead of the configured ladder.  An
// authenticated frame from the gateway proves it held the session at that
// moment, so the age runs from the newest one.

using System;

namespace RTMPE.Core
{
    /// <summary>
    /// The lifetime the server stated for the current reconnect token, and how
    /// many reconnect attempts that token is worth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="NetworkManager.Reconnect"/> uses this rule to size its attempts:
    /// once the server has not been heard from for longer than the lifetime it
    /// stated, it makes one attempt instead of
    /// <see cref="NetworkSettings.maxReconnectAttempts"/>. The age runs from the
    /// last authenticated frame the server sent, because the server renews the
    /// token of a session it still holds without telling the client. A stated
    /// lifetime is a lower bound rather than an expiry, so a token past it is
    /// still tried once.
    /// </para>
    /// <para>
    /// Time is measured with the wall clock (<see cref="DateTimeOffset.UtcNow"/>),
    /// so time the application spends suspended counts. The SDK keeps its own
    /// instance; you do not need to create one.
    /// </para>
    /// </remarks>
    public sealed class ReconnectTokenLife
    {
        /// <summary>
        /// The number of reconnect attempts made with a token past its stated
        /// lifetime: 1. One attempt still resumes a session whose token the server
        /// renewed.
        /// </summary>
        public const int AttemptsWorthSpendingOnAnOverAgeToken = 1;

        // -1 rather than 0: zero is a lifetime the gateway can state (a token
        // it issued but could not store, refused the moment it is presented),
        // and collapsing that onto "nothing was said" would discard the one
        // statement the client can act on immediately.
        private const long NoStatement = -1L;

        private long _statedLifeSeconds = NoStatement;

        // The newest moment the server is known to have held the session: the
        // token's arrival, then every authenticated frame after it.
        private long _heldAtUnixSeconds;

        /// <summary>
        /// The current wall-clock time in whole Unix seconds, the unit the methods
        /// of this type take.
        /// </summary>
        public static long NowUnixSeconds => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>
        /// Whether the server stated a lifetime for the token currently held.
        /// </summary>
        public bool HasStatement => _statedLifeSeconds >= 0;

        /// <summary>
        /// Records what the server stated about the reconnect token it has just
        /// issued, including that it stated nothing. Not intended to be called from
        /// game code.
        /// </summary>
        /// <param name="gatewayCaps">
        /// The server's capability flags. A lifetime is recorded only when they
        /// include <see cref="RTMPE.Core.Protocol.CapabilityFlags.ReconnectTokenLifetime"/>;
        /// otherwise the recorded lifetime is forgotten.
        /// </param>
        /// <param name="lifetimeSeconds">
        /// The stated lifetime in seconds; 0 means the token is past its lifetime at
        /// once.
        /// </param>
        /// <param name="nowUnixSeconds">The time the token arrived, in Unix seconds.</param>
        public void Record(
            RTMPE.Core.Protocol.CapabilityFlags gatewayCaps,
            uint lifetimeSeconds,
            long nowUnixSeconds)
        {
            if ((gatewayCaps & RTMPE.Core.Protocol.CapabilityFlags.ReconnectTokenLifetime) == 0)
            {
                Forget();
                return;
            }

            _statedLifeSeconds = lifetimeSeconds;
            _heldAtUnixSeconds = nowUnixSeconds;
        }

        /// <summary>
        /// Records that the server held the session at <paramref name="nowUnixSeconds"/>:
        /// an authenticated frame from it has just arrived. The stated lifetime is then
        /// measured from here. Only ever moves forward.
        /// </summary>
        /// <param name="nowUnixSeconds">The time the frame arrived, in Unix seconds.</param>
        internal void Witness(long nowUnixSeconds)
        {
            if (nowUnixSeconds > _heldAtUnixSeconds)
                _heldAtUnixSeconds = nowUnixSeconds;
        }

        /// <summary>
        /// Drops the recorded lifetime. The SDK calls it whenever it drops the
        /// reconnect token.
        /// </summary>
        public void Forget()
        {
            _statedLifeSeconds = NoStatement;
            _heldAtUnixSeconds = 0L;
        }

        /// <summary>
        /// Whether the stated lifetime has passed since the server was last heard from.
        /// </summary>
        /// <param name="nowUnixSeconds">The current time, in Unix seconds; see <see cref="NowUnixSeconds"/>.</param>
        /// <returns>
        /// <see langword="true"/> once the stated lifetime has elapsed;
        /// <see langword="false"/> when no lifetime was stated or the clock moved
        /// backwards.
        /// </returns>
        public bool IsPastStatedLife(long nowUnixSeconds)
        {
            if (!HasStatement) return false;

            long elapsed = nowUnixSeconds - _heldAtUnixSeconds;

            // A clock that moved backwards (a device time change, an NTP step)
            // makes the elapsed span meaningless rather than zero.  Answering
            // "not past" keeps the full ladder, which is the behaviour this
            // type is narrowing — the conservative direction is the one that
            // still tries.
            if (elapsed < 0) return false;

            return elapsed >= _statedLifeSeconds;
        }

        /// <summary>
        /// How many reconnect attempts to make with the token currently held.
        /// </summary>
        /// <param name="configuredBudget">The configured number of attempts; a value below 1 counts as 1.</param>
        /// <param name="nowUnixSeconds">The current time, in Unix seconds; see <see cref="NowUnixSeconds"/>.</param>
        /// <returns>
        /// <paramref name="configuredBudget"/> (at least 1), reduced to
        /// <see cref="AttemptsWorthSpendingOnAnOverAgeToken"/> once the stated
        /// lifetime has passed since the server was last heard from. The budget is never
        /// increased.
        /// </returns>
        public int AttemptBudget(int configuredBudget, long nowUnixSeconds)
        {
            int budget = configuredBudget > 0 ? configuredBudget : 1;
            if (!IsPastStatedLife(nowUnixSeconds)) return budget;
            return budget < AttemptsWorthSpendingOnAnOverAgeToken
                ? budget
                : AttemptsWorthSpendingOnAnOverAgeToken;
        }
    }
}
