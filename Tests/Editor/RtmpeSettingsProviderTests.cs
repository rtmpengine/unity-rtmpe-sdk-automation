// RTMPE SDK — Tests/Editor/RtmpeSettingsProviderTests.cs
//
// Edit-mode smoke tests for the Project / RTMPE settings panel.
//
// We can't drive the SettingsProvider GUI loop directly from a headless test
// runner, but we CAN exercise:
//   1. The provider is one Unity discovers: its factory carries the
//      [SettingsProvider] attribute and builds the panel at its path.  Asked
//      of the factory, because SettingsService.FetchSettingsProviders — what
//      lists the providers Unity found — is internal to the editor, and a call
//      to it does not compile in a project.
//   2. Constructing a SerializedObject around NetworkSettings exposes every
//      public field.  This is the same code path the panel uses to render
//      the Inspector — if it throws or omits a field, the panel breaks.

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Editor;

namespace RTMPE.Tests.Editor
{
    [TestFixture]
    [Category("Editor")]
    public class RtmpeSettingsProviderTests
    {
        [Test]
        [Description("The [SettingsProvider] factory builds the 'Project/RTMPE' provider.")]
        public void Provider_IsDiscoverable_AtProjectRtmpePath()
        {
            var factory = typeof(RtmpeSettingsProvider).GetMethod(
                nameof(RtmpeSettingsProvider.Create), BindingFlags.Public | BindingFlags.Static);
            Assert.That(factory, Is.Not.Null, "RtmpeSettingsProvider.Create is the panel's factory.");
            Assert.That(factory.GetCustomAttributes(typeof(SettingsProviderAttribute), false), Is.Not.Empty,
                "Unity calls a provider's factory only when it carries [SettingsProvider].");

            var rtmpe = RtmpeSettingsProvider.Create();
            Assert.That(rtmpe, Is.Not.Null);
            Assert.That(rtmpe.settingsPath, Is.EqualTo("Project/RTMPE"),
                "RtmpeSettingsProvider must register at 'Project/RTMPE'.");
            Assert.That(rtmpe.label, Is.EqualTo("RTMPE"));
        }

        [Test]
        [Description("All public NetworkSettings fields are reachable through SerializedObject.")]
        public void NetworkSettings_AllFields_AreVisibleViaSerializedObject()
        {
            var asset = ScriptableObject.CreateInstance<NetworkSettings>();
            try
            {
                var so = new SerializedObject(asset);

                // Collect every visible property the panel iterator walks over,
                // excluding the implicit m_Script reference that
                // ScriptableObject.SerializedObject always emits first.
                var visible = new HashSet<string>();
                var it = so.GetIterator();
                if (it.NextVisible(enterChildren: true))
                {
                    do
                    {
                        if (it.propertyPath == "m_Script") continue;
                        visible.Add(it.propertyPath);
                    } while (it.NextVisible(enterChildren: false));
                }

                // Expect every authoritative NetworkSettings field to show up.
                // Update this list whenever a new field is added.
                var required = new[]
                {
                    "serverHost",
                    "serverPort",
                    "heartbeatIntervalMs",
                    "connectionTimeoutMs",
                    "tickRate",
                    "autoRejoinLastRoomOnReconnect",
                    "sendBufferBytes",
                    "receiveBufferBytes",
                    "networkThreadBufferBytes",
                    "enableDebugLogs",
                    "apiKeySealServerPublicKeyHex",
                    "pinnedServerPublicKeyHex",
                    "requirePinnedServerPublicKey",
                };

                foreach (var field in required)
                    Assert.That(visible, Contains.Item(field),
                        $"NetworkSettings field '{field}' must be visible " +
                        "to the Project/RTMPE settings panel.");
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        [Description("Default-constructed NetworkSettings has sensible runtime defaults.")]
        public void NetworkSettings_Defaults_AreSafeForLocalDevelopment()
        {
            var asset = ScriptableObject.CreateInstance<NetworkSettings>();
            try
            {
                Assert.That(asset.serverHost, Is.EqualTo("127.0.0.1"));
                Assert.That(asset.serverPort, Is.InRange(1, 65535));
                Assert.That(asset.tickRate,   Is.GreaterThan(0));
                Assert.That(asset.TickInterval,
                    Is.EqualTo(1f / asset.tickRate).Within(1e-6f));
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }
    }
}
#endif
