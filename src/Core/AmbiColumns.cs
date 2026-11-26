using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace AmbiECS;

// Column access for generated code and hooks. Main thread, graph idle, so no NativeArray checks.
public static unsafe class AmbiColumn
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref TValue At<TValue>(in NativeArray<TValue> column, int index)
        where TValue : struct =>
        ref UnsafeUtility.ArrayElementAsRef<TValue>(
            NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(column),
            index
        );

    // row's block of stride elements
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Span<TValue> Block<TValue>(in NativeArray<TValue> column, int row, int stride)
        where TValue : struct => MemoryMarshal.CreateSpan(ref At(column, row * stride), stride);

    // keeps the first `keep` elements, rest uninitialized. creates the column if missing
    public static void Resize<TValue>(ref this NativeArray<TValue> column, int length, int keep)
        where TValue : struct
    {
        NativeArray<TValue> resized = new(
            length,
            Allocator.Persistent,
            NativeArrayOptions.UninitializedMemory
        );

        if (column.IsCreated)
        {
            Block(column, 0, keep).CopyTo(Block(resized, 0, keep));
            column.Dispose();
        }

        column = resized;
    }

    // constant stride in generated code, so the copy unrolls
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MoveBlock<TValue>(
        in NativeArray<TValue> column,
        int from,
        int to,
        int stride
    )
        where TValue : struct => Block(column, from, stride).CopyTo(Block(column, to, stride));

    public static void MoveBlock<TValue>(TValue[] column, int from, int to, int stride) =>
        Array.Copy(column, from * stride, column, to * stride, stride);

    // A row's references: the targets' handles front-packed in order, missing and destroyed ones skipped, the rest Null.
    // Jobs stop at the first Null; a target that leaves or is destroyed later is skipped by AmbiLookup instead.
    public static void WriteHandles<TTarget>(
        in NativeArray<AmbiHandle> column,
        int row,
        int stride,
        ReadOnlySpan<TTarget> targets
    )
        where TTarget : class, IAmbiRegistrant<TTarget>
    {
        Span<AmbiHandle> block = Block(column, row, stride);
        int count = 0;

        for (int i = 0; i < targets.Length && count < stride; i++)
        {
            AmbiHandle handle = Ambi<TTarget>.HandleOf(targets[i]);

            if (!handle.IsNull)
                block[count++] = handle;
        }

        block[count..].Clear();
    }
}
