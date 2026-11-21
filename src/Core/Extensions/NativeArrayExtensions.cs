using Unity.Collections;

namespace AmbiECS.Extensions;

public static class NativeArrayExtensions
{
    public static void DisposeExpand<T>(ref this NativeArray<T> array, int length)
        where T : struct
    {
        if (array == default)
            return;

        NativeArray<T> newBuffer = new(length, Allocator.Persistent);

        array.CopyTo(newBuffer);
        array.Dispose();
        array = newBuffer;
    }
}