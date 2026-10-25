// RTMPE SDK — Editor/RemoteMotionRootReader.cs
//
// What a readiness reader counts on a prefab or scene root.
//
// Two surfaces ask this question of LOADED components — the NetworkTransform
// inspector's advisory and the Network Prefabs window's rows — and both read it
// here.  ⚠️ A third asks it of a serialised file: the prefab scanner in the
// analysis engine under `Tooling/`, which parses YAML rather than loaded
// components, keeps its own statement of the same asymmetry.  Nothing holds the
// two texts to each other, so a change here is owed a reading of that one.
//
// ⚠️ The engine's project name is deliberately not written here.  A package
// source that mentions one is an engine source as far as
// check-automation-kit-in-package is concerned, and Unity compiles everything
// under Editor/ into every consuming project — the gate section B1 of the
// verification report records being turned red by a documentation comment.
//
// Free of UnityEditor: the argument is components already loaded, so nothing
// here needs an asset database, and the rule is reachable from a headless test.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace RTMPE.Editor
{
    /// <summary>
    /// The motion components a root carries, as the readiness readers count
    /// them.
    /// </summary>
    internal static class RemoteMotionRootReader
    {
        /// <summary>
        /// The canonical motion-component names on <paramref name="components"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// ⛔ The two halves of the pair are not read the same way, and the
        /// asymmetry is the whole of what this member states.  The RECEIVING
        /// half is read through its switch: the interpolator writes the
        /// transform from <c>Update</c>, which Unity does not call on a
        /// switched-off behaviour, while <c>GetComponent</c> — the receive
        /// path's own reader — hands one back just the same.  Counting it as
        /// present calls the prefab paired while its replica stands still.
        /// </para>
        /// <para>
        /// The SENDING half is counted whatever its switch, and the sending half is not read that way.  ⛔ Not because a
        /// switched-off sender still sends: its pose broadcast is Update-driven
        /// (<c>SendTransformUpdate</c> has one call site and it is in
        /// <c>Update</c>), so a disabled one ships no pose at all — what still
        /// runs on it is the fixed-tick dispatch, which is gated on
        /// IsAlive/IsOwner/IsSpawned rather than on <c>enabled</c>, and the
        /// variable flush.  It is counted because one that is off today is a
        /// line of somebody's <c>OnNetworkSpawn</c> away from being on: reading
        /// the switch on the sending half buys a reassuring answer that can be
        /// wrong, where reading it on the receiving half buys an accusing one a
        /// reader who meant it can dismiss.
        /// Dropping it from the count turns a prefab that needs an interpolator
        /// into one no reader says anything about at all.
        /// </para>
        /// <para>
        /// A null entry is walked past.  A missing script serialises as one, and
        /// asking it for its type throws out of the drawing call.
        /// </para>
        /// </remarks>
        internal static IReadOnlyList<string> MotionTypeNamesOn(MonoBehaviour[] components)
        {
            // ⛔ null, not an empty list.  ClassifyMotion reads an empty list as
            // "this root carries no NetworkTransform" — a claim about an asset —
            // and null as "nobody read it".  Answering the first for a root that
            // could not be read turns a measurement that did not happen into a
            // clean bill of health.
            if (components == null) return null;

            var names = new List<string>(components.Length);
            for (int i = 0; i < components.Length; i++)
            {
                MonoBehaviour component = components[i];
                if (component == null) continue;

                // The canonical name is resolved BEFORE the switch is read: a
                // project's own subclass is the same component, and a switch
                // read against the declared type counts a customer's subclass as
                // one that will run.
                string name = NetworkPrefabsInventory.CanonicalMotionTypeName(
                    component.GetType());

                if (string.Equals(
                        name,
                        NetworkPrefabsInventory.NetworkTransformInterpolatorTypeName,
                        StringComparison.Ordinal)
                    && !component.enabled)
                {
                    continue;
                }

                names.Add(name);
            }

            return names;
        }
    }
}
