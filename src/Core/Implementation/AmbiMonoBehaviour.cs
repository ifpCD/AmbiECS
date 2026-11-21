using Unity.Collections;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

// A MonoBehaviour with a row in the dense SoA registry of T while registered, and a Handle for its whole life (see AmbiRegistry).
//
// Nothing here waits for the job graph. Register, Deregister and MarkDirty take effect for main-thread code at once (IsRegistered,
// Registered) and reach the rows at AmbiScheduler's flush, once per object however often it changed. SoAIndex is the row the graph
// last ran on, -1 while it has none: read results there after AmbiScheduler.Complete.
// A type keeps its columns in LoadObjectToArrays (a new row), SyncObjectToArrays (its values) and SwapBack, and calls MarkDirty
// after changing anything SyncObjectToArrays writes. Those three are the only places a column is written, and they issue no
// requests.
// Note: whatever a row reads that is not serialized (other Ambi objects, handles) has to be acquired in OnEnable and released in OnDisable:
// a code reload in Play mode erases it and calls only those two, never Awake or OnDestroy.
[NoAutoStaticsCleanup]
public abstract class AmbiMonoBehaviour<T> : MonoBehaviour, IAmbiRegistrant
    where T : AmbiMonoBehaviour<T>
{
    AmbiSlot _slot = AmbiSlot.Unassigned;

    public static NativeArray<int> HandleToSoA => AmbiRegistry<T>.HandleToSoA;
    public static NativeArray<int> HandleVersions => AmbiRegistry<T>.HandleVersions;
    public static AmbiLookup Lookup => AmbiRegistry<T>.Lookup;

    // the objects registered right now; any request is safe while walking them
    internal static AmbiRegistry<T>.Registrations Registered => default;
    public static int RegisteredCount => AmbiRegistry<T>.RegisteredCount;

    // the rows: what the graph reads
    public static int RowCount => AmbiRegistry<T>.RowCount;
    public static bool IsEmpty => AmbiRegistry<T>.RowCount == 0;

    // Names this object until it is destroyed, through any number of Deregister/Register cycles. Null afterwards.
    public AmbiHandle Handle => AmbiRegistry<T>.HandleOf((T)this);
    public bool IsRegistered => _slot.Registered >= 0;
    internal int SoAIndex => _slot.SoAIndex;

    protected abstract int PreallocatedCapacity { get; }
    protected virtual bool RetainColumnsWhenEmpty => false;

    protected abstract void AllocateColumns();

    protected abstract void DeallocateColumns();

    protected abstract void SwapBackRow(int removedIndex, int lastIndex);

    protected virtual void LoadObjectToRow() { }

    protected virtual void SyncObjectToRow() { }

    internal void Register() => AmbiRegistry<T>.Register((T)this);

    internal void Deregister() => AmbiRegistry<T>.Deregister((T)this);

    internal void MarkDirty() => AmbiRegistry<T>.MarkDirty((T)this);

    // Override with base.OnDestroy(): retires this object's handle, so references to it resolve to nothing from then on.
    protected virtual void OnDestroy() => AmbiRegistry<T>.Destroy((T)this);

    ref AmbiSlot IAmbiRegistrant.Slot => ref _slot;

    int IAmbiRegistrant.PreallocatedCapacity => PreallocatedCapacity;

    bool IAmbiRegistrant.RetainsColumnsWhenEmpty => RetainColumnsWhenEmpty;

    void IAmbiRegistrant.Allocate() => AllocateColumns();

    void IAmbiRegistrant.Deallocate() => DeallocateColumns();

    void IAmbiRegistrant.SwapBack(int removedIndex, int lastIndex) => SwapBackRow(removedIndex, lastIndex);

    void IAmbiRegistrant.Load() => LoadObjectToRow();

    void IAmbiRegistrant.Sync() => SyncObjectToRow();
}
