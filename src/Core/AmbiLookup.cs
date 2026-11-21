using System.Runtime.CompilerServices;
using Unity.Collections;

public struct AmbiLookup
{
    [ReadOnly]
    public NativeArray<int> HandleToSoA;

    [ReadOnly]
    public NativeArray<int> HandleVersions;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryResolve(AmbiHandle handle, out int row)
    {
        if (handle.IsNull || HandleVersions[handle.Index] != handle.Version)
        {
            row = AmbiSlot.NONE;
            return false;
        }

        row = HandleToSoA[handle.Index];
        return row >= 0;
    }
}
