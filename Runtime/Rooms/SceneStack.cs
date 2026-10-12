// RTMPE SDK — Runtime/Rooms/SceneStack.cs
//
// The scenes a room has open, for a player who arrives after they were loaded.
//
// `__scene` names the LATEST scene the room was told to load and
// `__scene_additive` the mode of that one load, so after `LoadScene("Arena")`
// and `LoadScene("HUD", Additive)` a player who joins is told `HUD:Additive`
// and nothing else — it loads the layer over whatever scene it is in, and
// nothing on any side notices (audit P7-E3).  `__scene_stack` is the rest of
// the answer: the scene of the room's last single-mode load, then every scene
// loaded additively on top of it, in order.  The host writes it beside the
// other two in the same write; a single-mode load removes it, because there is
// nothing under a single-mode scene.
//
// 🔑 It is read only when it agrees with `__scene`: last entry equal to the
// room's scene, and the room's mode additive.  A host running an SDK that
// predates the key writes `__scene` alone, and a stack left over from before
// that write describes a room that has since moved on — announcing it would load
// scenes the room is no longer in.  A stack that does not agree is ignored, and
// the joiner is told the latest scene as it always was.
//
// Pure: names no Unity type, so the decisions are driven by a shard.

using System;
using System.Collections.Generic;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Reads and writes the room's <see cref="ReservedPropertyKeys.SceneStack"/>.
    /// </summary>
    internal static class SceneStack
    {
        /// <summary>
        /// Between two scenes in the stored value.  A scene name or path never
        /// holds a line break.
        /// </summary>
        internal const char Separator = '\n';

        /// <summary>One scene a player entering the room loads, and how.</summary>
        internal readonly struct Load
        {
            public string Scene { get; }
            public NetworkSceneLoadMode Mode { get; }

            public Load(string scene, NetworkSceneLoadMode mode)
            {
                Scene = scene;
                Mode  = mode;
            }
        }

        /// <summary>Whether the room's latest scene was loaded additively.</summary>
        internal static bool IsAdditive(RoomInfo room)
        {
            var properties = room?.Properties;
            return properties != null
                && properties.TryGetValue(ReservedPropertyKeys.SceneAdditive, out var flag)
                && flag.Type == PropertyType.Bool
                && flag.AsBool();
        }

        /// <summary>
        /// The scenes the room has open, its single-mode scene first, when the
        /// room's stack agrees with its scene; <see langword="null"/> otherwise.
        /// </summary>
        internal static string[] Of(RoomInfo room)
        {
            var properties = room?.Properties;
            if (properties == null
                || !properties.TryGetValue(ReservedPropertyKeys.SceneStack, out var stored)
                || stored.Type != PropertyType.String)
            {
                return null;
            }

            string text = stored.AsString();
            if (string.IsNullOrEmpty(text)) return null;

            var scenes = text.Split(Separator);
            if (scenes.Length < 2) return null;
            for (int i = 0; i < scenes.Length; i++)
            {
                if (string.IsNullOrEmpty(scenes[i])) return null;
            }

            if (!string.Equals(scenes[scenes.Length - 1], room.CurrentScene, StringComparison.Ordinal)) return null;
            if (!IsAdditive(room)) return null;
            return scenes;
        }

        /// <summary>
        /// What a player entering <paramref name="room"/> loads, in order: every
        /// scene in the room's stack — the first in single mode, the rest
        /// additively — or, with no stack that agrees, the room's scene in the
        /// room's mode.  Empty when the room has no scene.
        /// </summary>
        internal static IReadOnlyList<Load> EntryLoads(RoomInfo room)
        {
            string scene = room?.CurrentScene;
            if (string.IsNullOrEmpty(scene)) return Array.Empty<Load>();

            var scenes = Of(room);
            if (scenes == null)
            {
                return new[]
                {
                    new Load(scene, IsAdditive(room) ? NetworkSceneLoadMode.Additive : NetworkSceneLoadMode.Single),
                };
            }

            var loads = new Load[scenes.Length];
            loads[0] = new Load(scenes[0], NetworkSceneLoadMode.Single);
            for (int i = 1; i < scenes.Length; i++)
                loads[i] = new Load(scenes[i], NetworkSceneLoadMode.Additive);
            return loads;
        }

        /// <summary>
        /// Whether a write loading <paramref name="scene"/> in the given mode
        /// carries the stack key, read from <paramref name="room"/> as it stands
        /// before it; <paramref name="value"/> is what it carries — the stack, or
        /// <see cref="PropertyValue.Deletion"/> — and <paramref name="fits"/> is
        /// false when the load called for a stack the write cannot store.
        /// </summary>
        /// <remarks>
        /// <para>A single-mode load removes the stack: nothing is under it.</para>
        /// <para>An additive load adds the scene to the stack that agrees with the
        /// room, or — when the room's latest load was single-mode — starts one on
        /// that scene. A scene already in the stack moves to its top rather than
        /// appearing twice: the loader does not open a second copy, so the write
        /// restarts the round, and the stack is read only while its last scene is
        /// the room's — left where it was, a layer re-issued from under another
        /// would leave the stack disagreeing with the room, and a joiner told the
        /// layer alone. With neither — the latest scene additive and no
        /// stack that agrees, which a host running an SDK without the key leaves
        /// behind — the scene under it is unknown, and no stack is written; one
        /// stored is removed, because it no longer describes the room.</para>
        /// <para>⛔ The stack is a room property, so it is bounded twice: by the value
        /// cap, measured as the server measures it, and by the room's key cap,
        /// which counts reserved keys like any other. A write the server would
        /// refuse for either is refused WHOLE — the scene with it — so a stack
        /// that cannot be stored is left out rather than sent, a stored one is
        /// removed, and <paramref name="fits"/> says so.</para>
        /// </remarks>
        internal static bool Next(
            RoomInfo room, string scene, NetworkSceneLoadMode mode,
            out PropertyValue value, out bool fits)
        {
            value = PropertyValue.Deletion();
            fits  = true;
            var properties = room?.Properties;
            bool stored = properties != null && properties.ContainsKey(ReservedPropertyKeys.SceneStack);

            if (mode != NetworkSceneLoadMode.Additive) return stored;

            List<string> scenes = null;
            var agreed = Of(room);
            if (agreed != null)
            {
                scenes = new List<string>(agreed);
                scenes.Remove(scene);
                scenes.Add(scene);
            }
            else
            {
                string current = room?.CurrentScene;
                if (!string.IsNullOrEmpty(current) && !IsAdditive(room)
                    && !string.Equals(current, scene, StringComparison.Ordinal))
                {
                    scenes = new List<string> { current, scene };
                }
            }

            // A single-mode scene re-issued additively over itself is no layer.
            if (scenes == null || scenes.Count < 2) return stored;

            var candidate = PropertyValue.OfString(string.Join(Separator.ToString(), scenes));
            if (PropertyJsonSizing.ServerValueBytes(candidate) > PropertyLimits.MaxValueBytes
                || (!stored && KeysAfterTheWrite(properties) > PropertyLimits.MaxPropertiesPerRoom))
            {
                fits = false;
                return stored;
            }

            value = candidate;
            return true;
        }

        // How many keys the room holds once a scene write that adds the stack has
        // been applied: the three scene keys, plus every key it holds that is not
        // one of them.
        private static int KeysAfterTheWrite(IReadOnlyDictionary<string, PropertyValue> properties)
        {
            int others = 0;
            if (properties != null)
            {
                foreach (var key in properties.Keys)
                {
                    if (key == ReservedPropertyKeys.Scene
                        || key == ReservedPropertyKeys.SceneAdditive
                        || key == ReservedPropertyKeys.SceneStack)
                    {
                        continue;
                    }
                    others++;
                }
            }
            return others + 3;
        }
    }
}
