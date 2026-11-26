using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

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

    // the table covers every issued handle, and identity 0 never matches Null
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly unsafe bool TryResolve(AmbiHandle handle, out int row)
    {
        Entry entry = UnsafeUtility.ReadArrayElement<Entry>(
            NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(Entries),
            handle.Index
        );
        row = entry.Version == handle.Version ? entry.Row : AmbiSlot.NONE;
        return row >= 0;
    }
}
