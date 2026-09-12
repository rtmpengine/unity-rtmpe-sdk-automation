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
    /// Maps FNV-1a method IDs to <see cref="MethodInfo"/> entries for all
    /// <see cref="RtmpeRpcAttribute"/>-decorated methods on a given type.
    /// </summary>
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
        /// Compute the FNV-1a 32-bit hash of <c>"typeName.methodName"</c>.
        /// This is the stable wire method ID used in Enhanced RPC packets.
        /// </summary>
        /// <param name="typeName">
        /// The declaring type's metadata FULL name — what
        /// <see cref="Type.FullName"/> reports for an ordinary type.  An
        /// unqualified name hashes to a different id than the one the wire
        /// carries; prefer the <see cref="ComputeMethodId(Type, string)"/>
        /// overload, which derives this from the type itself.
        /// </param>
        /// <param name="methodName">The [RtmpeRpc] method's name.</param>
        /// <remarks>
        /// The construction lives in <see cref="RTMPE.Core.WireIdHash"/> and is
        /// shared with the NetworkVariable identity, which addresses the same
        /// object by the same kind of name.  This method is the RPC subsystem's
        /// name for it and stays public because callers outside the package
        /// compute method ids to compare against a captured frame.
        /// </remarks>
        public static uint ComputeMethodId(string typeName, string methodName)
            => RTMPE.Core.WireIdHash.Of(typeName, methodName);

        /// <summary>
        /// The wire method ID of <paramref name="methodName"/> on
        /// <paramref name="type"/> — the id this registry builds, dispatches on
        /// and validates, derived from the type rather than from a name the
        /// caller has to spell correctly.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="type"/> is <see langword="null"/>.  A missing type
        /// would fold the EMPTY scope, which is the scope every unnamed thing
        /// shares — the collision this derivation exists to make unreachable, so
        /// it is refused here rather than returned as a plausible id.
        /// </exception>
        public static uint ComputeMethodId(Type type, string methodName)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            return RTMPE.Core.WireIdHash.Of(RTMPE.Core.WireIdHash.ScopeOf(type), methodName);
        }

        /// <summary>
        /// Look up the <see cref="MethodInfo"/> for <paramref name="methodId"/> on
        /// <paramref name="type"/>, together with its <see cref="RtmpeRpcAttribute"/>.
        /// Returns <see langword="false"/> when the type has no [RtmpeRpc] method
        /// with that ID.
        /// </summary>
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
        /// Resolve which of <paramref name="candidateTypes"/> declares the
        /// [RtmpeRpc] method identified by <paramref name="methodId"/>, so an
        /// inbound Enhanced RPC can reach a method that lives on any
        /// NetworkBehaviour of the addressed object — not only the routing anchor.
        ///
        /// <para>The anchor (<paramref name="anchorIndex"/>) takes precedence:
        /// when it owns the id, its index is returned unconditionally, leaving the
        /// anchor-owned case byte-identical to pre-resolution behaviour.  Otherwise
        /// the remaining candidates are scanned — a single owner resolves to its
        /// index, while two or more resolve to <see langword="false"/> so the
        /// caller falls back to the anchor rather than dispatch to an arbitrary
        /// component.  Two owners means one type mounted twice on the object (or
        /// two instantiations of one generic, which share a scope): the id is
        /// theirs jointly and the frame names no instance, so there is nothing to
        /// choose between them.</para>
        ///
        /// <para>Null candidates are skipped.  A candidate whose [RtmpeRpc] table
        /// cannot be built — a reserved-id or intra-type collision that
        /// <c>BuildMap</c> rejects with a throw — is treated as non-owning rather
        /// than allowed to propagate out of the dispatch path.</para>
        /// </summary>
        /// <param name="candidateTypes">
        /// The component types on the addressed object, in caller order.
        /// </param>
        /// <param name="anchorIndex">
        /// Index of the routing anchor within <paramref name="candidateTypes"/>,
        /// or a negative value when the anchor is absent from the list.
        /// </param>
        /// <param name="methodId">The FNV-1a id to resolve.</param>
        /// <param name="index">The resolved owner's index, or -1 when none.</param>
        /// <returns>
        /// <see langword="true"/> with a unique owner's index; <see langword="false"/>
        /// (index -1) when no candidate owns the id or when the id is ambiguous.
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
        /// Look up the wire method ID for a named [RtmpeRpc] method on
        /// <paramref name="type"/>.
        /// Returns <see langword="false"/> when no such method is registered
        /// (wrong name or missing attribute).
        /// </summary>
        public static bool TryGetMethodId(Type type, string methodName, out uint methodId)
        {
            methodId = ComputeMethodId(type, methodName);
            var map  = GetOrBuild(type);
            return map.ContainsKey(methodId);
        }

        /// <summary>
        /// Validate all [RtmpeRpc] methods on <paramref name="type"/> for hash
        /// collisions with reserved IDs or with each other.  Called automatically
        /// from <c>NetworkBehaviour.OnNetworkSpawn</c> for early error detection.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when one or more [RtmpeRpc] methods on <paramref name="type"/>
        /// produce an FNV-1a hash that collides with a reserved
        /// <see cref="RpcMethodId"/> constant or with another method on the same
        /// type.  The exception message lists every conflicting method so the
        /// developer can rename them in a single pass.
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
