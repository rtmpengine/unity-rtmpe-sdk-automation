// RTMPE SDK — Tests/Runtime/Tier0SecurityTests.cs
//
// NUnit Edit-Mode tests covering Tier-0 security-engineer findings:
//
//   • C-02   — TransferOwnership authorization gate
//   • NEW-PT — JWT structural / temporal validation
//   • NEW-PT — RequiresEncryption allow-list (Disconnect / SessionAck etc.)
//   • H-PT-03 — EnhancedRpcVerifier default policy is non-null
//
// Internal members are reached via [InternalsVisibleTo("RTMPE.SDK.Tests")].

using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using RTMPE.Core;
using RTMPE.Rpc;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Tier0Security")]
    public class Tier0SecurityTests
    {
        private GameObject     _nmGo;
        private NetworkManager _manager;

        private NetworkSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _nmGo    = new GameObject("NetworkManager");
            _manager = _nmGo.AddComponent<NetworkManager>();

            // 🚨 The JWT cases below need a NAMED configuration, not a default.
            // They used to rely on "_settings is null resolves to
            // JwtSignatureAlgorithm.None"; the fallback is Ed25519 — secure by
            // default — so a token with no signature never reached the claims
            // parser and every claim-level case was refused at the signature step
            // (S4-46). None is stated here because these are CLAIM tests: this
            // fixture has no Ed25519 signer, and one does not belong in the shipped
            // package. The signature contract is held by the headless verifier
            // suite, which mints real signatures.
            _settings = ScriptableObject.CreateInstance<NetworkSettings>();
            _settings.jwtSignatureAlgorithm = NetworkSettings.JwtSignatureAlgorithm.None;
            _manager.SetSettingsForTests(_settings);

            EnhancedRpcVerifier.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_nmGo);
            if (_settings != null)
            {
                UnityEngine.Object.DestroyImmediate(_settings);
                _settings = null;
            }
            EnhancedRpcVerifier.Reset();
        }

        // ── C-02 — TransferOwnership authorization ────────────────────────────

        [Test]
        [Description("Transfer to a non-roster string with no host context is rejected.")]
        public void OwnershipTransfer_UnknownTargetWithoutContext_Rejected()
        {
            // Local player is set but no room, so IsMasterClient is false and
            // the new owner cannot match any roster. The object has a current
            // owner so initialAssignment is also false.
            _manager.SetLocalPlayerStringId("p-local");

            var registry  = new NetworkObjectRegistry();
            var ownership = new OwnershipManager(registry, _manager);
            var spawnGo   = new GameObject("nb");
            try
            {
                var nb = spawnGo.AddComponent<TestNetworkBehaviour>();
                nb.Initialize(42UL, "p-other");
                nb.SetSpawned(true);
                registry.Register(nb);

                // Reach the registry via reflection-free path: the spawn
                // manager surface used by the production code.
                var spawnManagerField = typeof(NetworkManager)
                    .GetField("_spawnManager",
                        System.Reflection.BindingFlags.NonPublic
                      | System.Reflection.BindingFlags.Instance);
                spawnManagerField.SetValue(_manager,
                    new SpawnManager(registry, ownership, _manager));

                bool authorised = _manager.IsOwnershipTransferAuthorized(
                    objectId: 42UL,
                    newOwner: "attacker-supplied",
                    senderId: 99UL);
                Assert.IsFalse(authorised,
                    "Grant from unknown sender to unknown owner must be refused.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(spawnGo);
            }
        }

        [Test]
        [Description("Initial assignment from no-owner is permitted.")]
        public void OwnershipTransfer_InitialAssignment_Accepted()
        {
            _manager.SetLocalPlayerStringId("p-local");

            var registry  = new NetworkObjectRegistry();
            var ownership = new OwnershipManager(registry, _manager);
            var spawnGo   = new GameObject("nb");
            try
            {
                var nb = spawnGo.AddComponent<TestNetworkBehaviour>();
                nb.Initialize(43UL, "");
                nb.SetSpawned(true);
                registry.Register(nb);

                typeof(NetworkManager)
                    .GetField("_spawnManager",
                        System.Reflection.BindingFlags.NonPublic
                      | System.Reflection.BindingFlags.Instance)
                    .SetValue(_manager,
                        new SpawnManager(registry, ownership, _manager));

                bool authorised = _manager.IsOwnershipTransferAuthorized(
                    objectId: 43UL,
                    newOwner: "anyone",
                    senderId: 0UL);
                Assert.IsTrue(authorised,
                    "First-time assignment to a no-owner object must be allowed.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(spawnGo);
            }
        }

        [Test]
        [Description("Initial assignment to a non-roster newOwner is rejected.")]
        public void OwnershipTransfer_InitialAssignment_NonRosterTarget_Rejected()
        {
            // No room is configured, so IsRosterMember returns false for every
            // candidate. Initial assignment must therefore fail even though the
            // object has no current owner: the gateway is the only authority that
            // can introduce a new player into the ownership graph, and an attacker
            // who supplies an arbitrary string for `newOwner` must not slip past
            // the check just because the slot is unowned.
            _manager.SetLocalPlayerStringId("p-local");

            var registry  = new NetworkObjectRegistry();
            var ownership = new OwnershipManager(registry, _manager);
            var spawnGo   = new GameObject("nb");
            try
            {
                var nb = spawnGo.AddComponent<TestNetworkBehaviour>();
                nb.Initialize(44UL, "");
                nb.SetSpawned(true);
                registry.Register(nb);

                typeof(NetworkManager)
                    .GetField("_spawnManager",
                        System.Reflection.BindingFlags.NonPublic
                      | System.Reflection.BindingFlags.Instance)
                    .SetValue(_manager,
                        new SpawnManager(registry, ownership, _manager));

                bool authorised = _manager.IsOwnershipTransferAuthorized(
                    objectId: 44UL,
                    newOwner: "ghost-not-in-roster",
                    senderId: 0UL);
                Assert.IsFalse(authorised,
                    "Initial assignment must require the newOwner to be a roster member.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(spawnGo);
            }
        }

        // ── JWT validation ────────────────────────────────────────────────────

        [Test]
        [Description("Empty JWT is rejected with a descriptive error.")]
        public void Jwt_Empty_Rejected()
        {
            bool ok = _manager.TryValidateJwt(
                "", null, null,
                identityVerificationKey: null,
                out _, out string err);
            Assert.IsFalse(ok);
            Assert.IsNotNull(err);
        }

        [Test]
        [Description("Malformed JWT (not three segments) is rejected.")]
        public void Jwt_NotThreeSegments_Rejected()
        {
            Assert.IsFalse(_manager.TryValidateJwt(
                "header.body", null, null,
                identityVerificationKey: null,
                out _, out _));
            Assert.IsFalse(_manager.TryValidateJwt(
                "a.b.c.d", null, null,
                identityVerificationKey: null,
                out _, out _));
        }

        [Test]
        [Description("Expired JWT is rejected.")]
        public void Jwt_Expired_Rejected()
        {
            // The unsigned-token advisory (a Debug.LogError) fires on the no-pin
            // branch this fixture configures, and Unity fails a test on an
            // unexpected error entry. See Jwt_Valid_ReturnsSub for why it is
            // ignored rather than expected.
            LogAssert.ignoreFailingMessages = true;
            try
            {
                // exp = 1 (epoch second 1, January 1970).
                string jwt = MakeJwt("{\"sub\":\"123\",\"exp\":1}");
                Assert.IsFalse(_manager.TryValidateJwt(
                    jwt, null, null,
                    identityVerificationKey: null,
                    out _, out string err));
                StringAssert.Contains("exp", err);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
        }

        [Test]
        [Description("Issuer mismatch is rejected when expectedIssuer is set.")]
        public void Jwt_IssuerMismatch_Rejected()
        {
            LogAssert.ignoreFailingMessages = true;
            try
            {
                string jwt = MakeJwt(
                    "{\"sub\":\"123\",\"iss\":\"https://attacker.example\"," +
                    "\"exp\":" + FarFutureExp() + "}");
                Assert.IsFalse(_manager.TryValidateJwt(
                    jwt, "https://gateway.example", null,
                    identityVerificationKey: null,
                    out _, out string err));
                StringAssert.Contains("iss", err);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
        }

        [Test]
        [Description("Valid JWT with future exp returns the sub claim.")]
        public void Jwt_Valid_ReturnsSub()
        {
            // The unsigned-token advisory is a Debug.LogError — deliberately, so CI
            // log scraping sees an unverified deployment — and Unity fails a test on
            // an unexpected error entry. Ignored here rather than expected: the
            // advisory is latched once per AppDomain, so whether THIS case observes
            // it depends on test order, which is not a property worth asserting.
            LogAssert.ignoreFailingMessages = true;
            try
            {
                string jwt = MakeJwt(
                    "{\"sub\":\"7777\",\"iss\":\"gw\",\"exp\":" + FarFutureExp() + "}");
                Assert.IsTrue(_manager.TryValidateJwt(
                    jwt, "gw", null,
                    identityVerificationKey: null,
                    out string sub, out string err),
                    "valid token must validate (err=" + err + ")");
                Assert.AreEqual("7777", sub);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
        }

        [Test]
        [Description("alg=none is rejected even when no signing pin is configured.")]
        public void Jwt_AlgNone_RejectedUnconditionally()
        {
            // The fixture configures jwtSignatureAlgorithm = None explicitly (SetUp),
            // so this case is the one configuration in which the alg gate is the SOLE
            // refusal: with a pin, an unsigned token is refused by the signature step
            // anyway and the case would assert nothing about the gate.
            // 🚨 It used to rely on `_settings == null` "resolving to None". It does
            // not — ResolveVerificationKey answers Ed25519 for null settings, secure
            // by default — so this case passed while asserting nothing, and the three
            // claim-level cases failed.
            string jwt = MakeJwtWithHeader(
                "{\"alg\":\"none\"}",
                "{\"sub\":\"123\",\"exp\":" + FarFutureExp() + "}");
            // Suppress the no-pin advisory in case the alg-none gate ever regresses
            // and the algMode==None branch is reached.
            LogAssert.ignoreFailingMessages = true;
            try
            {
                Assert.IsFalse(_manager.TryValidateJwt(
                jwt, null, null,
                identityVerificationKey: null,
                out _, out string err));
                Assert.IsNotNull(err);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
        }

        [Test]
        [Description("Missing/empty alg header is rejected unconditionally.")]
        public void Jwt_AlgMissing_RejectedUnconditionally()
        {
            string jwt = MakeJwtWithHeader(
                "{}",
                "{\"sub\":\"123\",\"exp\":" + FarFutureExp() + "}");
            LogAssert.ignoreFailingMessages = true;
            try
            {
                Assert.IsFalse(_manager.TryValidateJwt(
                jwt, null, null,
                identityVerificationKey: null,
                out _, out string err));
                Assert.IsNotNull(err);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }

            string emptyAlg = MakeJwtWithHeader(
                "{\"alg\":\"\"}",
                "{\"sub\":\"123\",\"exp\":" + FarFutureExp() + "}");
            LogAssert.ignoreFailingMessages = true;
            try
            {
                Assert.IsFalse(_manager.TryValidateJwt(
                emptyAlg, null, null,
                identityVerificationKey: null,
                out _, out _));
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
        }

        [Test]
        [Description("Token without an exp claim is rejected (exp is mandatory).")]
        public void Jwt_NoExp_Rejected()
        {
            // Header alg is non-"none" so the structural alg-none gate passes and
            // the claims parser is reached. The fixture configures
            // jwtSignatureAlgorithm = None, so the verifier takes the no-pin advisory
            // branch (warning is expected, not asserted) rather than demanding a
            // signature — which is what `_settings == null` actually does.
            LogAssert.ignoreFailingMessages = true;
            try
            {
                string noExp = MakeJwtWithHeader(
                    "{\"alg\":\"HS256\"}",
                    "{\"sub\":\"123\"}");
                Assert.IsFalse(_manager.TryValidateJwt(
                noExp, null, null,
                identityVerificationKey: null,
                out _, out string err));
                StringAssert.Contains("exp", err);

                string zeroExp = MakeJwtWithHeader(
                    "{\"alg\":\"HS256\"}",
                    "{\"sub\":\"123\",\"exp\":0}");
                Assert.IsFalse(_manager.TryValidateJwt(
                zeroExp, null, null,
                identityVerificationKey: null,
                out _, out string err2));
                StringAssert.Contains("exp", err2);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
        }

        [Test]
        [Description("Empty expectedIssuer still validates a good token but emits a one-shot advisory.")]
        public void Jwt_IssuerUnconfigured_PassesAndWarns()
        {
            // Reset the per-AppDomain latches so this fixture re-observes the
            // advisories regardless of previous test ordering.
            NetworkManager.ResetJwtIssuerUnconfiguredWarningForTests();
            NetworkManager.ResetJwtAudienceUnconfiguredWarningForTests();

            // The signature-unverified advisory also fires (the fixture configures
            // algMode=None); ignore it so the test focuses on the iss/aud advisories.
            LogAssert.ignoreFailingMessages = true;
            try
            {
                LogAssert.Expect(LogType.Error,
                    new System.Text.RegularExpressions.Regex(
                        "jwtExpectedIssuer is empty"));
                LogAssert.Expect(LogType.Error,
                    new System.Text.RegularExpressions.Regex(
                        "jwtExpectedAudience is empty"));

                string jwt = MakeJwtWithHeader(
                    "{\"alg\":\"HS256\"}",
                    "{\"sub\":\"42\",\"exp\":" + FarFutureExp() + "}");

                Assert.IsTrue(_manager.TryValidateJwt(
                jwt, expectedIssuer: null, expectedAudience: null,
                identityVerificationKey: null,
                out string sub,
                        out string err),
                    "valid token must still validate when iss/aud are unconfigured (err=" + err + ")");
                Assert.AreEqual("42", sub);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
        }

        // ── RequiresEncryption allow-list ─────────────────────────────────────

        [Test]
        [Description("Disconnect must require AEAD encryption once session is up.")]
        public void RequiresEncryption_Disconnect_True()
        {
            Assert.IsTrue(NetworkManager.RequiresEncryption(PacketType.Disconnect));
        }

        [Test]
        [Description("SessionAck must require encryption (channel-bound identifiers).")]
        public void RequiresEncryption_SessionAck_True()
        {
            Assert.IsTrue(NetworkManager.RequiresEncryption(PacketType.SessionAck));
        }

        [Test]
        [Description("Pre-handshake packet types are exempt because no key exists yet.")]
        public void RequiresEncryption_PreHandshake_False()
        {
            Assert.IsFalse(NetworkManager.RequiresEncryption(PacketType.HandshakeInit));
            Assert.IsFalse(NetworkManager.RequiresEncryption(PacketType.Challenge));
            Assert.IsFalse(NetworkManager.RequiresEncryption(PacketType.HandshakeResponse));
            Assert.IsFalse(NetworkManager.RequiresEncryption(PacketType.HandshakeAck));
            Assert.IsFalse(NetworkManager.RequiresEncryption(PacketType.ReconnectInit));
        }

        // ── H-PT-03 — Default sender verifier ─────────────────────────────────

        [Test]
        [Description("Default SenderVerifier rejects sender_id == 0.")]
        public void Sender_Zero_Rejected()
        {
            Assert.IsFalse(EnhancedRpcVerifier.IsSenderAcceptable(0UL));
        }

        [Test]
        [Description("Default SenderVerifier accepts non-zero sender_id (with warning).")]
        public void Sender_NonZero_Accepted()
        {
            Assert.IsTrue(EnhancedRpcVerifier.IsSenderAcceptable(5UL));
        }

        [Test]
        [Description("Default verifier hook is non-null after Reset (never bypassed).")]
        public void DefaultVerifier_NotNullAfterReset()
        {
            EnhancedRpcVerifier.Reset();
            Assert.IsNotNull(EnhancedRpcVerifier.SenderVerifier,
                "Default policy must be installed; null = silent bypass.");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        // Encode a JWT with an arbitrary header/sig but a real claims body.
        // 🚨 A non-"none" alg, and the reason is the whole of S4-46: RFC 8725 §3.1
        // rejection of `alg=none` runs BEFORE any claim is parsed, so every case
        // built on a `none` header was refused at the signature gate with the
        // generic failure string — the expired case could not see "exp" in the
        // error, the issuer case could not see "iss", and the valid case got false.
        // The cases written after that gate landed already used "HS256" here; these
        // three were left behind.
        private static string MakeJwt(string claimsJson)
            => MakeJwtWithHeader("{\"alg\":\"HS256\"}", claimsJson);

        // Encode a JWT with caller-controlled header JSON. Required for tests
        // that need to drive the alg-gate (alg=none / missing alg) directly.
        private static string MakeJwtWithHeader(string headerJson, string claimsJson)
        {
            string header = Base64Url(headerJson);
            string body   = Base64Url(claimsJson);
            // Signature segment is structurally required but never inspected
            // when algMode=None; tests that pass a pin must override this.
            string sig    = Base64Url("sig");
            return header + "." + body + "." + sig;
        }

        private static string Base64Url(string s)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(s);
            string b64 = Convert.ToBase64String(bytes);
            return b64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static long FarFutureExp()
            => DateTimeOffset.UtcNow.AddYears(10).ToUnixTimeSeconds();

        // Test double — minimal NetworkBehaviour subclass.
        private sealed class TestNetworkBehaviour : NetworkBehaviour { }
    }
}
