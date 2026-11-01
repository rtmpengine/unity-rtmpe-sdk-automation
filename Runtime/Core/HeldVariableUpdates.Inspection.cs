using System;

namespace RTMPE.Core
{
    internal sealed partial class HeldVariableUpdates
    {
        // Read without releasing: a held spawn needs the grants that preceded it
        // before a departure can decide its lifetime.
        internal string LastMatchingValue(ulong objectId, Func<byte[], string> read)
        {
            string last = null;
            foreach (var held in _queue)
            {
                if (held.ObjectId != objectId) continue;
                string value = read(held.Frame);
                if (value != null) last = value;
            }
            return last;
        }
    }
}
