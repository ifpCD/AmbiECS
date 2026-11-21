using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace AmbiECS.ReferenceDesign;

// The annotation pattern. Columns are static storage you declare; glue links an object's members to them; the generator writes
// the rest: allocation, growth, swap-back, load and sync, dirty-tracking setters, read-back, and the Unity messages.
//
// [Input]    written from the object: on every sync after it is marked dirty, or only when its row is created (LoadOnly)
// [State]    owned by jobs: set to its default when a row is created, and carried along with its row
// [Scratch]  owned by jobs, not tied to rows: PerRow elements per row of capacity (following growth) or a fixed Length
//
// A settable partial property gets a setter that marks the object dirty; a plain member needs MarkDirty() after a change.
// A get-only partial property reads its row back. Anything not annotated is yours, kept in the optional partial hooks:
// OnColumnsAllocated, OnColumnsResized, OnColumnsDeallocating, OnRowLoaded, OnRowSynced and OnRowRemoved.
[Ambi(Capacity = 10)]
internal partial class Foo : MonoBehaviour
{
    const int BAR_STRIDE = 15;

    [Input]
    public static NativeArray<float> TargetVolumes;

    [Input(LoadOnly = true)]
    public static float[] ConstantFactors;

    [Input(Stride = BAR_STRIDE)]
    public static NativeArray<AmbiHandle> BarHandles;

    [State(Default = 1f)]
    public static NativeArray<float> CurrentVolumes;

    [Scratch(PerRow = 131)]
    public static NativeArray<int> ArbitraryScratch;

    [field: SerializeField]
    [Glue(nameof(TargetVolumes))]
    public partial float Volume { get; set; } = 1f;

    [field: SerializeField]
    [Glue(nameof(ConstantFactors))]
    public float ConstantFactor { get; set; } = 3.8f;

    // handles of up to BAR_STRIDE bars, front-packed
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
