// RTMPE SDK — Runtime/Rooms/PropertyWriteFlights.cs
//
// One property write in flight per subject (the room, or this client's own
// player), and what was written while it was (audit P3-E4, P7-M5).
//
// The server applies a write only at the version after the one it holds, and
// a client stamps its write from the last broadcast it received.  So two writes
// sent in one round trip name the same version and the server refuses the
// second — `SetRoomProperty("Round", 3); LoadScene("Arena2")` loaded nothing —
// and a writer that missed a broadcast names a version the server has moved
// past.  Held here, a second write waits for the first's broadcast and goes out
// at the version that broadcast set; and when the server answers a conflict
// with its state (`"resync": true`), the write it refused goes out again at the
// version that state carries.

using System;
using System.Collections.Generic;

namespace RTMPE.Rooms
{
    internal sealed class PropertyWriteFlights
    {
        /// <summary>
        /// The most writes waiting behind one in flight.  Writes merge while their
        /// union fits the subject's key cap and one datagram, so this is reached
        /// only by a caller writing more than one write may carry, many times
        /// over, inside one round trip; past it a write that does not merge into
        /// the last is refused to its caller.
        /// </summary>
        internal const int MaxWaiting = 32;

        private sealed class Flight
        {
            public int Expected;
            public Dictionary<string, PropertyValue> OnTheWire;
            public readonly List<Dictionary<string, PropertyValue>> Waiting =
                new List<Dictionary<string, PropertyValue>>();
        }

        private readonly Dictionary<string, Flight> _flights =
            new Dictionary<string, Flight>(StringComparer.Ordinal);

        internal int Count => _flights.Count;

        internal bool InFlight(string subject) => _flights.ContainsKey(subject);

        internal int WaitingCount(string subject)
            => _flights.TryGetValue(subject, out var f) ? f.Waiting.Count : 0;

        /// <summary>
        /// Keep <paramref name="properties"/> behind the write in flight for
        /// <paramref name="subject"/>, merged into the last waiting write while
        /// the union stays within <paramref name="keyCap"/> and
        /// <paramref name="fits"/> one datagram, and the two do not both load a
        /// scene.  Returns whether a write was in flight — when it was not,
        /// nothing is kept and the caller sends.
        /// </summary>
        /// <remarks>
        /// <para>🔑 The merge is judged on the union, never on the parts: two writes
        /// that each fit can make one that does not, and a waiting write that
        /// cannot be sent is lost whole — every key in it, a scene load
        /// included — when its turn comes.</para>
        /// <para>⛔ Two scene loads never merge. A load is an instruction, not a
        /// value: merged, the later <c>__scene</c> replaces the earlier, so the
        /// earlier layer is announced to nobody and no member loads it — while
        /// the stack the later write carries tells a player who joins that it is
        /// open (audit P7-E3 review).</para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// <see cref="MaxWaiting"/> writes are already waiting and this one does
        /// not merge into the last.
        /// </exception>
        internal bool Defer(
            string subject,
            IReadOnlyDictionary<string, PropertyValue> properties,
            int keyCap,
            Func<IReadOnlyDictionary<string, PropertyValue>, bool> fits)
        {
            if (!_flights.TryGetValue(subject, out var flight)) return false;
            var waiting = flight.Waiting;
            if (waiting.Count > 0)
            {
                var last = waiting[waiting.Count - 1];
                if (!BothLoadAScene(last, properties) && UnionCount(last, properties) <= keyCap)
                {
                    var union = Copy(last);
                    foreach (var kv in properties) union[kv.Key] = kv.Value;
                    if (fits(union))
                    {
                        waiting[waiting.Count - 1] = union;
                        return true;
                    }
                }
            }
            if (waiting.Count >= MaxWaiting)
                throw new InvalidOperationException(
                    $"{MaxWaiting} property writes are already waiting for the one in flight, and this one " +
                    "does not fit beside the last of them. Write fewer or smaller properties at a time, or " +
                    "wait for the change event of the ones already sent.");
            waiting.Add(Copy(properties));
            return true;
        }

        /// <summary>A write for <paramref name="subject"/> went out at <paramref name="expected"/>.</summary>
        internal void Sent(string subject, int expected, IReadOnlyDictionary<string, PropertyValue> properties)
        {
            if (!_flights.TryGetValue(subject, out var flight))
            {
                flight = new Flight();
                _flights[subject] = flight;
            }
            flight.Expected  = expected;
            flight.OnTheWire = Copy(properties);
        }

        /// <summary>
        /// A broadcast for <paramref name="subject"/> reached <paramref name="version"/>.
        /// When it covers the write in flight, that write is done, and the next
        /// waiting one — the one to send now — is returned; null otherwise.
        /// </summary>
        internal Dictionary<string, PropertyValue> Confirmed(string subject, int version)
        {
            if (!_flights.TryGetValue(subject, out var flight) || version < flight.Expected) return null;
            return Advance(subject, flight);
        }

        /// <summary>
        /// The server refused a write for <paramref name="subject"/> and sent its
        /// state: the write in flight is returned to be sent again, and what was
        /// waiting stays behind it.  Null when nothing was in flight.
        /// </summary>
        internal Dictionary<string, PropertyValue> Refused(string subject)
        {
            if (!_flights.TryGetValue(subject, out var flight)) return null;
            return flight.OnTheWire;
        }

        /// <summary>
        /// The write in flight for <paramref name="subject"/> drew no answer in
        /// time; the next waiting one is returned to be sent, null when none is.
        /// </summary>
        internal Dictionary<string, PropertyValue> Expired(string subject)
        {
            if (!_flights.TryGetValue(subject, out var flight)) return null;
            return Advance(subject, flight);
        }

        internal void Clear() => _flights.Clear();

        /// <summary>
        /// Lays this client's writes for <paramref name="subject"/> that no
        /// broadcast has confirmed — the one in flight, then each waiting one in
        /// order — over <paramref name="properties"/>, leaving it as those writes
        /// will.  A deletion removes its key.
        /// </summary>
        internal void Overlay(string subject, Dictionary<string, PropertyValue> properties)
        {
            if (!_flights.TryGetValue(subject, out var flight)) return;
            if (flight.OnTheWire != null) Apply(flight.OnTheWire, properties);
            foreach (var write in flight.Waiting) Apply(write, properties);
        }

        private static void Apply(IReadOnlyDictionary<string, PropertyValue> write, Dictionary<string, PropertyValue> onto)
        {
            foreach (var kv in write)
            {
                if (kv.Value.IsDeletion) onto.Remove(kv.Key);
                else onto[kv.Key] = kv.Value;
            }
        }

        private static bool BothLoadAScene(
            Dictionary<string, PropertyValue> waiting, IReadOnlyDictionary<string, PropertyValue> properties)
            => waiting.ContainsKey(ReservedPropertyKeys.Scene) && properties.ContainsKey(ReservedPropertyKeys.Scene);

        private Dictionary<string, PropertyValue> Advance(string subject, Flight flight)
        {
            if (flight.Waiting.Count == 0)
            {
                _flights.Remove(subject);
                return null;
            }
            var next = flight.Waiting[0];
            flight.Waiting.RemoveAt(0);
            // The write that goes out next is this client's own unconfirmed write
            // from now, not from when it is sent: the broadcast that promoted it
            // raises its events first, and a load made from a handler of them is
            // stacked on Overlay — which read the promoted write nowhere and
            // stacked the load on the scene before it (the second look at P7-E3).
            // Sent restates it with the version it goes out at.
            flight.OnTheWire = next;
            return next;
        }

        private static int UnionCount(Dictionary<string, PropertyValue> a, IReadOnlyDictionary<string, PropertyValue> b)
        {
            int n = a.Count;
            foreach (var key in b.Keys)
                if (!a.ContainsKey(key)) n++;
            return n;
        }

        private static Dictionary<string, PropertyValue> Copy(IReadOnlyDictionary<string, PropertyValue> properties)
        {
            var copy = new Dictionary<string, PropertyValue>(StringComparer.Ordinal);
            foreach (var kv in properties) copy[kv.Key] = kv.Value;
            return copy;
        }
    }
}
