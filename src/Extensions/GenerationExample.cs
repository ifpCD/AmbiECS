using System.Collections.Generic;
using Unity.Collections;
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
// A get-only partial property reads its row back. Anything not annotated is yours, kept in the optional partial hooks:
// OnColumnsAllocated, OnColumnsResized, OnColumnsDeallocating, OnRowLoaded, OnRowSynced and OnRowRemoved.
[Ambi(Capacity = 10)]
internal partial class Foo : MonoBehaviour
{
    const int BAR_STRIDE = 15;

    [Input]
    public static NativeArray<float> TargetVolumes;

    [Input]
    public static float[] ConstantFactors;

    [Input(Stride = BAR_STRIDE)]
    public static NativeArray<AmbiHandle> BarHandles;

    [State(Default = 1f)]
    public static NativeArray<float> CurrentVolumes;

    [Scratch(PerRow = 131)]
    public static NativeArray<int> ArbitraryScratch;

    // tracked by [Track]: no column of its own, but TargetVolume reads it
    [Track]
    [field: SerializeField]
    public partial float Volume { get; set; } = 1f;

    // computed: rewritten whenever Volume changes
    [Glue(nameof(TargetVolumes))]
    float TargetVolume => Mathf.Clamp01(Volume);

    // tracked: one write per row, unless it changes
    [field: SerializeField]
    [Glue(nameof(ConstantFactors))]
    public partial float ConstantFactor { get; set; } = 3.8f;

    // untracked: MarkDirty() after changing the list. Handles of up to BAR_STRIDE bars, front-packed
    [Glue(nameof(BarHandles))]
    public List<Bar> Bars = [];

    [Glue(nameof(CurrentVolumes))]
    public partial float CurrentVolume { get; }
}

[Ambi(Capacity = 150)]
internal partial class Bar : MonoBehaviour
{
    [Input]
    public static NativeArray<int> Contributions;

    [Glue(nameof(Contributions))]
    public int Contribution { get; set; } = 12;
}
