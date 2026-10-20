// RTMPE SDK — Runtime/Core/FlushWalkCursor.cs
//
// Where the per-tick flush walk begins: which owned object it starts at, and
// which component each object's fan-out starts at.

namespace RTMPE.Core
{
    /// <summary>
    /// The start of the flush walk, advanced once per tick like an odometer:
    /// the object start moves every tick, and the component start moves once
    /// per full turn of the objects.
    /// </summary>
    /// <remarks>
    /// <para>The walk exists for a link whose reliable window frees fewer slots
    /// per tick than the owned objects have dirty components: whatever the
    /// walk reaches first is sent and the rest is deferred, so the pairs at the
    /// head of the walk are the pairs that move.  Two starts that both advance
    /// every tick put the head at (t mod N, t mod M) — a lattice that reaches
    /// every pair only when the slots freed per tick are at least
    /// gcd(N, M); eight objects of two components at one slot a tick would
    /// serve object o its component o mod 2 and no other for as long as the
    /// link stayed slow, which is the starvation the walk is there to
    /// remove.  Turned like an odometer, the head visits every (object,
    /// component) pair once in N·M ticks at one slot a tick, whatever N and
    /// M share.</para>
    ///
    /// <para>The object start is clamped to the count the walk is given, so a
    /// registry that shrank below it restarts the turn at the same component
    /// start — the survivors are offered again rather than the walk indexing
    /// past the end; a removal shifts the indices behind it either way, and
    /// coverage is then eventual rather than exact.  A registry that grows by
    /// an object every tick never completes a turn, and the component start
    /// then stands still: the head is moving over new objects for as long as
    /// that lasts.</para>
    ///
    /// <para>A class rather than a struct: the manager keeps it in a field,
    /// and a mutable struct in a <c>readonly</c> field advances a defensive
    /// copy — every tick begins at (0, 0), with no diagnostic from the
    /// compiler.</para>
    /// </remarks>
    internal sealed class FlushWalkCursor
    {
        private int _objectStart;
        private int _componentStart;

        /// <summary>The component index each object's fan-out begins at, modulo its own count.</summary>
        public int ComponentStart => _componentStart;

        /// <summary>The object the walk begins at, for a walk over <paramref name="objects"/> objects.</summary>
        public int ObjectStart(int objects)
        {
            if (_objectStart >= objects) _objectStart = 0;
            return _objectStart;
        }

        /// <summary>
        /// Move to the next tick's start: the next object, and — when that
        /// completes a turn of the <paramref name="objects"/> — the next
        /// component.
        /// </summary>
        public void Advance(int objects)
        {
            if (objects <= 0) { _objectStart = 0; return; }
            _objectStart++;
            if (_objectStart >= objects)
            {
                _objectStart = 0;
                _componentStart = (_componentStart + 1) & 0x3FFFFFFF;
            }
        }
    }
}
