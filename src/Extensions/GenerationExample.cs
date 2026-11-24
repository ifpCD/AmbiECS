using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace AmbiECS.ReferenceDesign;

// The annotation pattern. Columns are static storage you declare; glue links an object's members to them; the generator writes
// the rest: allocation, growth, swap-back, row writes, setters, read-back, and the Unity messages.
//
// [Input]    written from the object: when its row is created, then whenever what feeds it changes
// [State]    owned by jobs: set to its default when a row is created, and carried along with its row
// [Scratch]  owned by jobs, not tied to rows: PerRow elements per row of capacity (following growth) or a fixed Length
//
// Tracked: a glued settable partial property, or one marked [Track] for the computed glue reading it. Its column, and those of the
// computed members reading it, are written on change: at once while the graph is idle (all of Update), at the next flush
// otherwise. MarkDirty() never re-reads it.
// Untracked: any other member, re-read after MarkDirty(). A computed one is also rewritten when the tracked properties it reads change.
// [Readback] reads a get-only partial property back from its row, after completing the graph, or only the JobHandle named by
// After. Anything not annotated is yours, kept in the optional partial hooks:
// OnColumnsAllocated, OnColumnsResized, OnColumnsDeallocating, OnRowLoaded, OnRowSynced and OnRowRemoved.
[Ambi(Capacity = 10)]
internal partial class Foo : MonoBehaviour
{
    const int BAR_STRIDE = 15;

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
