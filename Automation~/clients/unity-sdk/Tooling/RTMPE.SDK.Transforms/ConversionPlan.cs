using System.Collections.Generic;

namespace RTMPE.SDK.Transforms
{
    /// <summary>Which of the two conversion shapes a planned member takes.</summary>
    public enum ConversionArm
    {
        /// <summary>Retype the plain private field itself to a NetworkVariable.</summary>
        InPlace,

        /// <summary>
        /// Leave the Unity-serialized config field untouched and add a separate
        /// NetworkVariable field seeded from it — the HealthController shape.
        /// </summary>
        Companion,
    }

    /// <summary>One member's conversion.</summary>
    public sealed class PlannedConversion
    {
        public PlannedConversion(
            string memberName, string networkVariableTypeName,
            ConversionArm arm, string companionFieldName = null)
        {
            MemberName = memberName;
            NetworkVariableTypeName = networkVariableTypeName;
            Arm = arm;
            CompanionFieldName = companionFieldName;
        }

        public string MemberName { get; }

        /// <summary>The destination type from the closed map — never computed here.</summary>
        public string NetworkVariableTypeName { get; }

        public ConversionArm Arm { get; }

        /// <summary>The new field's name for the companion arm; null for in-place.</summary>
        public string CompanionFieldName { get; }
    }

    /// <summary>
    /// The input the generation transform executes: classifier verdicts in
    /// declaration order. The transform is pure, and carries no identity at all —
    /// it emits the member's own name and the identity follows from it, so there
    /// is no id for a plan to get wrong or for a caller to have to supply.
    /// </summary>
    public sealed class ConversionPlan
    {
        public ConversionPlan(IReadOnlyList<PlannedConversion> conversions)
        {
            Conversions = conversions;
        }

        public IReadOnlyList<PlannedConversion> Conversions { get; }
    }
}
