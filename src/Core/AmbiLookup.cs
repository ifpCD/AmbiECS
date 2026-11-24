using System.Runtime.CompilerServices;
using Unity.Collections;

namespace AmbiECS;

public struct AmbiLookup
{
    // One per identity, row and version side by side: a handle resolves in a single read.
    public struct Entry
    {
        internal int Row; // -1 while it has none
        internal int Version;
    }

    [ReadOnly]
    public NativeArray<Entry> Entries;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryResolve(AmbiHandle handle, out int row)
    {
        if (handle.IsNull)
        {
            row = AmbiSlot.NONE;
            return false;
        }

        Entry entry = Entries[handle.Index];
        row = entry.Version == handle.Version ? entry.Row : AmbiSlot.NONE;
        return row >= 0;
    }
}
