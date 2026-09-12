// RTMPE SDK — Runtime/Rpc/RpcTypeRegistry.cs
//
// Trust boundary:
//  This registry is the sole authority that maps a wire-supplied
//  INetworkSerializable type name to a concrete System.Type the inbound
//  parser will Activator-instantiate.  Every entry in the dictionary is
//  therefore part of the application's reflection-attack surface — any
//  public type reachable here can be constructed by a remote peer or a
//  relayed packet, and its NetworkDeserialize will run with hostile
//  bytes as input.
//
// Default policy (secure by default):
//  • The dictionary is empty until the application registers types.
//  • Authors mark each RPC payload type with [RtmpeRpcSerializable] or
//    pre-register it via Register<T>().  Types lacking both are NOT
//    resolvable.
//  • The legacy AppDomain-wide reflection scan is gated behind
//    AllowAppDomainScan (default false).  Even when re-enabled the scan
//    is filtered to attributed types only — turning the scan back on
//    never widens the surface beyond what the author opted into via
//    [RtmpeRpcSerializable].
//  • Resolve() returns null for any unregistered type name.  The caller
//    in RpcSerializer.ReadParam logs a warning and surfaces the
//    parameter as null, dropping the offending payload before it can
//    reach a [RtmpeRpc] handler.
//
// Threat model:
//  The wire field "typeName" is attacker-controlled; it MUST NOT be
//  used to navigate the AppDomain.  Resolve() is therefore a strict
//  dictionary lookup against the author-attested registry — no fallback
//  to Type.GetType, no string-based reflection, no assembly probing.
//
// Thread safety:
//  • Reads happen on the Unity main thread (RPC dispatch always
//    marshals to main).  Writes happen at static-init time and from
//    explicit Register<T>() calls.  ConcurrentDictionary keeps
//    Register<T>() safe under concurrent access.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Maps explicitly-registered <see cref="INetworkSerializable"/> type
    /// names to their concrete <see cref="System.Type"/> for inbound RPC
    /// parameter instantiation.
    ///
   /// <para>The registry is closed by default: a wire-supplied type name
    /// only resolves when the application has registered it via
    /// <see cref="Register{T}"/> or attributed it with
    /// <see cref="RtmpeRpcSerializableAttribute"/> AND opted into the
    /// AppDomain scan via <see cref="AllowAppDomainScan"/>.</para>
    /// </summary>
    public static class RpcTypeRegistry
    {
        // Keyed by Type.FullName (no assembly suffix).  ConcurrentDictionary
        // is used so Register<T>() is safe under concurrent access from a
        // background warm-up thread, even though steady-state is single-
        // threaded.
        private static readonly ConcurrentDictionary<string, Type> _byName =
            new ConcurrentDictionary<string, Type>(StringComparer.Ordinal);

        // IL2CPP-compatible factory functions stored by Register<T>().
        // () => new T() is a statically-dispatched constructor the AOT compiler
        // can trace and preserve.  Types registered via Register(Type) do NOT
        // get a factory entry; CreateInstance() returns null for them and logs
        // a warning — use Register<T>() in IL2CPP builds.
        private static readonly ConcurrentDictionary<string, Func<INetworkSerializable>> _factories =
            new ConcurrentDictionary<string, Func<INetworkSerializable>>(StringComparer.Ordinal);

        // Tracks whether the AppDomain-wide reflection scan has run.
        private static int _scanState; // 0 = not scanned, 1 = scanned

        // Opt-in flag for the legacy AppDomain reflection scan.  Disabled
        // by default per the SDK's secure-by-default policy: the scan
        // walks every loaded assembly's type table and was historically
        // a reflection gadget surface even when the discovered types
        // were never intended to be reachable from inbound RPC.
        //
       // When re-enabled, the scan only picks up types decorated with
        // [RtmpeRpcSerializable] — toggling this flag therefore cannot
        // widen the registry beyond the application's attested set.
        // Set to true BEFORE the first inbound RPC arrives, e.g. in an
        // application bootstrap method.
        private static volatile bool _allowAppDomainScan;

        /// <summary>
        /// When <see langword="true"/>, the first call to
        /// <see cref="Resolve"/> performs a one-time AppDomain scan that
        /// auto-registers every type carrying
        /// <see cref="RtmpeRpcSerializableAttribute"/>.  Default is
        /// <see langword="false"/>: the registry only contains types
        /// added via <see cref="Register{T}"/>.
        ///
       /// <para>Security: even when this flag is enabled, the scan
        /// filters strictly by <see cref="RtmpeRpcSerializableAttribute"/>.
        /// Untagged types are never auto-registered.</para>
        /// </summary>
        public static bool AllowAppDomainScan
        {
            get => _allowAppDomainScan;
            set => _allowAppDomainScan = value;
        }

        // Reset on Play-Mode entry so a second run gets a clean registry.
        // Without this the registry would retain stale Type references from
        // assemblies that were unloaded between Play sessions.
        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            _byName.Clear();
            _factories.Clear();
            System.Threading.Interlocked.Exchange(ref _scanState, 0);
            _allowAppDomainScan = false;
        }

        /// <summary>
        /// Explicitly register <typeparamref name="T"/> as an inbound-RPC
        /// payload type.  This is the deterministic, secure path: it adds
        /// exactly one type and never triggers a reflection scan.
        ///
       /// <para>Idempotent.  Safe to call from any thread.</para>
        /// </summary>
        public static void Register<T>() where T : INetworkSerializable, new()
        {
            // Store an AOT-safe factory before delegating to Register(Type).
            // () => new T() is a statically-dispatched constructor call that
            // IL2CPP can trace and preserve; Activator.CreateInstance cannot.
            var key = typeof(T).FullName;
            if (key != null) _factories[key] = () => new T();
            Register(typeof(T));
        }

        /// <summary>
        /// Variant of <see cref="Register{T}"/> that takes a runtime
        /// <see cref="Type"/>.  Use only when the type is known
        /// dynamically; prefer the generic overload in author-time code so
        /// the compiler enforces the <c>new()</c> and
        /// <see cref="INetworkSerializable"/> constraints.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// The type is not a non-abstract <see cref="INetworkSerializable"/>
        /// with a public parameterless constructor.
        /// </exception>
        public static void Register(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (!IsRegistrable(type))
                throw new ArgumentException(
                    $"RpcTypeRegistry.Register: '{type.FullName}' is not a registrable " +
                    "INetworkSerializable type (must be non-abstract, non-generic-definition, " +
                    "and either a value type or a class with a public parameterless ctor).",
                    nameof(type));

            if (type.FullName == null) return;

            // Guard the dictionary write with a same-name / different-Type
            // detection.  Idempotent re-registration of the exact same Type
            // (the documented behaviour) stays silent; a second registration
            // under the same FullName but a DIFFERENT runtime Type instance
            // surfaces a diagnostic.  The mismatch is only ever observed
            // after IL2CPP / domain-reload boundaries (assembly version
            // skew, hot-reload of a recompiled type) and silently letting
            // the new entry win means in-flight RPC handlers continue to
            // dispatch through the old NetworkDeserialize while every new
            // entry uses the new one — divergent behaviour for the same
            // wire shape.  Logging at the moment of swap makes the source
            // observable in player.log; the assignment still proceeds so
            // tests and authoring code that intentionally rotate types
            // continue to work.
            if (_byName.TryGetValue(type.FullName, out var existing) && existing != type)
            {
                UnityEngine.Debug.LogWarning(
                    $"[RTMPE] RpcTypeRegistry.Register: '{type.FullName}' already maps to a " +
                    $"different Type instance ({existing.AssemblyQualifiedName}) — overwriting " +
                    $"with {type.AssemblyQualifiedName}.  This usually indicates an assembly " +
                    "version skew or a hot-reloaded type; in-flight RPC handlers may dispatch " +
                    "through stale NetworkDeserialize bindings until they next re-resolve.");
            }
            _byName[type.FullName] = type;
            InstallReflectionFactory(type);
        }

        /// <summary>
        /// Give a type registered without its compile-time identity a factory,
        /// so that resolving it by name is followed by being able to build one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// 🚨 Without this, the two non-generic registration paths — the public
        /// <see cref="Register(Type)"/> and the documented
        /// <c>[RtmpeRpcSerializable]</c> AppDomain scan — wrote
        /// <c>_byName</c> alone. A type found that way resolved by name and
        /// then <see cref="CreateInstance"/> returned <see langword="null"/>,
        /// so every RPC carrying it lost the parameter. The XML doc framed that
        /// as an AOT concern — *"use Register&lt;T&gt; for IL2CPP builds"* — and
        /// it was not: with no factory at all the failure was total on Mono and
        /// in the Editor too.
        /// </para>
        /// <para>
        /// ⛔ It never displaces an existing factory. <see cref="Register{T}"/>
        /// installs <c>() =&gt; new T()</c> BEFORE delegating here, and that
        /// closure is a statically-dispatched constructor call IL2CPP can trace
        /// and preserve. Overwriting it with a reflective one would trade the
        /// AOT-safe path for the AOT-fragile one on every explicit registration.
        /// </para>
        /// <para>
        /// ⚠️ The constructor is resolved once, here, rather than on each call:
        /// a type that cannot be built is a registration-time fact, and finding
        /// it out per inbound packet would put reflection lookup on the receive
        /// path. <see cref="IsRegistrable"/> already refuses a reference type
        /// with no public parameterless constructor, so the null branch below is
        /// reachable only through <see cref="Register(Type)"/>, which applies no
        /// such filter.
        /// </para>
        /// </remarks>
        private static void InstallReflectionFactory(Type type)
        {
            var key = type.FullName;
            if (key == null) return;
            if (_factories.ContainsKey(key)) return;

            if (type.IsValueType)
            {
                // A value type always has a zero-initialised form and no
                // constructor to strip.
                _factories.TryAdd(key, () => (INetworkSerializable)Activator.CreateInstance(type));  // IL2CPP-EXEMPT(reflection): a value type has no constructor for the AOT compiler to strip — Activator returns its zero-initialised form, and the reference-type branch below resolves a real constructor once at registration rather than per call.
                return;
            }

            var ctor = type.GetConstructor(
                BindingFlags.Public | BindingFlags.Instance,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            if (ctor == null)
            {
                Debug.LogWarning(
                    $"[RTMPE] RpcTypeRegistry.Register: '{key}' has no public parameterless " +
                    "constructor, so it can be resolved by name but never instantiated — every " +
                    "RPC carrying it will surface that parameter as null.  Give it one, or " +
                    "register a type that has one.");
                return;
            }

            _factories.TryAdd(key, () => (INetworkSerializable)ctor.Invoke(null));
        }

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="fullName"/>
        /// has been explicitly registered.  Useful for diagnostic UIs and
        /// for tests that want to assert the registry contents.
        /// </summary>
        public static bool IsRegistered(string fullName)
            => !string.IsNullOrEmpty(fullName) && _byName.ContainsKey(fullName);

        /// <summary>
        /// Instantiate the registered type identified by
        /// <paramref name="fullName"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every registration path leaves a factory behind.
        /// <see cref="Register{T}"/> installs <c>() =&gt; new T()</c>, a
        /// statically-dispatched constructor call IL2CPP can trace and
        /// preserve. <see cref="Register(Type)"/> and the
        /// <c>[RtmpeRpcSerializable]</c> AppDomain scan install a reflective
        /// one over the type's public parameterless constructor, resolved once
        /// at registration.
        /// </para>
        /// <para>
        /// 🚨 Until 2026-08-27 the two non-generic paths installed NOTHING, and
        /// this documentation described the consequence as an AOT concern —
        /// *"use Register&lt;T&gt; for IL2CPP builds"*. It was not: with no
        /// factory at all, a type discovered by the documented automatic path
        /// resolved by name and then returned <see langword="null"/> here on
        /// Mono and in the Editor as readily as under IL2CPP, so every RPC
        /// carrying it lost that parameter.
        /// </para>
        /// <para>
        /// ⚠️ <c>Register&lt;T&gt;</c> remains the recommendation for IL2CPP,
        /// and now for a real reason rather than a described one: a reflective
        /// factory calls a constructor the linker may have stripped, which
        /// throws — caught below, reported, and surfaced as
        /// <see langword="null"/> rather than allowed into the receive loop.
        /// </para>
        /// <para>
        /// Returns <see langword="null"/> when <paramref name="fullName"/> is
        /// not registered, when it has no usable constructor, or when that
        /// constructor threw.
        /// </para>
        /// </remarks>
        internal static object CreateInstance(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            if (_factories.TryGetValue(fullName, out var factory))
            {
                // ⚠️ A constructor is application code, and this runs on the
                // inbound dispatch loop. An escaping exception here would abort
                // the remaining parameters of the RPC and every entry after it
                // in the datagram — and because a payload type is chosen by the
                // SENDER, it would do so on every packet carrying that type.
                //
                // 🔑 Distinguished from "no factory" in the report rather than
                // in the return value: both surface the parameter as null, but
                // a throwing constructor is a defect in the registered type and
                // silence would leave it looking like an unregistered name.
                try
                {
                    return factory();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[RTMPE] RpcTypeRegistry: constructing '{fullName}' threw " +
                        $"{ex.GetType().Name}: {ex.Message}.  The parameter is surfaced as null.  " +
                        "A registered RPC payload type must have a parameterless constructor that " +
                        "does not throw; under IL2CPP it may also have been stripped, in which " +
                        "case register it with Register<T>() so the constructor is traceable.");
                    return null;
                }
            }
            // A type registered via Register(Type) rather than Register<T>() has
            // no AOT-safe factory — under IL2CPP the parameterless constructor
            // may be stripped — so the caller gets null.  Reported by the caller
            // rather than here: how often this may be said is a property of the
            // inbound loop, which chooses how many parameters to reject per
            // datagram, and a report from inside the lookup sits below the rate
            // gate the caller applies.
            return null;
        }

        /// <summary>
        /// Snapshot the currently registered type names.  Returned list is
        /// a copy and is safe to iterate without holding the registry
        /// lock.  Order is not specified.
        /// </summary>
        public static IReadOnlyList<string> RegisteredTypeNames()
        {
            // Materialise into a list so callers can iterate without
            // observing concurrent dictionary mutations.
            var keys = _byName.Keys;
            var copy = new List<string>(keys.Count);
            foreach (var k in keys) copy.Add(k);
            return copy;
        }

#if UNITY_INCLUDE_TESTS
        /// <summary>
        /// Test-only: clear every registration and reset the scan-state
        /// flag.  NOT exposed to game code — incorrect use here would
        /// reopen the reflection-attack surface.  Compiled only when
        /// <c>UNITY_INCLUDE_TESTS</c> is defined so the shipped Player
        /// assembly cannot reach this entry point via reflection.
        /// </summary>
        internal static void ResetForTests()
        {
            _byName.Clear();
            _factories.Clear();
            System.Threading.Interlocked.Exchange(ref _scanState, 0);
            _allowAppDomainScan = false;
        }
#endif // UNITY_INCLUDE_TESTS

        /// <summary>
        /// Look up the concrete <see cref="Type"/> previously registered
        /// for <paramref name="fullName"/>.  Returns <see langword="null"/>
        /// for any unregistered name — the caller in
        /// <c>RpcSerializer.ReadParam</c> treats null as a hard failure
        /// and surfaces the parameter as null with a warning.
        ///
       /// <para>Wire-supplied <paramref name="fullName"/> is treated as
        /// hostile input and is never used to navigate the AppDomain
        /// directly.  The only authorised path to a Type is the
        /// dictionary populated by <see cref="Register{T}"/> or the
        /// attribute-filtered AppDomain scan.</para>
        /// </summary>
        public static Type Resolve(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;

            if (_byName.TryGetValue(fullName, out var hit)) return hit;

            // Optionally consult the attribute-filtered scan.  Disabled by
            // default; even when enabled, only types carrying
            // [RtmpeRpcSerializable] are added.
            if (_allowAppDomainScan)
            {
                EnsureScanned();
                if (_byName.TryGetValue(fullName, out hit)) return hit;
            }

            return null;
        }

        /// <summary>
        /// Run the attribute-filtered AppDomain scan exactly once.
        /// Subsequent callers short-circuit on the CompareExchange.
        /// </summary>
        private static void EnsureScanned()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _scanState, 1, 0) != 0)
                return; // another caller already scanned

            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                foreach (var asm in assemblies)
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException ex)
                    {
                        // Some types may be unloadable (e.g. editor-only
                        // assemblies in a player build); use the partial list.
                        types = ex.Types?.Where(t => t != null).ToArray() ?? Array.Empty<Type>();
                    }
                    catch (Exception)
                    {
                        // Defensive: a hostile assembly (or one with broken
                        // reflection metadata) must not abort the scan.
                        continue;
                    }

                    foreach (var t in types)
                    {
                        if (t == null) continue;
                        if (!IsRegistrable(t)) continue;

                        // Hard filter: ONLY types carrying the explicit
                        // opt-in attribute are admitted.  This is the
                        // load-bearing line of defence against the
                        // reflection-gadget surface.
                        if (t.GetCustomAttribute<RtmpeRpcSerializableAttribute>(inherit: false) == null)
                            continue;

                        if (t.FullName != null)
                        {
                            _byName[t.FullName] = t;
                            // ⚠️ …and a factory, or the documented automatic
                            // path resolves a type it cannot build. This scan
                            // wrote _byName alone until 2026-08-27.
                            InstallReflectionFactory(t);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // The scan is best-effort.  A failure here only means
                // automatic discovery did not work — explicit Register<T>()
                // is still available as the deterministic fallback.
                Debug.LogWarning(
                    $"[RTMPE] RpcTypeRegistry: AppDomain scan failed " +
                    $"({ex.GetType().Name}: {ex.Message}).  " +
                    "Use RpcTypeRegistry.Register<T>() to register custom RPC types explicitly.");
            }
        }

        /// <summary>
        /// Common eligibility filter: a type is registrable only when it
        /// is a concrete (non-abstract, non-interface, non-open-generic)
        /// <see cref="INetworkSerializable"/> with a public parameterless
        /// constructor (structs always satisfy the latter).
        /// </summary>
        private static bool IsRegistrable(Type t)
        {
            if (t.IsAbstract) return false;
            if (t.IsInterface) return false;
            if (t.IsGenericTypeDefinition) return false;
            if (!typeof(INetworkSerializable).IsAssignableFrom(t)) return false;
            if (!t.IsValueType)
            {
                var ctor = t.GetConstructor(
                    BindingFlags.Public | BindingFlags.Instance,
                    binder: null, types: Type.EmptyTypes, modifiers: null);
                if (ctor == null) return false;
            }
            return true;
        }
    }
}
