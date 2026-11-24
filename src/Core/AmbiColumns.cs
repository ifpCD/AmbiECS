using System;
using Unity.Collections;

namespace AmbiECS;

// What generated code writes columns with, and what hand-kept buffers can use in the hooks. Only ever called while the graph is
// idle; nothing here allocates but Resize.
public static class AmbiColumn
{
    // Reallocates to length, keeping what fits. Creates the column when it does not exist yet.
    // todo: defer deallocation?
    public static void Resize<TValue>(ref this NativeArray<TValue> column, int length, Allocator allocator = Allocator.Persistent)
        where TValue : struct
    {
        NativeArray<TValue> resized = new(length, allocator);

        if (column.IsCreated)
        {
            NativeArray<TValue>.Copy(column, resized, Math.Min(column.Length, length));
            column.Dispose();
        }

        column = resized;
    }

    // A row's block of stride elements moves to another row.
    public static void MoveBlock<TValue>(NativeArray<TValue> column, int from, int to, int stride)
        where TValue : struct => NativeArray<TValue>.Copy(column, from * stride, column, to * stride, stride);

    public static void MoveBlock<TValue>(TValue[] column, int from, int to, int stride) =>
        Array.Copy(column, from * stride, column, to * stride, stride);

    // A row's references: the targets' handles front-packed in order, missing and destroyed ones skipped, the rest Null.
    // Jobs stop at the first Null; a target that leaves or is destroyed later is skipped by AmbiLookup instead.
    public static void WriteHandles<TTarget>(NativeArray<AmbiHandle> column, int row, int stride, ReadOnlySpan<TTarget> targets)
        where TTarget : class, IAmbiRegistrant<TTarget>
    {
        int slot = row * stride;
        int end = slot + stride;

        for (int i = 0; i < targets.Length && slot < end; i++)
        {
            AmbiHandle handle = Ambi<TTarget>.HandleOf(targets[i]);

            if (!handle.IsNull)
                column[slot++] = handle;
        }

        for (; slot < end; slot++)
            column[slot] = AmbiHandle.Null;
    }

    // A row's values: the first stride of them in order, the rest default.
    public static void WriteValues<TValue>(NativeArray<TValue> column, int row, int stride, ReadOnlySpan<TValue> values)
        where TValue : struct
    {
        int start = row * stride;
        int count = Math.Min(values.Length, stride);

        for (int i = 0; i < count; i++)
            column[start + i] = values[i];

        for (int i = count; i < stride; i++)
            column[start + i] = default;
    }
}
