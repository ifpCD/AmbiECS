using Unity.Collections;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

[NoAutoStaticsCleanup]
public abstract class AmbiMonoBehaviour<T> : MonoBehaviour, IAmbiRegistrant
    where T : AmbiMonoBehaviour<T>
{
    AmbiSlot _slot = AmbiSlot.Unassigned;

    public static NativeArray<int> HandleToSoA => AmbiRegistry<T>.HandleToSoA;
    public static NativeArray<int> HandleVersions => AmbiRegistry<T>.HandleVersions;
    public static AmbiLookup Lookup => AmbiRegistry<T>.Lookup;

    // the objects registered right now
    // any request is safe while walking them
    internal static AmbiRegistry<T>.Registrations Registered => default;
    public static int RegisteredCount => AmbiRegistry<T>.RegisteredCount;

    // the rows: what the graph reads
    public static int ActiveCount => AmbiRegistry<T>.ActiveCount;
    public static bool CompletelyInactive => AmbiRegistry<T>.ActiveCount == 0;

    // Names this object until it is destroyed through any number of Deregister/Register cycles. Null afterwards.
    public AmbiHandle Handle => AmbiRegistry<T>.HandleOf((T)this);
    public bool IsRegistered => _slot.Registered >= 0;
    internal int SoAIndex => _slot.SoAIndex;

    protected abstract int AllocatedCapacity { get; }
    protected virtual bool RetainNativeWhenEmpty => false;

    protected abstract void AllocateNative();

    protected abstract void DeallocateNative();

    protected abstract void SwapBack(int removedIndex, int lastIndex);

    protected virtual void LoadObjectToArrays() { }

    protected virtual void SyncObjectToArrays() { }

    protected static void DisposeRegistry() => AmbiRegistry<T>.Dispose();

    internal void Register() => AmbiRegistry<T>.Register((T)this);

    internal void Deregister() => AmbiRegistry<T>.Deregister((T)this);

    internal void MarkDirty() => AmbiRegistry<T>.MarkDirty((T)this);

    // retires this object's handle - references to it resolve to nothing from then on
    protected virtual void OnDestroy() => AmbiRegistry<T>.Destroy((T)this);

    ref AmbiSlot IAmbiRegistrant.Slot => ref _slot;

    int IAmbiRegistrant.Capacity => AllocatedCapacity;

    bool IAmbiRegistrant.RetainsNative => RetainNativeWhenEmpty;

    void IAmbiRegistrant.Allocate() => AllocateNative();

    void IAmbiRegistrant.Deallocate() => DeallocateNative();

    void IAmbiRegistrant.SwapBack(int removedIndex, int lastIndex) => SwapBack(removedIndex, lastIndex);

    void IAmbiRegistrant.Load() => LoadObjectToArrays();

    void IAmbiRegistrant.Sync() => SyncObjectToArrays();
}
