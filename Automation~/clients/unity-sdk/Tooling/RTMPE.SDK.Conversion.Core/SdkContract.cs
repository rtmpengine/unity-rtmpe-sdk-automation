namespace RTMPE.SDK.Conversion.Core
{
    /// <summary>
    /// The SDK contract surface that scored, gated, and fixture sources compile
    /// against — the types every analyzer keys off, with gameplay APIs left
    /// unresolved by design.
    ///
    /// Every judgment the toolchain passes on converted code is made against
    /// this contract and no other: the readiness score, the authority verdict,
    /// and the advisory compile gate all read the same surface. It lives here,
    /// in the one project all three consumers already reference, because it was
    /// previously declared three times and held together by parity tests — an
    /// arrangement that could prove the copies agreed and never that any of them
    /// was right, since a surface missing from all three is invisible to a
    /// comparison between them.
    /// </summary>
    public static class SdkContract
    {
        public const string Stub =
            // `transform` belongs to the contract for the reason GameObject does:
            // an ownership signal reads it. A component's own transform is the
            // state NetworkTransform replicates and the commonest owner-only
            // write there is, so a surface without it answers "this Update
            // drives nothing" for every moving object — silencing RTMPE2003 on
            // the loop the guard exists for and handing the score's Ownership
            // weight to a component every client is simulating independently.
            // `GetComponent<T>()` and `AddComponent<T>()` belong here for the same
            // reason: the motion reading takes `AddComponent<NetworkTransform>()`
            // as the declaration it is, and a receiver acquired through
            // `GetComponent<Other>()` binds to the type it names, so the headless
            // score reads the same value the IDE reads rather than an error type.
            "namespace UnityEngine { public class Component { public Transform transform => null; " +
            "    public GameObject gameObject => null; public T GetComponent<T>() => default; " +
            "    public bool TryGetComponent<T>(out T component) { component = default; return false; } } " +
            "  public class MonoBehaviour : Component {} " +
            // ScriptableObject is a root here as Component is: every Unity
            // template ships `Readme : ScriptableObject`, and a base the stub
            // does not declare put a "did not resolve" line about that type at
            // the head of every fresh project's readiness report. It declares
            // no member — nothing the rubric reads keys off one.
            "  public class ScriptableObject {} " +
            // Every Transform member the motion reading keys off is here, so a
            // write the IDE reads as motion binds the same way in the headless
            // score.  Open, as the engine declares it: RectTransform derives from
            // it, and a reading that admits a mover by its base has to be able to
            // meet one here.
            "  public class Transform : Component { " +
            "    public Vector3 position { get; set; } public Quaternion rotation { get; set; } " +
            "    public Vector3 localPosition { get; set; } public Quaternion localRotation { get; set; } " +
            "    public Vector3 localScale { get; set; } public Vector3 eulerAngles { get; set; } " +
            "    public Vector3 localEulerAngles { get; set; } public Transform parent { get; set; } " +
            "    public Vector3 forward { get; set; } public Vector3 right { get; set; } public Vector3 up { get; set; } " +
            "    public void Translate(Vector3 translation) {} public void Rotate(Vector3 eulers) {} " +
            "    public void RotateAround(Vector3 point, Vector3 axis, float angle) {} " +
            "    public void SetPositionAndRotation(Vector3 position, Quaternion rotation) {} " +
            "    public void SetLocalPositionAndRotation(Vector3 position, Quaternion rotation) {} " +
            "    public void LookAt(Transform target) {} } " +
            // RectTransform moves an object through the Transform members above and
            // through nothing of its own that the reading keys off, so it declares
            // nothing of its own here.
            "  public sealed class RectTransform : Transform {} " +
            // The other movers, with the members a write to which moves the object
            // — the members the motion reading keys off, and nothing else of their
            // engine surface — so a physics or controller mover scores here as it
            // does in the IDE, and a receiver named like one but typed as a setting
            // is told apart from one.  Declared as the engine declares them for the
            // version this package supports (2022.3: `velocity`; Unity 6's
            // `linearVelocity` is read by the receiver's type and needs no member
            // here), so a body the toolchain emits against them compiles there as
            // it compiles here.
            "  public sealed class Rigidbody : Component { " +
            "    public Vector3 position { get; set; } public Quaternion rotation { get; set; } " +
            "    public Vector3 velocity { get; set; } public Vector3 angularVelocity { get; set; } " +
            "    public void MovePosition(Vector3 position) {} public void MoveRotation(Quaternion rotation) {} " +
            "    public void AddForce(Vector3 force) {} public void AddTorque(Vector3 torque) {} } " +
            "  public sealed class Rigidbody2D : Component { " +
            "    public Vector2 position { get; set; } public float rotation { get; set; } " +
            "    public Vector2 velocity { get; set; } public float angularVelocity { get; set; } " +
            "    public void MovePosition(Vector2 position) {} public void MoveRotation(float angle) {} " +
            "    public void AddForce(Vector2 force) {} public void AddTorque(float torque) {} } " +
            "  public enum CollisionFlags { None = 0, Sides = 1, Above = 2, Below = 4 } " +
            "  public sealed class CharacterController : Component { " +
            "    public bool isGrounded => false; " +
            "    public CollisionFlags Move(Vector3 motion) => CollisionFlags.None; public bool SimpleMove(Vector3 speed) => false; } " +
            "  public sealed class SerializeField : System.Attribute {} " +
            // GameObject belongs to the contract, not the unresolved gameplay
            // surface: the authority signal reads a held GameObject — the
            // `[SerializeField] GameObject _prefab` an orchestrator wires the scene
            // through — as orchestration. A surface without it leaves that signal
            // structurally dead in the headless score and gate, grading an
            // orchestrator as a plain leaf exactly where a real Unity compile would
            // classify it correctly.
            "  public sealed class GameObject { public Transform transform => null; " +
            "    public T GetComponent<T>() => default; public T AddComponent<T>() where T : Component => default; } " +
            "  public sealed class HeaderAttribute : System.Attribute { public HeaderAttribute(string header) {} } " +
            // All three arities, because Unity has all three and a shipped sample
            // uses the second: with one the attribute on TwoPlayerAvatar does not
            // bind, and every rule reading it scores a compilation carrying an
            // error nothing reports.
            "  public sealed class RequireComponent : System.Attribute { "
            + "    public RequireComponent(System.Type type) {} "
            + "    public RequireComponent(System.Type type, System.Type type2) {} "
            + "    public RequireComponent(System.Type type, System.Type type2, System.Type type3) {} } " +
            // Vector3 and Quaternion resolve, with the static members the
            // conversion's allowlisted initializers name: a field seeded from
            // one of them must compile here exactly as it does against the real
            // engine, or the conversion the toolchain itself would emit scores
            // as unresolved. The samples' other gameplay APIs stay unresolved by
            // design — this surface is the SDK contract, not a Unity mirror.
            "  public struct Vector2 : System.IEquatable<Vector2> { " +
            "    public float x; public float y; " +
            "    public Vector2(float x, float y) { this.x = x; this.y = y; } " +
            "    public bool Equals(Vector2 other) => x == other.x && y == other.y; " +
            "    public static Vector2 zero => new Vector2(0f, 0f); " +
            "    public static Vector2 one => new Vector2(1f, 1f); " +
            "    public static Vector2 up => new Vector2(0f, 1f); " +
            "    public static Vector2 down => new Vector2(0f, -1f); " +
            "    public static Vector2 left => new Vector2(-1f, 0f); " +
            "    public static Vector2 right => new Vector2(1f, 0f); } " +
            // ⚠️ PROPERTIES, because they are properties in Unity while every
            // float vector beside them carries fields. A contract that made
            // them fields would bind uses Unity refuses.
            "  public struct Vector2Int : System.IEquatable<Vector2Int> { " +
            "    public Vector2Int(int x, int y) { this.x = x; this.y = y; } " +
            "    public int x { get; set; } public int y { get; set; } " +
            "    public bool Equals(Vector2Int other) => x == other.x && y == other.y; " +
            "    public static Vector2Int zero => new Vector2Int(0, 0); " +
            "    public static Vector2Int one => new Vector2Int(1, 1); " +
            "    public static Vector2Int up => new Vector2Int(0, 1); " +
            "    public static Vector2Int down => new Vector2Int(0, -1); " +
            "    public static Vector2Int left => new Vector2Int(-1, 0); " +
            "    public static Vector2Int right => new Vector2Int(1, 0); } " +
            "  public struct Vector3 : System.IEquatable<Vector3> { " +
            "    public float x; public float y; public float z; " +
            "    public bool Equals(Vector3 other) => x == other.x && y == other.y && z == other.z; " +
            "    public static readonly Vector3 zero; public static readonly Vector3 one; " +
            "    public static readonly Vector3 up; public static readonly Vector3 down; " +
            "    public static readonly Vector3 left; public static readonly Vector3 right; " +
            "    public static readonly Vector3 forward; public static readonly Vector3 back; } " +
            "  public struct Quaternion : System.IEquatable<Quaternion> { " +
            "    public float x; public float y; public float z; public float w; " +
            "    public static readonly Quaternion identity; " +
            "    public bool Equals(Quaternion other) => x == other.x && y == other.y && z == other.z && w == other.w; } " +
            // Color is in the RPC serializer's closed set (RpcTypeId 0x07), so a
            // surface without it turned an `[RtmpeRpc] void Flash(Color c)` into
            // RTMPE1002 — an Error — and faulted the score's RPC dimension on a
            // parameter the runtime encodes. Contract for the same reason Vector3
            // and Quaternion are: the analyzer names it by metadata name.
            "  public struct Color : System.IEquatable<Color> { " +
            "    public float r; public float g; public float b; public float a; " +
            "    public Color(float r, float g, float b, float a = 1f) { this.r = r; this.g = g; this.b = b; this.a = a; } " +
            "    public bool Equals(Color other) => r == other.r && g == other.g && b == other.b && a == other.a; " +
            "    public static readonly Color white; public static readonly Color black; public static readonly Color red; " +
            "    public static readonly Color green; public static readonly Color blue; public static readonly Color clear; } } " +
            // IDamageable carries its one member: the interface exists so game
            // code can receive server-authorised damage, and an interface with
            // no member would accept an implementation the editor rejects with
            // CS0535 — a false-accept, the one direction this gate must never
            // fail in.
            "namespace RTMPE.Core { public interface IDamageable { void ReceiveApplyDamage(int damage); } " +
            // A single frame of player input, as the prediction hooks consume
            // it. Wire serialisation stays out — the fields here are the ones a
            // GatherInput override assigns.
            "  public struct InputPayload { public uint Tick; public float MoveX; public float MoveY; public bool Jump; } " +
            "  public abstract class NetworkBehaviour : UnityEngine.MonoBehaviour { " +
            // The identity and lifecycle surface the package documentation
            // teaches beside the hooks: ownership identity, spawn state, the
            // owner-lifetime opt-out, and the attested sender read inside an
            // RPC body. Doc-taught members are contract for the same reason
            // the wrappers are — code written from the documentation must
            // compile here exactly as it does in the editor.
            "    public bool IsOwner => false; public ulong NetworkObjectId => 0; " +
            "    public string OwnerPlayerId => \"\"; public bool IsSpawned => false; " +
            "    public bool DestroyWithOwner { get; set; } " +
            "    protected ulong CurrentRpcSender => 0; " +
            // The dispatch entry point the RPC generation transform rewrites call
            // sites to. Without it the toolchain's own generated output does not
            // bind, and every surface that judges code against this contract —
            // the readiness score and the advisory compile gate — rejects it.
            "    public void RPC(string methodName, params object[] args) {} " +
            // The lifecycle hooks are protected, exactly as the runtime declares
            // them, so a sample override must also be protected; a public override
            // here would surface as CS0507 instead of compiling against a looser stub.
            "    protected virtual void OnNetworkSpawn() {} protected virtual void OnNetworkDespawn() {} " +
            "    protected virtual void OnOwnershipChanged(string previousOwner, string newOwner) {} " +
            "    protected virtual void OnFixedTick(float deltaTime) {} protected virtual void OnDestroy() {} " +
            // The client-side-prediction pair, documented with these exact
            // signatures: a predicted-movement component overrides both, and a
            // contract without them rejects the whole project wholesale.
            "    protected virtual InputPayload GatherInput() => default; " +
            "    protected virtual void ApplyInput(InputPayload input, float deltaTime) {} } } " +
            // The navigation mover, in the namespace the engine declares it in.
            "namespace UnityEngine.AI { public sealed class NavMeshAgent : UnityEngine.Component { " +
            "    public UnityEngine.Vector3 destination { get; set; } public UnityEngine.Vector3 velocity { get; set; } " +
            "    public bool SetDestination(UnityEngine.Vector3 target) => false; public bool Warp(UnityEngine.Vector3 newPosition) => false; } } " +
            // All eight wrappers the closed type map emits, in the runtime's own
            // hierarchy: seven carried by the generic base, and String derived
            // straight from NetworkVariableBase as the runtime declares it.
            // Detection keys on NetworkVariableBase, so a wrapper missing here is
            // an error type that scores as "no replicated state" — the toolchain
            // grading its own correct output as unconverted.
            // Value is a property, exactly as the runtime declares it: a field
            // here would compile `pos.Value.y = 1f` and `ref hp.Value` — shapes
            // the editor rejects with CS1612/CS0206 — and certify code the
            // first real Unity compile refuses. The element constraint is
            // mirrored for the same reason, in the opposite direction: a
            // hand-written wrapper over a non-equatable element must fail here
            // as it fails there.
            "namespace RTMPE.Sync { public abstract class NetworkVariableBase {} " +
            "  public abstract class NetworkVariable<T> : NetworkVariableBase where T : struct, System.IEquatable<T> { " +
            "    protected NetworkVariable(RTMPE.Core.NetworkBehaviour owner, string memberName, T initialValue = default) {} " +
            "    public T Value { get => default; set {} } public event System.Action<T, T> OnValueChanged; } " +
            "  public sealed class NetworkVariableInt : NetworkVariable<int> { " +
            "    public NetworkVariableInt(RTMPE.Core.NetworkBehaviour owner, string memberName, int initialValue = default) " +
            "      : base(owner, memberName, initialValue) {} } " +
            "  public sealed class NetworkVariableFloat : NetworkVariable<float> { " +
            "    public NetworkVariableFloat(RTMPE.Core.NetworkBehaviour owner, string memberName, float initialValue = default) " +
            "      : base(owner, memberName, initialValue) {} } " +
            "  public sealed class NetworkVariableBool : NetworkVariable<bool> { " +
            "    public NetworkVariableBool(RTMPE.Core.NetworkBehaviour owner, string memberName, bool initialValue = default) " +
            "      : base(owner, memberName, initialValue) {} } " +
            "  public sealed class NetworkVariableVector2 : NetworkVariable<UnityEngine.Vector2> { " +
            "    public NetworkVariableVector2(RTMPE.Core.NetworkBehaviour owner, string memberName, UnityEngine.Vector2 initialValue = default) " +
            "      : base(owner, memberName, initialValue) {} } " +
            "  public sealed class NetworkVariableVector2Int : NetworkVariable<UnityEngine.Vector2Int> { " +
            "    public NetworkVariableVector2Int(RTMPE.Core.NetworkBehaviour owner, string memberName, UnityEngine.Vector2Int initialValue = default) " +
            "      : base(owner, memberName, initialValue) {} } " +
            "  public sealed class NetworkVariableVector3 : NetworkVariable<UnityEngine.Vector3> { " +
            "    public NetworkVariableVector3(RTMPE.Core.NetworkBehaviour owner, string memberName, UnityEngine.Vector3 initialValue = default) " +
            "      : base(owner, memberName, initialValue) {} } " +
            "  public sealed class NetworkVariableQuaternion : NetworkVariable<UnityEngine.Quaternion> { " +
            "    public NetworkVariableQuaternion(RTMPE.Core.NetworkBehaviour owner, string memberName, UnityEngine.Quaternion initialValue = default) " +
            "      : base(owner, memberName, initialValue) {} } " +
            "  public sealed class NetworkVariableString : NetworkVariableBase { " +
            "    public NetworkVariableString(RTMPE.Core.NetworkBehaviour owner, string memberName, string initialValue = \"\") {} " +
            "    public string Value { get => \"\"; set {} } public event System.Action<string, string> OnValueChanged; } " +
            // The synchronised lists are contract, on the same footing as the
            // scalar wrappers: the runtime ships them, the package documentation
            // teaches them, and detection keys on NetworkVariableBase — so a list
            // missing here is an error type, and a component whose replicated
            // state is a list scores as holding none while the advisory gate
            // refuses code the real engine compiles. Declarations mirror the
            // runtime's own: the seven closed element types over one generic base,
            // constructed without an initial value, mutated through the list
            // surface rather than a Value property, and readable as an
            // IReadOnlyList<T> — the runtime declares it, with the enumerators
            // explicit, so that a LINQ operator or a read-only parameter over a
            // converted field compiles here as it does in the editor, while
            // `foreach` still binds to the struct enumerator.
            "  public enum NetworkListChangeKind : byte { Add = 1, Insert = 2, RemoveAt = 3, Set = 4, Clear = 5, FullSync = 6 } " +
            "  public readonly struct NetworkVariableListChangeEvent<T> { " +
            "    public readonly NetworkListChangeKind Kind; public readonly int Index; " +
            "    public readonly T NewValue; public readonly T PreviousValue; " +
            "    public NetworkVariableListChangeEvent(NetworkListChangeKind kind, int index, T newValue, T previousValue) " +
            "      { Kind = kind; Index = index; NewValue = newValue; PreviousValue = previousValue; } } " +
            "  public abstract class NetworkVariableList<T> : NetworkVariableBase, System.Collections.Generic.IReadOnlyList<T> { " +
            "    protected NetworkVariableList(RTMPE.Core.NetworkBehaviour owner, string memberName) {} " +
            "    System.Collections.Generic.IEnumerator<T> System.Collections.Generic.IEnumerable<T>.GetEnumerator() => default; " +
            "    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => default; " +
            "    public int FullSyncOpThreshold { get; set; } " +
            "    public int Count => 0; " +
            "    public int MaxCount => 0; public bool IsFull => false; " +
            "    public T this[int index] { get => default; set {} } " +
            "    public void Add(T item) {} public void Insert(int index, T item) {} " +
            "    public void RemoveAt(int index) {} public bool Remove(T item) => false; " +
            "    public void Clear() {} public bool Contains(T item) => false; " +
            "    public int IndexOf(T item) => -1; " +
            // The whole public surface the runtime's generic base declares, held
            // to the runtime file by TheStubsListSurfaceIsTheRuntimesTests in both
            // directions: TryAdd, CanSend, IsFull and MaxCount were missing for a
            // release, so the compile check stood down on any file that used one
            // and a write through them was unreadable to the readiness rubric.
            "    public bool TryAdd(T item) => false; public bool CanSend(T value) => false; " +
            "    public bool CanAdd(T item) => false; " +
            // The runtime exposes the list's enumerator, so `foreach` over a
            // synchronised list is ordinary usage; the pattern binds to this
            // method's return type, which must therefore be the runtime's own.
            "    public System.Collections.Generic.List<T>.Enumerator GetEnumerator() => default; " +
            "    public event System.Action<NetworkVariableListChangeEvent<T>> OnListChanged; } " +
            "  public sealed class NetworkVariableListInt : NetworkVariableList<int> { " +
            "    public NetworkVariableListInt(RTMPE.Core.NetworkBehaviour owner, string memberName) : base(owner, memberName) {} } " +
            "  public sealed class NetworkVariableListFloat : NetworkVariableList<float> { " +
            "    public NetworkVariableListFloat(RTMPE.Core.NetworkBehaviour owner, string memberName) : base(owner, memberName) {} } " +
            "  public sealed class NetworkVariableListVector3 : NetworkVariableList<UnityEngine.Vector3> { " +
            "    public NetworkVariableListVector3(RTMPE.Core.NetworkBehaviour owner, string memberName) : base(owner, memberName) {} } " +
            "  public sealed class NetworkVariableListString : NetworkVariableList<string> { " +
            "    public NetworkVariableListString(RTMPE.Core.NetworkBehaviour owner, string memberName) : base(owner, memberName) {} } " +
            "  public sealed class NetworkVariableListBool : NetworkVariableList<bool> { " +
            "    public NetworkVariableListBool(RTMPE.Core.NetworkBehaviour owner, string memberName) : base(owner, memberName) {} } " +
            "  public sealed class NetworkVariableListVector2 : NetworkVariableList<UnityEngine.Vector2> { " +
            "    public NetworkVariableListVector2(RTMPE.Core.NetworkBehaviour owner, string memberName) : base(owner, memberName) {} } " +
            "  public sealed class NetworkVariableListVector2Int : NetworkVariableList<UnityEngine.Vector2Int> { " +
            "    public NetworkVariableListVector2Int(RTMPE.Core.NetworkBehaviour owner, string memberName) : base(owner, memberName) {} } " +
            "  [System.AttributeUsage(System.AttributeTargets.Field | System.AttributeTargets.Property)] " +
            // SendRateHz is the attribute's whole point — the documentation's
            // bandwidth guidance is written as [NetworkVariable(SendRateHz = 10f)],
            // and a hollow attribute turns that named argument into CS0246.
            "  public sealed class NetworkVariableAttribute : System.Attribute { public float SendRateHz { get; set; } } } " +
            // The audience enum and the attribute's audience-carrying constructor:
            // the transform emits [RtmpeRpc(RpcTarget.X)], so a parameterless
            // attribute alone rejects every audience the toolchain designates.
            // The serializer's tenth type: a parameter implementing this interface
            // is encodable (RpcTypeId 0x0A), and the RPC rule tests it by symbol.
            // Both members are declared, not stubbed empty — an interface with no
            // member accepts an implementation the editor rejects with CS0535, the
            // false-accept the compile gate must never make (the IDamageable note
            // above states the same rule). The writer and reader are declared by
            // name only: a serialise body calling their members reads as
            // unresolved here exactly as the rest of the gameplay surface does, and
            // the compile gate is differential over that.
            "namespace RTMPE.Rpc { public interface IRtmpeWriter {} public interface IRtmpeReader {} " +
            "  public interface INetworkSerializable { void NetworkSerialize(IRtmpeWriter writer); void NetworkDeserialize(IRtmpeReader reader); } " +
            "  public enum RpcTarget : byte { All = 0, Others = 1, Server = 2, AllBuffered = 3 } " +
            "  [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = false, Inherited = false)] " +
            "  public sealed class RtmpeRpcAttribute : System.Attribute { " +
            "    public RtmpeRpcAttribute(RpcTarget target = RpcTarget.All) {} public RpcTarget Target { get; } } } ";
    }
}
