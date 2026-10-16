using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

internal static class AmbiColumn
{
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
    public static AmbiHandle HandleOf<TTarget>(TTarget target)
        where TTarget : AmbiMonoBehaviour<TTarget> => target != null ? target.Handle : AmbiHandle.Null;

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
