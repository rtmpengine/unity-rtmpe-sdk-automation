// RTMPE SDK — Runtime/Core/SubscriberAdoption.cs
//
// The one reading of "carry the subscribers forward", shared by every manager
// that is replaced when the connection is rebuilt.
//
// Three managers implement adoption and each carries a dozen events, so the
// alternative to a shared rule is thirty-odd hand-written combines that agree
// only for as long as somebody keeps checking.  They already disagreed once
// about something subtler than a missing event: none of them asked whether the
// replacement was holding the handler already.

using System;

namespace RTMPE.Core
{
    /// <summary>
    /// Combines a predecessor's subscribers onto its replacement.
    /// </summary>
    internal static class SubscriberAdoption
    {
        /// <summary>
        /// Return <paramref name="current"/> extended with every subscriber of
        /// <paramref name="previous"/> that it was not already holding.
        /// </summary>
        /// <remarks>
        /// The rebuild is invisible from outside the SDK — the manager
        /// properties answer with whichever instance is current — so an
        /// application has two equally reasonable places to subscribe: once
        /// before <c>Connect()</c>, or each time it is told the session is up.
        /// Adoption serves the first.  Without this rule it punishes the second:
        /// the handler is carried onto the replacement and then added to it
        /// again, and because every rebuild adopts a list that already contains
        /// it, the count tracks the number of reconnects rather than stopping at
        /// two.  A session that flaps dispatches the application's handler
        /// hundreds of times per event, with no error, no warning, and no
        /// counter that separates it from a busy game.
        ///
        /// <para>Identity is the delegate's, so a method group and a lambda over
        /// the same method are correctly two subscribers, exactly as they are to
        /// <c>-=</c>.</para>
        ///
        /// <para>The comparison is a multiset difference, not a set membership
        /// test, and the difference is the boundary case: a handler the
        /// application deliberately subscribed twice on the predecessor arrives
        /// twice even when the replacement already holds one copy.
        /// De-duplication is between two instances and never within one —
        /// collapsing a repeat the application asked for would change the
        /// behaviour of a working project at its first reconnect, which is this
        /// failure in the other direction.  Written as membership first, and
        /// that version silently dropped both of the predecessor's copies when
        /// the replacement held one, which is the thing this paragraph
        /// promises does not happen.</para>
        ///
        /// <para>Nothing is pushed back onto <paramref name="previous"/>: a
        /// transport callback already in flight can still reach it, and it must
        /// not gain subscribers on its way out.</para>
        /// </remarks>
        internal static T Carry<T>(T current, T previous) where T : Delegate
        {
            if (previous == null) return current;

            Delegate[] alreadyHeld = current == null
                ? Array.Empty<Delegate>()
                : current.GetInvocationList();

            // Each copy the replacement already holds accounts for one copy from
            // the predecessor and no more.  A plain membership test would let a
            // single held copy absorb every repeat the predecessor carried.
            bool[] spent = new bool[alreadyHeld.Length];

            T combined = current;
            Delegate[] incoming = previous.GetInvocationList();
            for (int i = 0; i < incoming.Length; i++)
            {
                int match = -1;
                for (int j = 0; j < alreadyHeld.Length; j++)
                {
                    if (spent[j] || !alreadyHeld[j].Equals(incoming[i])) continue;
                    match = j;
                    break;
                }

                if (match >= 0)
                {
                    spent[match] = true;
                    continue;
                }

                combined = (T)Delegate.Combine(combined, incoming[i]);
            }

            return combined;
        }
    }
}
