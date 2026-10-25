using System;
using System.Collections.Generic;
using System.Globalization;

namespace RTMPE.SDK.Conversion.Core
{
    /// <summary>How one NetworkVariable construction appears in source.</summary>
    public sealed class SourceConstruction
    {
        public SourceConstruction(
            string declaringTypeName, string memberName, bool ownerIsThis, string nameArgument)
        {
            DeclaringTypeName = declaringTypeName;
            MemberName = memberName;
            OwnerIsThis = ownerIsThis;
            NameArgument = nameArgument;
        }

        public string DeclaringTypeName { get; }

        public string MemberName { get; }

        /// <summary>True when the construction passes <c>this</c> as the owner.</summary>
        public bool OwnerIsThis { get; }

        /// <summary>
        /// The name this construction derives its identity from — the operand of
        /// <c>nameof(...)</c>, or a string literal — or null when neither is
        /// statically readable.
        /// </summary>
        /// <remarks>
        /// ⚠️ Not the same as <see cref="MemberName"/>, and the difference is the
        /// only way a type can still collide with itself: an author who writes
        /// <c>_shield = new NetworkVariableInt(this, nameof(_health))</c> has two
        /// members addressing one identity, and the compiler is content because
        /// both names resolve.
        /// </remarks>
        public string NameArgument { get; }
    }

    /// <summary>What one type's derivation was asked about.</summary>
    public sealed class DerivationRequest
    {
        public DerivationRequest(
            string typeName,
            IReadOnlyList<SourceConstruction> sourceConstructions,
            IReadOnlyList<string> newMembers,
            IReadOnlyDictionary<string, string> rpcs)
        {
            TypeName = typeName;
            SourceConstructions = sourceConstructions;
            NewMembers = newMembers;
            Rpcs = rpcs;
        }

        /// <summary>Fully-qualified metadata name of the type being converted.</summary>
        public string TypeName { get; }

        /// <summary>Every NetworkVariable construction already visible on the type.</summary>
        public IReadOnlyList<SourceConstruction> SourceConstructions { get; }

        /// <summary>Members this run is about to construct, in declaration order.</summary>
        public IReadOnlyList<string> NewMembers { get; }

        /// <summary>RPC provenance to carry through into the record unchanged.</summary>
        public IReadOnlyDictionary<string, string> Rpcs { get; }
    }

    /// <summary>The classes of fault a derivation can report.</summary>
    public enum LedgerIssueKind
    {
        /// <summary>Two members on one type derive the same identity.</summary>
        SharedIdentity,

        /// <summary>The record is invalid, or bound to another type.</summary>
        LedgerInvalid,

        /// <summary>A construction whose name this pass cannot read statically.</summary>
        UnreadableName,

        /// <summary>
        /// One member constructed twice, deriving two identities.
        /// </summary>
        MemberConstructedTwice,

        /// <summary>The identity space or the sidecar filename is unusable.</summary>
        IdSpaceHazard,
    }

    /// <summary>One error, with the member it concerns where applicable.</summary>
    public sealed class LedgerIssue
    {
        public LedgerIssue(LedgerIssueKind kind, string memberName, string message)
        {
            Kind = kind;
            MemberName = memberName;
            Message = message;
        }

        public LedgerIssueKind Kind { get; }

        public string MemberName { get; }

        public string Message { get; }
    }

    /// <summary>What a derivation decided, and the record that states it.</summary>
    public sealed class DerivationVerdict
    {
        public DerivationVerdict(
            IReadOnlyList<LedgerIssue> errors,
            IReadOnlyDictionary<string, uint> identities,
            LedgerDocument record)
        {
            Errors = errors;
            Identities = identities;
            Record = record;
        }

        public bool Accepted => Errors.Count == 0;

        public IReadOnlyList<LedgerIssue> Errors { get; }

        /// <summary>Member name → the identity it derives to.</summary>
        public IReadOnlyDictionary<string, uint> Identities { get; }

        /// <summary>The record to write; null when the derivation was refused.</summary>
        public LedgerDocument Record { get; }
    }

    /// <summary>
    /// Turns a type's members into wire identities by deriving each from the
    /// declaring type and the member's own name, and states the result as a
    /// record.
    /// </summary>
    /// <remarks>
    /// ⛔ There is nothing here to allocate, and that is the point. An allocator
    /// answers "which number is free", which is a question about everything else
    /// sharing the address space — and a NetworkVariable's address space is the
    /// OBJECT, which a tool converting one type in one file cannot see. Derivation
    /// asks a question the file can answer, and the answer is unique across the
    /// object because two components of one object differ by concrete type.
    ///
    /// <para>⚠️ With one residual case, and it is not this tool's to see: Unity
    /// permits N instances of one component type on one GameObject, and no
    /// <c>[DisallowMultipleComponent]</c> is declared anywhere in the SDK. Two
    /// <c>Health</c> components on one object derive the same identities, which
    /// is a property of the OBJECT — a per-type pass in a per-type file cannot
    /// answer it. The runtime does, at registration: the second component's
    /// variable is refused and reported rather than left to address the first's
    /// state.</para>
    ///
    /// <para>What survives as a fault is therefore narrow: two members of ONE type
    /// deriving one identity. Distinct member names cannot do that by accident —
    /// it takes a construction naming a member other than the one it is assigned
    /// to, or a 32-bit hash collision.</para>
    /// </remarks>
    public static class VariableIdDerivation
    {
        public static DerivationVerdict Plan(DerivationRequest request)
        {
            var errors = new List<LedgerIssue>();
            var identities = new SortedDictionary<string, uint>(StringComparer.Ordinal);

            // Which name each member derives from. A construction already in
            // source is read from what it passes; a member this run is about to
            // write derives from its own name, because that is what the emitted
            // `nameof(...)` will resolve to.
            var namedBy = new SortedDictionary<string, string>(StringComparer.Ordinal);

            foreach (var construction in request.SourceConstructions ?? Array.Empty<SourceConstruction>())
            {
                if (!construction.OwnerIsThis || construction.MemberName == null)
                {
                    continue;
                }

                if (construction.NameArgument == null)
                {
                    errors.Add(new LedgerIssue(
                        LedgerIssueKind.UnreadableName, construction.MemberName,
                        "'" + construction.MemberName + "' on '" + request.TypeName
                        + "' derives its identity from an expression this tool cannot read"
                        + " — pass nameof(" + construction.MemberName + ") so the name is a name"));
                    continue;
                }

                // ⛔ Never last-writer-wins. A member constructed twice with two
                // different names has TWO identities in the build — one per
                // construction — and a record can name only one of them, so
                // silently keeping the last would write a file that is wrong in
                // the one way a record must not be: quietly. `IdArgumentsByMember`
                // states the same rule one file over ("ALL of them, never the last
                // one seen"), and the allocator this replaced reported it too.
                if (namedBy.TryGetValue(construction.MemberName, out string alreadyNamed)
                    && !string.Equals(alreadyNamed, construction.NameArgument, StringComparison.Ordinal))
                {
                    errors.Add(new LedgerIssue(
                        LedgerIssueKind.MemberConstructedTwice, construction.MemberName,
                        "'" + construction.MemberName + "' on '" + request.TypeName
                        + "' is constructed twice, naming '" + alreadyNamed + "' and '"
                        + construction.NameArgument + "' — that is two identities for one member,"
                        + " and only one of them can be recorded. Construct it once, naming the"
                        + " member it is assigned to."));
                    continue;
                }

                namedBy[construction.MemberName] = construction.NameArgument;
            }

            foreach (string member in request.NewMembers ?? Array.Empty<string>())
            {
                // ⛔ Never over a construction already read from source. A member
                // this run is about to WRITE derives from its own name, because
                // that is what the emitted `nameof(...)` resolves to — but an
                // already-converted field stays in the plan so a re-run is a no-op,
                // and the transform leaves its construction exactly as it found it.
                // Defaulting that member here replaces the name the program will
                // actually pass with the name it ought to have passed, which is
                // precisely the fault below, erased on its way to being reported.
                if (!namedBy.ContainsKey(member))
                {
                    namedBy[member] = member;
                }
            }

            // Two members, one identity. Reported against BOTH, because either is
            // a legitimate place to make the edit and naming only one sends the
            // reader looking for a rule the other member appears to satisfy.
            var byIdentity = new Dictionary<uint, string>();
            // One issue per MEMBER, not one per colliding pair: three members on
            // one identity make two pairs, and reporting per pair named the first
            // member twice.
            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in namedBy)
            {
                uint id = Fnv1a.ComputeMethodId(request.TypeName, pair.Value);
                identities[pair.Key] = id;

                if (byIdentity.TryGetValue(id, out string first))
                {
                    string message =
                        "'" + first + "' and '" + pair.Key + "' on '" + request.TypeName
                        + "' both derive identity 0x" + id.ToString("X8", CultureInfo.InvariantCulture)
                        + " — an object registers each identity once, so constructing both"
                        + " throws out of OnNetworkSpawn and the object is destroyed rather"
                        + " than spawned. Each must derive from its own member name.";

                    // Against both members, because either is a legitimate place
                    // to make the edit and the structured MemberName field is what
                    // the advisor emits — carrying only the second sends a reader
                    // to a member that, read on its own, satisfies the rule.
                    if (reported.Add(first))
                    {
                        errors.Add(new LedgerIssue(LedgerIssueKind.SharedIdentity, first, message));
                    }

                    if (reported.Add(pair.Key))
                    {
                        errors.Add(new LedgerIssue(LedgerIssueKind.SharedIdentity, pair.Key, message));
                    }
                }
                else
                {
                    byIdentity.Add(id, pair.Key);
                }
            }

            if (errors.Count > 0)
            {
                return new DerivationVerdict(errors, identities, record: null);
            }

            return new DerivationVerdict(
                errors,
                identities,
                new LedgerDocument(
                    request.TypeName,
                    identities,
                    request.Rpcs ?? new SortedDictionary<string, string>(StringComparer.Ordinal)));
        }

        /// <summary>
        /// Refuses a set of sidecar paths that is one file under two spellings.
        /// </summary>
        /// <remarks>
        /// Two type names differing only by case resolve to one file on a Windows
        /// or macOS checkout, so a batch writing both would have the second
        /// overwrite the first's record.
        /// </remarks>
        public static IReadOnlyList<LedgerIssue> ValidateWriteSet(IEnumerable<string> fileNames)
        {
            var issues = new List<LedgerIssue>();
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in fileNames)
            {
                if (seen.TryGetValue(name, out string first))
                {
                    issues.Add(new LedgerIssue(
                        LedgerIssueKind.IdSpaceHazard, null,
                        "'" + name + "' collides with '" + first + "' on a case-insensitive filesystem"));
                }
                else
                {
                    seen.Add(name, name);
                }
            }

            return issues;
        }
    }
}
