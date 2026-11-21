using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

// What the column attributes expand to, written by hand until the [AmbiSync] generator exists. Stateless, allocation-free, and
// only ever called from LoadObjectToRow, SyncObjectToRow and SwapBackRow, while the job graph is idle.
internal static class AmbiColumn
{
    // [AmbiStatefulStride] / [AmbiRefStride] SwapBackRow: the last row's block moves into the removed row.
    public static void SwapBack<TValue>(NativeArray<TValue> column, int removedIndex, int lastIndex, int stride = 1)
        where TValue : struct
    {
        if (removedIndex == lastIndex)
            return;

        if (stride == 1)
            column[removedIndex] = column[lastIndex];
        else
            NativeArray<TValue>.Copy(column, lastIndex * stride, column, removedIndex * stride, stride);
    }
}

internal static class AmbiRefs
{
    // [AmbiRef]: the handle a reference column holds. Null for no target or a destroyed one.
    public static AmbiHandle HandleOf<TTarget>(TTarget target)
        where TTarget : AmbiMonoBehaviour<TTarget> => target != null ? target.Handle : AmbiHandle.Null;

    // [AmbiRefStride] SyncObjectToRow: the row's handles front-packed in list order, missing entries skipped, the rest Null.
    // Jobs stop at the first Null; a target disabled or destroyed later is skipped by AmbiLookup instead.
    public static void WriteStride<TTarget>(NativeArray<AmbiHandle> column, int row, int stride, List<TTarget> targets)
        where TTarget : AmbiMonoBehaviour<TTarget>
    {
        int start = row * stride;
        int count = targets.Count < stride ? targets.Count : stride;
        int slot = 0;

        for (int i = 0; i < count; i++)
        {
            TTarget target = targets[i];

            if (target != null)
                column[start + slot++] = target.Handle;
        }

        for (; slot < stride; slot++)
            column[start + slot] = AmbiHandle.Null;
    }

    // [AmbiRefStride] OnValidate: caps the authoring list at its stride. Only ever shrinks it.
    public static void ClampStride<TTarget>(List<TTarget> targets, int stride, Object context)
    {
        if (targets.Count <= stride)
            return;

#if UNITY_EDITOR
        Debug.LogWarning($"{context.name}: at most {stride} {typeof(TTarget).Name} references; removed the last {targets.Count - stride}.", context);
#endif

        targets.RemoveRange(stride, targets.Count - stride);
    }
}
