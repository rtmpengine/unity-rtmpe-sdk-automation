// RTMPE SDK — Runtime/Rpc/RpcRegistry.cs
//
// Discovers [RtmpeRpc]-decorated methods by reflection and maps them to stable
// wire method IDs using FNV-1a 32-bit hashing of "QualifiedTypeName.MethodName".
//
// Design decisions:
//  • Identity scope: the type's metadata FULL name, never its unqualified one.
//    An Enhanced RPC frame carries [object_id][method_id] and no component
//    discriminator, so the id must be unique across everything mounted on one
//    object — and two types of one short name in different namespaces are an
//    ordinary way to write a game, not a pathology.  The scope is derived by
//    RTMPE.Core.WireIdHash.ScopeOf, which the NetworkVariable identity shares.
//  • Lazy per-type discovery: a type's methods are scanned on first access,
//    not at app startup.  This avoids Assembly.GetTypes() over all loaded
//    assemblies (expensive on IL2CPP) and eliminates ordering dependencies.
//  • Thread safety: the _cache dictionary is guarded by a lock.  Unity main-
//    thread callers (which is the only supported call site) never contend.
//  • Collision guard: Validate(type) checks that none of the FNV-1a hashes
//    collide with each other or with the reserved manual RpcMethodId constants.
//    Call Validate() from NetworkBehaviour.OnNetworkSpawn to catch problems
//    at object spawn time rather than at first RPC invocation.
//  • Method name uniqueness: two [RtmpeRpc] methods with the same name on the
//    same type produce a hash collision — Validate() treats that as a fatal
//    configuration error (not an overload system).

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Computes RPC method ids, finds the <see cref="RtmpeRpcAttribute"/> methods of a type,
    /// and checks them for id collisions.
    /// </summary>
    /// <remarks>
    /// A method's id is a 32-bit FNV-1a hash of the full name of the component type and the
    /// method name. A method inherited from a base class takes its id from the type it is called
    /// on.
    /// </remarks>
    public static class RpcRegistry
    {
        // ── Reserved manual method IDs that FNV-1a hashes must not collide with
        private static readonly HashSet<uint> ReservedIds = new HashSet<uint>
        {
            RpcMethodId.Ping,
            RpcMethodId.TransferOwnership,
            RpcMethodId.RequestDamage,
            RpcMethodId.ApplyDamage,
            RpcMethodId.GameStateChange,
            RpcMethodId.SyncGameState,
        };

        // ── Per-type cache: Type → Dictionary<methodId, (MethodInfo, attr)> ──
        private static readonly Dictionary<Type, Dictionary<uint, (MethodInfo Method, RtmpeRpcAttribute Attr)>> _cache
            = new Dictionary<Type, Dictionary<uint, (MethodInfo Method, RtmpeRpcAttribute Attr)>>();

        // Types whose [RtmpeRpc] table cannot be built (a reserved-id or intra-type
        // collision that BuildMap rejects with a throw).  BuildMap only writes the
        // success cache, so without this a collided type would re-run its whole
        // reflection scan — and re-throw — on every OwnsMethod probe.  Recording it
        // once bounds the failure to a single scan per type.
        private static readonly HashSet<Type> _unmappable = new HashSet<Type>();

        private static readonly object _lock = new object();

        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetCache()
        {
            lock (_lock) { _cache.Clear(); _unmappable.Clear(); }
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the id of the RPC method <paramref name="methodName"/> of the type named
        /// <paramref name="typeName"/>.
        /// </summary>
        /// <param name="typeName">
        /// The type's full name, as <see cref="Type.FullName"/> gives it. A name without its
        /// namespace gives a different id; prefer <see cref="ComputeMethodId(Type, string)"/>.
        /// </param>
        /// <param name="methodName">The method's name.</param>
        /// <returns>The method id, which your server function receives as
        /// <c>method_id</c>.</returns>
        public static uint ComputeMethodId(string typeName, string methodName)
            => RTMPE.Core.WireIdHash.Of(typeName, methodName);

        /// <summary>
        /// Returns the id of the RPC method <paramref name="methodName"/> of
        /// <paramref name="type"/>.
        /// </summary>
        /// <param name="type">The type the method is called on.</param>
        /// <param name="methodName">The method's name.</param>
        /// <returns>The method id, which your server function receives as
        /// <c>method_id</c>.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="type"/> is <see langword="null"/>.
        /// </exception>
        public static uint ComputeMethodId(Type type, string methodName)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            return RTMPE.Core.WireIdHash.Of(RTMPE.Core.WireIdHash.ScopeOf(type), methodName);
        }

        /// <summary>
        /// Finds the RPC method of <paramref name="type"/> with the id
        /// <paramref name="methodId"/>, and its <see cref="RtmpeRpcAttribute"/>.
        /// </summary>
        /// <returns><see langword="false"/> when the type has no RPC method with that
        /// id.</returns>
        /// <exception cref="InvalidOperationException">The type's RPC methods have colliding
        /// ids (see <see cref="Validate"/>).</exception>
        public static bool TryFindMethod(
            Type type,
            uint methodId,
            out MethodInfo method,
            out RtmpeRpcAttribute attr)
        {
            var map = GetOrBuild(type);
            if (map.TryGetValue(methodId, out var entry))
            {
                method = entry.Method;
                attr   = entry.Attr;
                return true;
            }

            method = null;
            attr   = null;
            return false;
        }

        /// <summary>
        /// Finds which of <paramref name="candidateTypes"/> declares the RPC method with the id
        /// <paramref name="methodId"/>. Used by the SDK to deliver received calls; not intended
        /// to be called from game code.
        /// </summary>
        /// <remarks>
        /// The type at <paramref name="anchorIndex"/> is checked first and wins when it declares
        /// the method. Otherwise the only other type that declares it is chosen; when two or
        /// more do, none is. Null entries, and types whose RPC ids collide, declare nothing.
        /// </remarks>
        /// <param name="candidateTypes">
        /// The component types on the addressed object.
        /// </param>
        /// <param name="anchorIndex">
        /// The index of the type to check first, or a negative value for none.
        /// </param>
        /// <param name="methodId">The method id.</param>
        /// <param name="index">The index of the declaring type, or -1.</param>
        /// <returns>
        /// <see langword="true"/> when exactly one type is chosen; otherwise
        /// <see langword="false"/>, with <paramref name="index"/> -1.
        /// </returns>
        public static bool TryResolveOwningType(
            IReadOnlyList<Type> candidateTypes, int anchorIndex, uint methodId, out int index)
        {
            index = -1;
            if (candidateTypes == null) return false;

            // Anchor precedence: the registered routing component wins outright,
            // so an id it owns never triggers the ambiguity scan below.
            if (anchorIndex >= 0 && anchorIndex < candidateTypes.Count
                && OwnsMethod(candidateTypes[anchorIndex], methodId))
            {
                index = anchorIndex;
                return true;
            }

            for (int i = 0; i < candidateTypes.Count; i++)
            {
                if (i == anchorIndex || !OwnsMethod(candidateTypes[i], methodId)) continue;
                if (index >= 0)
                {
                    // A second owner — the id maps to more than one component and
                    // the wire cannot say which. Refuse rather than guess.
                    index = -1;
                    return false;
                }
                index = i;
            }
            return index >= 0;
        }

        /// <summary>
        /// Whether <paramref name="type"/> declares an [RtmpeRpc] method with
        /// <paramref name="methodId"/>, absorbing the collision throw a malformed
        /// type would otherwise raise from <see cref="TryFindMethod"/> — a type the
        /// registry refuses to map cannot own a dispatchable id.
        /// </summary>
        private static bool OwnsMethod(Type type, uint methodId)
        {
            if (type == null) return false;

            // A type already known to be unmappable can own no dispatchable id, and
            // re-probing it would re-run BuildMap's reflection scan and re-throw on
            // every packet — short-circuit before touching the registry.
            lock (_lock)
            {
                if (_unmappable.Contains(type)) return false;
            }

            try
            {
                return TryFindMethod(type, methodId, out _, out _);
            }
            catch (InvalidOperationException)
            {
                lock (_lock) { _unmappable.Add(type); }
                return false;
            }
        }

        /// <summary>
        /// Finds the id of the RPC method <paramref name="methodName"/> of
        /// <paramref name="type"/>.
        /// </summary>
        /// <returns><see langword="false"/> when <paramref name="type"/> has no
        /// <see cref="RtmpeRpcAttribute"/> method of that name.</returns>
        /// <exception cref="InvalidOperationException">The type's RPC methods have colliding
        /// ids (see <see cref="Validate"/>).</exception>
        public static bool TryGetMethodId(Type type, string methodName, out uint methodId)
        {
            methodId = ComputeMethodId(type, methodName);
            var map  = GetOrBuild(type);
            return map.ContainsKey(methodId);
        }

        /// <summary>
        /// Checks the RPC methods of <paramref name="type"/> for ids that collide with a
        /// reserved id (<see cref="RpcMethodId"/>) or with each other.
        /// </summary>
        /// <remarks>
        /// The SDK runs this check when an object of the type first spawns, and logs a failure
        /// as an error; that type's RPCs are then not delivered. The analyzers report the same
        /// problems while you edit.
        /// </remarks>
        /// <param name="type">The type to check.</param>
        /// <exception cref="ArgumentNullException"><paramref name="type"/> is
        /// <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">
        /// One or more ids collide. The message lists every colliding method.
        /// </exception>
        public static void Validate(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            var collisions = CollectCollisions(type);
            if (collisions.Count == 0) return;

            var sb = new StringBuilder();
            sb.Append("[RTMPE] RpcRegistry.Validate: ");
            sb.Append(collisions.Count);
            sb.Append(" RPC method ID collision(s) detected on type '");
            sb.Append(RTMPE.Core.WireIdHash.ScopeOf(type));
            sb.Append("':");
            foreach (var c in collisions)
            {
                sb.Append("\n  • ");
                sb.Append(c);
            }
            sb.Append("\nRename the conflicting [RtmpeRpc] methods to resolve.");
            throw new InvalidOperationException(sb.ToString());
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static Dictionary<uint, (MethodInfo Method, RtmpeRpcAttribute Attr)> GetOrBuild(Type type)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(type, out var existing))
                    return existing;

                var map = BuildMap(type);
                _cache[type] = map;
                return map;
            }
        }

        private static Dictionary<uint, (MethodInfo Method, RtmpeRpcAttribute Attr)> BuildMap(Type type)
        {
            var map = new Dictionary<uint, (MethodInfo Method, RtmpeRpcAttribute Attr)>();

            // The scope every id on this type is derived from, read once so the
            // table and the messages that describe it cannot disagree.
            string scope = RTMPE.Core.WireIdHash.ScopeOf(type);

            // Scan public instance methods INCLUDING those inherited from
            // base classes.  The hash key is `ComputeMethodId(type,
            // method.Name)` — keyed on the runtime type, not the declaring
            // one — so a [RtmpeRpc] method declared on a base class
            // participates in dispatch under the runtime subtype's hash on
            // both sides of the wire.  Without inheritance, a
            // common OO pattern (BasePlayer : NetworkBehaviour declaring
            // a shared `[RtmpeRpc] Damage(int)` that subclasses inherit)
            // would silently fail at dispatch time on every Subclass
            // instance.  C#'s method-resolution rules handle override /
            // shadow correctly: GetMethods returns the most-derived
            // implementation for virtual methods, and shadowed methods
            // surface as the derived-type's declaration — which is the
            // intended dispatch target in both cases.
            var methods = type.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Public);

            foreach (var mi in methods)
            {
                // inherit: true is a defensive default only: [RtmpeRpc] is
                // declared Inherited=false, so an override that drops the
                // attribute is intentionally not dispatched — a method registers
                // only when it carries the attribute on its own declaration.
                var attr = mi.GetCustomAttribute<RtmpeRpcAttribute>(inherit: true);
                if (attr == null) continue;

                uint id = ComputeMethodId(scope, mi.Name);

                // Check against reserved manual IDs.
                if (ReservedIds.Contains(id))
                {
                    // Hard-throw: a reserved-id collision means the SDK
                    // cannot dispatch this method without overlapping a
                    // built-in.  Silently dropping the entry would let the
                    // caller invoke the method and never see it fire.  The
                    // SDK refuses to start until the developer renames it.
                    throw new InvalidOperationException(
                        $"[RTMPE] RpcRegistry: method '{scope}.{mi.Name}' produces FNV-1a " +
                        $"hash 0x{id:X8} which collides with a reserved RpcMethodId. " +
                        "Rename the method to resolve the collision.");
                }

                // Check for intra-type collision (same type, two methods hash to same ID).
                if (map.TryGetValue(id, out var existing))
                {
                    // Object's `Equals` / `GetHashCode` / `ToString` /
                    // `MemberwiseClone` etc. are inherited by every type —
                    // they do not carry [RtmpeRpc] so they never reach
                    // here.  Any duplicate at this point is therefore a
                    // genuine same-name [RtmpeRpc] definition (e.g. an
                    // overload) which is unsupported by the FNV-keyed
                    // dispatch table.  Hard-throw so the developer cannot
                    // ship a binary in which one of the two collided
                    // methods would silently never dispatch.
                    throw new InvalidOperationException(
                        $"[RTMPE] RpcRegistry: method '{scope}.{mi.Name}' has FNV-1a " +
                        $"hash 0x{id:X8} that collides with [RtmpeRpc] method " +
                        $"'{scope}.{existing.Method.Name}' on the same type. " +
                        "Rename one of the methods to resolve the collision.");
                }

                map[id] = (mi, attr);
            }

            return map;
        }

        /// <summary>
        /// Re-scans <paramref name="type"/> from scratch (independent of the
        /// per-type cache built by <see cref="BuildMap"/>) and returns the list
        /// of collision descriptions.  Empty list ⇒ no collisions.
        /// </summary>
        /// <remarks>
        /// We deliberately do NOT consult <see cref="GetOrBuild"/> here:
        /// <see cref="BuildMap"/> THROWS on the first collision it meets, so the
        /// cache holds a map only for types that have none, and asking it would
        /// answer the question by raising rather than by reporting — this method
        /// exists to hand <see cref="Validate"/> every collision at once.
        /// Re-scanning is O(methods) and runs once per type at that type's first
        /// <c>NetworkBehaviour.OnNetworkSpawn</c> — negligible.
        /// </remarks>
        private static List<string> CollectCollisions(Type type)
        {
            var collisions = new List<string>();
            var seen       = new Dictionary<uint, string>();
            string scope   = RTMPE.Core.WireIdHash.ScopeOf(type);

            // Match BuildMap's discovery scope so the validator and the
            // dispatch path see the same set of [RtmpeRpc] methods.
            // Inherited [RtmpeRpc] attributes participate in dispatch and
            // therefore must participate in collision validation too.
            var methods = type.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Public);

            foreach (var mi in methods)
            {
                var attr = mi.GetCustomAttribute<RtmpeRpcAttribute>(inherit: true);
                if (attr == null) continue;

                uint id = ComputeMethodId(scope, mi.Name);

                if (ReservedIds.Contains(id))
                {
                    collisions.Add(
                        $"'{scope}.{mi.Name}' (FNV-1a 0x{id:X8}) collides with reserved RpcMethodId");
                    continue;
                }

                if (seen.TryGetValue(id, out var prior))
                {
                    collisions.Add(
                        $"'{scope}.{mi.Name}' (FNV-1a 0x{id:X8}) collides with prior '{prior}'");
                    continue;
                }

                seen[id] = mi.Name;
            }

            return collisions;
        }
    }
}
