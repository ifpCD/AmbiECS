# AmbiECS

AmbiECS is a hybrid ECS for Unity 7 (C# 14) that bridges MonoBehaviours with Burst-compiled job graphs.  

Annotated classes and properties are tracked by a source generator that packs its data into the declared persistent SoA storage, tracking property changes, and managing generational handles.  
The scheduler manages data synchronization and provides a subsystem pattern to deal with custom flushing behaviour, arbitrary update job graph scheduling, and OOP result retrieval.  
Unmanaged generational handles allow for inter-class cross-referencing in Burst-domain, automatically derived from main thread managed class references.

## Thesis
OOP MonoBehaviour-centric development is ergonomic and has a rich ecosystem. ECS is performant, but is oftentimes an architectural overkill for projects.  
A proposal is to bridge the underlying performance boosters (Unity Jobs and Burst), whilst adhering to the MonoBehaviour pattern, allowing for targetted incremental upgrades in existing systems.  
All lifecycle remains on the main thread, with hot update loops being rewritten using a centralized subsystem implementation.  
The tradeoff is inherent buffer declaration and synchronization boilerplate, which is minimized by source generation, yet remains explicit.  

## Terminology
- `Column` is a densely packed array annotated either with `[Input]`, `[State]`, or `[Scratch]`.
- `Row` is an index for a registed instance within a class-wide collections of `Column`s for element access inside jobs (may change during each flush).
- `Flush` is the OOP to DOD row sorting and data synchronization stage in the frame that happens after PlayerLoop.Update, before IAmbiSystem job scheduling.
- `Buffer` any arbitrary, persistently allocated array whose lifecycle is managed by AmbiECS.

## Annotations
### Classes
- `Ambi` makes a `partial` class, a component or a plain object, an Ambi type through source generation.
### Buffers
- `Input` is a `Column` for OOP side authoring values and read-only access in the Job Graph. A strided `Input` holds up to `Stride` handles per row, from a list or array of an Ambi type.
- `Scratch` is effectively a persistently allocated scratchpad, sized either by the class capacity (zeroed on growth), or a fixed length (left uninitialized).
- `State` is a `Column` without an OOP value, that persists across the entirety of an instance's registration through swap backs.
### Properties
- `Glue` is how you point an OOP property to an `Input Column`. If it's a property getter that's reading a `Track` property, it automatically synchronizes on change.
- `Track` is used if a piece of data is a derivative to the value you want to use in the Job Graph without doing it in DOD.
- `Readback` is a computed getter that lets you introspect into the Job Graph results on the main thread, at the cost of waiting for a job completion. A strided `State` reads back as a `ReadOnlySpan<T>`.

## Classes

- `Ambi<T>` keeps each type's rows tightly packed as objects come and go, and hands out their handles.
- `AmbiScheduler` runs the frame: finishes last frame's jobs, applies changes and schedules systems.
- `AmbiHandle` and `AmbiLookup` provide stable references between objects that jobs can resolve with a single read.
- `AmbiTransform` gives shared, parallel access to transforms.
- `IAmbiSystem` is the interface to implement in order to schedule jobs each frame, with optional callbacks before the flush, after it, and after scheduling.

## Caveats

a `Readback` `ReadOnlySpan<T>` reference is only valid until the next flush.  
The scheduler subsystem architecture, including inter-dependency chaining is not fully figured out yet.  
Same frame OOP readback can only occur in `IAmbiSystem.PostScheduleUpdate` or `LateUpdate`

## Implementation

```cs
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
```

## Source Generation Example (Partially omitted)

```cs
partial class Foo : AmbiECS.IAmbiRegistrant<Foo>
{
    static void AmbiECS.IAmbiRegistrant<Foo>.AllocateColumns(int capacity)
    {
        TargetVolumes = new(capacity, Unity.Collections.Allocator.Persistent, Unity.Collections.NativeArrayOptions.UninitializedMemory);
        ConstantFactors = new float[capacity];
        BarHandles = new(capacity * 15, Unity.Collections.Allocator.Persistent, Unity.Collections.NativeArrayOptions.UninitializedMemory);
        CurrentVolumes = new(capacity, Unity.Collections.Allocator.Persistent, Unity.Collections.NativeArrayOptions.UninitializedMemory);
        VolumeHistories = new(capacity * 4, Unity.Collections.Allocator.Persistent, Unity.Collections.NativeArrayOptions.UninitializedMemory);
        ArbitraryScratch = new(2048, Unity.Collections.Allocator.Persistent, Unity.Collections.NativeArrayOptions.UninitializedMemory);
        OnColumnsAllocated(capacity);
    }

    static void AmbiECS.IAmbiRegistrant<Foo>.ResizeColumns(int rowCount, int capacity)
    {
        AmbiECS.AmbiColumn.Resize(ref TargetVolumes, capacity, rowCount);
        System.Array.Resize(ref ConstantFactors, capacity);
        AmbiECS.AmbiColumn.Resize(ref BarHandles, capacity * 15, rowCount * 15);
        AmbiECS.AmbiColumn.Resize(ref CurrentVolumes, capacity, rowCount);
        AmbiECS.AmbiColumn.Resize(ref VolumeHistories, capacity * 4, rowCount * 4);
        OnColumnsResized(rowCount, capacity);
    }

    static void AmbiECS.IAmbiRegistrant<Foo>.DeallocateColumns()
    {
        OnColumnsDeallocating();
        TargetVolumes.Dispose();
        ConstantFactors = null;
        BarHandles.Dispose();
        CurrentVolumes.Dispose();
        VolumeHistories.Dispose();
        ArbitraryScratch.Dispose();
    }

    void AmbiECS.IAmbiRegistrant<Foo>.LoadRow(int row)
    {
        AmbiECS.AmbiColumn.At(CurrentVolumes, row) = (float)(1);
        for (int i = row * 4, end = i + 4; i < end; i++) AmbiECS.AmbiColumn.At(VolumeHistories, i) = default;
        AmbiECS.AmbiColumn.At(TargetVolumes, row) = TargetVolume;
        ConstantFactors[row] = ConstantFactor;
        AmbiECS.AmbiColumn.WriteHandles<Bar>(BarHandles, row, 15, CollectionsMarshal.AsSpan(Bars));
        _ambiPending = 0;
        OnRowLoaded(row);
    }

    void AmbiECS.IAmbiRegistrant<Foo>.SyncRow(int row, bool dirty)
    {
        ulong pending = _ambiPending;
        _ambiPending = 0;

        if (dirty)
        {
            AmbiECS.AmbiColumn.WriteHandles<Bar>(BarHandles, row, 15, CollectionsMarshal.AsSpan(Bars));
        }

        if (dirty || (pending & 0x1UL) != 0)
            AmbiECS.AmbiColumn.At(TargetVolumes, row) = TargetVolume;
        if ((pending & 0x2UL) != 0)
            ConstantFactors[row] = ConstantFactor;
        OnRowSynced(row);
    }

    void AmbiECS.IAmbiRegistrant<Foo>.RemoveRow(int row, int lastRow)
    {
        OnRowRemoved(row, lastRow);
        if (row != lastRow)
        {
            AmbiECS.AmbiColumn.At(TargetVolumes, row) = AmbiECS.AmbiColumn.At(TargetVolumes, lastRow);
            ConstantFactors[row] = ConstantFactors[lastRow];
            AmbiECS.AmbiColumn.MoveBlock(BarHandles, lastRow, row, 15);
            AmbiECS.AmbiColumn.At(CurrentVolumes, row) = AmbiECS.AmbiColumn.At(CurrentVolumes, lastRow);
            AmbiECS.AmbiColumn.MoveBlock(VolumeHistories, lastRow, row, 4);
        }
    }

    public partial float Volume
    {
        get;
        set
        {
            if (EqualityComparer<float>.Default.Equals(field, value))
                return;

            field = value;

            _ambiPending |= 0x1UL;
            AmbiECS.Ambi<Foo>.MarkPending(this, ref _ambiSlot);
        }
    }

    public partial float ConstantFactor
    {
        get;
        set
        {
            if (EqualityComparer<float>.Default.Equals(field, value))
                return;

            field = value;

            _ambiPending |= 0x2UL;
            AmbiECS.Ambi<Foo>.MarkPending(this, ref _ambiSlot);
        }
    }

    public partial float CurrentVolume
    {
        get
        {
            PromisedCompletionHandle.Complete();
            int row = _ambiSlot.Row;
            return row < 0 ? default : AmbiECS.AmbiColumn.At(CurrentVolumes, row);
        }
    }

    public partial ReadOnlySpan<float> VolumeHistory
    {
        get
        {
            PromisedCompletionHandle.Complete();
            int row = _ambiSlot.Row;
            return row < 0 ? default : AmbiECS.AmbiColumn.Block(VolumeHistories, row, 4);
        }
    }

    void OnEnable() => AmbiRegister();

    void OnDisable() => AmbiDeregister();

    void OnDestroy() => AmbiDestroy();

    void OnValidate() => AmbiOnValidate();

    static partial void OnColumnsAllocated(int capacity);

    static partial void OnColumnsResized(int rowCount, int capacity);

    static partial void OnColumnsDeallocating();

    partial void OnRowLoaded(int row);

    partial void OnRowSynced(int row);

    partial void OnRowRemoved(int row, int lastRow);
}
```