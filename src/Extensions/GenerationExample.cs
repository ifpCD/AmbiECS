using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace AmbiECS.ReferenceDesign;

[Ambi(Capacity = 10)]
internal partial class Foo : MonoBehaviour
{
    const int BAR_STRIDE = 15;
    const int HISTORY = 4;

    public static JobHandle PromisedCompletionHandle;

    [Input]
    public static NativeArray<float> TargetVolumes;

    [Input]
    public static float[] ConstantFactors;

    [Input(Stride = BAR_STRIDE)]
    public static NativeArray<AmbiHandle> BarHandles;

    // Does not have an OOP input, but instance swap backs preserve the information between frames.
    [State(Default = 1f)]
    public static NativeArray<float> CurrentVolumes;

    [State(Stride = HISTORY)]
    public static NativeArray<float> VolumeHistories;

    // Manually managed via optional callbacks
    [Scratch(Length = 2048)]
    public static NativeArray<int> ArbitraryScratch;

    [Track]
    [field: SerializeField]
    public partial float Volume { get; set; } = 1f;

    // If Volume changes, this recomputes and updates TargetVolumes
    [Glue(nameof(TargetVolumes))]
    float TargetVolume => Mathf.Clamp01(Volume);

    [field: SerializeField]
    [Glue(nameof(ConstantFactors))]
    public partial float ConstantFactor { get; set; } = 3.8f;

    // Automatically resolves Bust friendly Bar AmbiHandles
    [Glue(nameof(BarHandles))]
    public List<Bar> Bars = [];

    // DOD value that can be read back with the promise that it's ready after a specified JobHandle
    [Readback(nameof(CurrentVolumes), After = nameof(PromisedCompletionHandle))]
    public partial float CurrentVolume { get; }

    // span over the row's block, valid until the next flush
    [Readback(nameof(VolumeHistories), After = nameof(PromisedCompletionHandle))]
    public partial ReadOnlySpan<float> VolumeHistory { get; }

    static partial void OnColumnsAllocated(int capacity) { }

    static partial void OnColumnsResized(int rowCount, int capacity) { }

    static partial void OnColumnsDeallocating() { }

    partial void OnRowLoaded(int row) { }

    partial void OnRowSynced(int row) { }

    partial void OnRowRemoved(int row, int lastRow) { }
}

[Ambi(Capacity = 150)]
internal partial class Bar : MonoBehaviour
{
    [Input]
    public static NativeArray<int> Contributions;

    [Glue(nameof(Contributions))]
    public int Contribution { get; set; } = 12;
}
