// RTMPE SDK — Editor/WizardLabelLayout.cs
//
// How wide the label column of a wizard step has to be.
//
// Unity's label column is a fixed width that does NOT grow with the window, so
// a label longer than it is clipped — with no ellipsis and no tooltip to say so.
// That is how "API-Key Seal Public Key (X25519)" reached a tester as
// "API-Key Seal Public Key (X", beside three other fields cut in the same
// place, on a window with several hundred unused pixels to its right.
//
// 🔑 The width is DERIVED from the labels that are actually drawn, never set to
// a number somebody has to keep right: a label edited later re-measures itself,
// and a translated one does too.
//
// This file is arithmetic on purpose. `SetupWizard.cs` is compiled by no shard —
// it is named, with its reason, in the Editor assembly's own coverage register —
// so a width computed there could only ever be asserted over its own text.

using System.Collections.Generic;

namespace RTMPE.Editor
{
    /// <summary>
    /// The label-column width for a wizard step, from the widest label it draws
    /// and the width of the window drawing it.
    /// </summary>
    internal static class WizardLabelLayout
    {
        /// <summary>
        /// The narrowest the value column may be squeezed to before the label
        /// column stops growing.  A 64-character hex key is the longest thing
        /// pasted into this window, and a field too narrow to show a usable run
        /// of it trades one unreadable column for another.
        /// </summary>
        internal const float MinimumFieldWidth = 180f;

        /// <summary>
        /// Unity's own conventional label width, and the floor here.  A step
        /// whose labels are all short keeps the proportions an editor user
        /// expects rather than collapsing to the text.
        /// </summary>
        internal const float MinimumLabelWidth = 150f;

        /// <summary>Breathing room between the label and the field it names.</summary>
        internal const float Gap = 10f;

        /// <summary>
        /// The widest of a step's measured labels.
        /// </summary>
        /// <remarks>
        /// 🔑 Here rather than at the call site, and for one reason: a fold
        /// written there — <c>if (w &gt; widest)</c> — is one character from
        /// being <c>widest = w</c>, which sizes the column from the LAST label
        /// instead of the widest and clips today, because the last of the seven
        /// is not the longest. `SetupWizard.cs` is compiled by no shard, so that
        /// mistake could only ever be read, and it reads correctly.
        /// </remarks>
        internal static float Widest(IReadOnlyList<float> measured)
        {
            if (measured == null)
            {
                return 0f;
            }

            float widest = 0f;
            for (int i = 0; i < measured.Count; i++)
            {
                float candidate = measured[i];
                // A NaN loses every comparison, so it neither wins nor displaces
                // a real measurement; LabelWidth answers the floor if one is all
                // there is.
                if (candidate > widest)
                {
                    widest = candidate;
                }
            }

            return widest;
        }

        /// <summary>
        /// The width to give the label column.
        /// </summary>
        /// <param name="widestLabel">
        /// The widest drawn label, in points, as the label style measures it.
        /// </param>
        /// <param name="windowWidth">The width available to the whole row.</param>
        /// <remarks>
        /// ⛔ The result is always positive and always finite, including on the
        /// first layout pass of a window that has not been sized yet — where
        /// `currentViewWidth` is small or zero — because a non-positive label
        /// width is not a narrower column but a broken one.
        /// </remarks>
        internal static float LabelWidth(float widestLabel, float windowWidth)
        {
            // A measurement that is not a number cannot be compared: every
            // comparison below would answer false and the NaN would travel into
            // the layout. Answering the floor is a readable column; answering
            // NaN is a window that does not draw.
            if (float.IsNaN(widestLabel) || float.IsInfinity(widestLabel)
                || float.IsNaN(windowWidth) || float.IsInfinity(windowWidth))
            {
                return MinimumLabelWidth;
            }

            float wanted = widestLabel + Gap;

            // What the window can spare while the value column stays usable.
            // On a window too narrow to give both, the label keeps the floor and
            // the field takes the loss — the same trade Unity makes by default,
            // and the one that keeps a row identifiable.
            float ceiling = windowWidth - MinimumFieldWidth;
            if (ceiling < MinimumLabelWidth)
            {
                ceiling = MinimumLabelWidth;
            }

            if (wanted > ceiling)
            {
                return ceiling;
            }

            return wanted < MinimumLabelWidth ? MinimumLabelWidth : wanted;
        }
    }
}
