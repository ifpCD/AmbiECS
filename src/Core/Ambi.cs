using System;
using Unity.Collections;
using Unity.Scripting.LifecycleManagement;

namespace AmbiECS;

// The dense SoA registry of one Ambi type, which the generator implements IAmbiRegistrant<T> for.
//
// Two views of the same objects, each for one side:
// Registered: the objects registered right now, for main-thread code. Requests change it at once, so it never holds an object
// that has left or been destroyed.
// Rows: the objects the graph reads, packed in [0, RowCount), removed by swap-back. Each request queues its object once, and
// rows change only when AmbiScheduler flushes the queues. LoadRow fills a new row and SyncRow rewrites a row from its object;
// both run at the flush, write that row and nothing else, and issue no requests.
//
// Capacity is where a type starts. The managed views grow at once; the columns, and the native mirrors of the identity table,
// grow only while the graph is idle, so growing never waits on a job. Types that can't grow throw at their caller instead.
// Columns are freed when a flush leaves a type empty, unless the type retains them.
//
// Identities: an AmbiHandle names an object from the first read of its Handle until it is destroyed, whatever its registration
// in between. HandleToSoA[identity] is its row or -1, and HandleVersions retires the handle on destruction. Versions start at 1
// (0 is Null) and outlive their slot, so a recycled slot never reissues a handle someone still holds. The identity table lives
// until code unload.
//
// Nothing is allocated per request, only when a view grows past its capacity.
[NoAutoStaticsCleanup]
public static class Ambi<T>
    where T : class, IAmbiRegistrant<T>
{
    static T[] _registered = [];
    static int _registeredCount;
    static int _visits; // stamps the walks over Registered

    static T[] _rows; // null while the columns are deallocated
    static int _rowCount;
    static int _capacity; // of the rows and columns, kept while they are deallocated so they come back at the same size

    static NativeArray<int> _handleToSoA;
    static NativeArray<int> _handleVersions;
    static int[] _versions = []; // authoritative: the native mirrors catch up while the graph is idle
    static int[] _freeIdentities = [];
    static int _freeIdentityCount;
    static int _nextIdentity;

    static T[] _queue = [];
    static int _queueCount;

    static Ambi() => AmbiScheduler.Enlist(Depart, Arrive, Dispose);

    public static int RegisteredCount => _registeredCount;

    public static int RowCount => _rowCount;

    public static bool IsEmpty => _rowCount == 0;

    public static AmbiLookup Lookup => new() { HandleToSoA = _handleToSoA, HandleVersions = _handleVersions };

    // the objects registered right now; any request is safe while walking them
    public static Registrations Registered => default;

    public static void Register(T instance)
    {
        ref AmbiSlot slot = ref instance.Slot;

        if (slot.Registered >= 0 || (slot.Flags & AmbiSlot.DESTROYED) != 0)
            return;

        if (_registeredCount == _registered.Length)
            Array.Resize(ref _registered, NextCapacity(_registered.Length));

        slot.Registered = _registeredCount;
        _registered[_registeredCount++] = instance;

        if (slot.SoAIndex >= 0)
            slot.Flags |= AmbiSlot.RELOAD;

        Settle(instance, ref slot);
    }

    public static void Deregister(T instance)
    {
        ref AmbiSlot slot = ref instance.Slot;

        if (slot.Registered < 0)
            return;

        Unregister(ref slot);
        Settle(instance, ref slot);
    }

    public static void MarkDirty(T instance)
    {
        ref AmbiSlot slot = ref instance.Slot;

        // a row is synced in full when it is created
        if (slot.Registered < 0)
            return;

        slot.Flags |= AmbiSlot.DIRTY;
        Settle(instance, ref slot);
    }

    // Gone for good: OnDestroy for components, Release for plain objects. Its handle stops resolving at the next flush.
    public static void Destroy(T instance)
    {
        ref AmbiSlot slot = ref instance.Slot;

        if ((slot.Flags & AmbiSlot.DESTROYED) != 0)
            return;

        Unregister(ref slot);
        slot.Flags |= AmbiSlot.DESTROYED;

        // only a row or a handle needs undoing
        if (slot.SoAIndex >= 0 || slot.Identity >= 0)
            Settle(instance, ref slot);
    }

    // Null for no object or a destroyed one.
    public static AmbiHandle HandleOf(T instance)
    {
        if (instance is null)
            return AmbiHandle.Null;

        ref AmbiSlot slot = ref instance.Slot;

        if ((slot.Flags & AmbiSlot.DESTROYED) != 0)
            return AmbiHandle.Null;

        if (slot.Identity == AmbiSlot.NONE)
            AcquireIdentity(instance, ref slot);

        return new AmbiHandle { Index = slot.Identity, Version = _versions[slot.Identity] };
    }

    // The next length for a full view: the declared capacity first, then double. Types that can't grow throw instead.
    static int NextCapacity(int length)
    {
        if (length == 0)
            return Math.Max(1, T.InitialCapacity);

        if (!T.CanGrow)
            throw new InvalidOperationException($"{typeof(T).Name} capacity of {length} exceeded.");

        return 2 * length;
    }

    // Code unload, once the graph is done: every object leaves, those still waiting to arrive included, and the identity table goes
    // with the rows whatever handles are still out, since no code is left to resolve them.
    static void Dispose()
    {
        while (_registeredCount != 0)
            Unregister(ref _registered[_registeredCount - 1].Slot);

        Drain();

        while (_rowCount != 0)
        {
            T instance = _rows[_rowCount - 1];
            Apply(instance, ref instance.Slot);
        }

        if (_rows != null)
            DeallocateRows();

        if (_handleToSoA.IsCreated)
        {
            _handleToSoA.Dispose();
            _handleVersions.Dispose();
        }
    }

    // Out of Registered at once, by swap-back.
    static void Unregister(ref AmbiSlot slot)
    {
        int index = slot.Registered;

        if (index < 0)
            return;

        T last = _registered[--_registeredCount];
        _registered[index] = last;
        last.Slot.Registered = index;
        _registered[_registeredCount] = null;
        slot.Registered = AmbiSlot.NONE;
    }

    static void Settle(T instance, ref AmbiSlot slot)
    {
        if (!AmbiScheduler.Defers)
        {
            Apply(instance, ref slot);
            ReleaseRowsIfEmpty();
            return;
        }

        if ((slot.Flags & AmbiSlot.QUEUED) != 0)
            return;

        // the queue is managed: it grows at once, whatever the graph is doing
        if (_queueCount == _queue.Length)
            Array.Resize(ref _queue, Math.Max(2 * T.InitialCapacity, 2 * _queue.Length));

        slot.Flags |= AmbiSlot.QUEUED;
        _queue[_queueCount++] = instance;
    }

    // This type alone, outside a flush: teardown.
    static void Drain()
    {
        Depart();
        Arrive();
    }

    // Rows that go and identities that retire, before anything arrives: the identity slots are free again for the handles the
    // arrivals' syncs read.
    static void Depart()
    {
        MirrorIdentities();

        for (int i = 0; i < _queueCount; i++)
        {
            T instance = _queue[i];
            ref AmbiSlot slot = ref instance.Slot;

            if (slot.SoAIndex >= 0 && Leaves(ref slot))
                RemoveRow(instance, ref slot);

            if ((slot.Flags & AmbiSlot.DESTROYED) != 0)
                RetireIdentity(ref slot);
        }
    }

    // Everything else, from the back, so that if a LoadRow or SyncRow throws, whatever was not applied stays queued for the next flush.
    static void Arrive()
    {
        while (_queueCount > 0)
        {
            T instance = _queue[--_queueCount];
            _queue[_queueCount] = null;

            ref AmbiSlot slot = ref instance.Slot;
            slot.Flags &= ~AmbiSlot.QUEUED;
            Apply(instance, ref slot);
        }

        ReleaseRowsIfEmpty();
    }

    static void Apply(T instance, ref AmbiSlot slot)
    {
        MirrorIdentities();

        if (slot.SoAIndex >= 0 && Leaves(ref slot))
            RemoveRow(instance, ref slot);

        if (slot.Registered >= 0)
        {
            if (slot.SoAIndex < 0)
                AddRow(instance, ref slot);
            else if ((slot.Flags & AmbiSlot.DIRTY) != 0)
                instance.SyncRow(slot.SoAIndex);
        }

        slot.Flags &= ~(AmbiSlot.DIRTY | AmbiSlot.RELOAD);

        if (slot.Identity >= 0)
            _handleToSoA[slot.Identity] = slot.SoAIndex;

        if ((slot.Flags & AmbiSlot.DESTROYED) != 0)
            RetireIdentity(ref slot);
    }

    // Its row goes when it is no longer registered, or registered again after leaving, so that the row starts over.
    static bool Leaves(ref AmbiSlot slot) => slot.Registered < 0 || (slot.Flags & AmbiSlot.RELOAD) != 0;

    static void AddRow(T instance, ref AmbiSlot slot)
    {
        if (_rows == null)
            AllocateRows();
        else if (_rowCount == _rows.Length)
            GrowRows();

        int row = _rowCount++;
        slot.SoAIndex = row;
        _rows[row] = instance;

        instance.LoadRow(row);
        instance.SyncRow(row);
    }

    static void RemoveRow(T instance, ref AmbiSlot slot)
    {
        int row = slot.SoAIndex;
        int lastRow = --_rowCount;

        if (row != lastRow)
        {
            T moved = _rows[lastRow];
            ref AmbiSlot movedSlot = ref moved.Slot;
            _rows[row] = moved;
            movedSlot.SoAIndex = row;

            if (movedSlot.Identity >= 0)
                _handleToSoA[movedSlot.Identity] = row;
        }

        instance.RemoveRow(row, lastRow);

        _rows[lastRow] = null;
        slot.SoAIndex = AmbiSlot.NONE;
    }

    // The identity table covers at least every row, so that jobs handed Lookup always find it, whether or not a handle was read.
    static void AllocateRows()
    {
        if (_capacity == 0)
            _capacity = NextCapacity(0);

        _rows = new T[_capacity];
        T.AllocateColumns(_capacity);
        ReserveIdentities(_capacity);
        MirrorIdentities();
    }

    static void GrowRows()
    {
        int capacity = NextCapacity(_capacity);

        Array.Resize(ref _rows, capacity);
        T.ResizeColumns(_rowCount, capacity);
        _capacity = capacity;

        ReserveIdentities(capacity);
        MirrorIdentities();
    }

    static void DeallocateRows()
    {
        T.DeallocateColumns();
        _rows = null;
    }

    // Once per flush rather than per removal: a type emptied and refilled in one flush keeps its columns.
    static void ReleaseRowsIfEmpty()
    {
        if (_rowCount == 0 && _rows != null && !T.RetainColumnsWhenEmpty)
            DeallocateRows();
    }

    static void AcquireIdentity(T instance, ref AmbiSlot slot)
    {
        if (_freeIdentityCount > 0)
            slot.Identity = _freeIdentities[--_freeIdentityCount];
        else
        {
            if (_nextIdentity == _versions.Length)
                ReserveIdentities(NextCapacity(_versions.Length));

            slot.Identity = _nextIdentity++;
        }

        // An identity without a row maps to -1 already, so only an object with a row has an entry to write. While the graph is idle,
        // as it is whenever a SyncRow reads a Handle, it is written at once, so a reference synced in a flush resolves in the graph
        // that follows. Otherwise the next flush writes it.
        if (slot.SoAIndex < 0)
            return;

        if (AmbiScheduler.IsIdle)
        {
            MirrorIdentities();
            _handleToSoA[slot.Identity] = slot.SoAIndex;
        }
        else
            Settle(instance, ref slot);
    }

    // Native mirrors are written while the graph is idle only, and are up to date then.
    static void RetireIdentity(ref AmbiSlot slot)
    {
        int identity = slot.Identity;

        if (identity < 0)
            return;

        slot.Identity = AmbiSlot.NONE;

        int version = _versions[identity] + 1;
        _versions[identity] = version == 0 ? 1 : version; // 0 is Null

        _handleVersions[identity] = _versions[identity];
        _handleToSoA[identity] = AmbiSlot.NONE;
        _freeIdentities[_freeIdentityCount++] = identity;
    }

    // Managed only, so at any time: the native mirrors follow at the next idle point.
    static void ReserveIdentities(int capacity)
    {
        int length = _versions.Length;

        if (capacity <= length)
            return;

        Array.Resize(ref _versions, capacity);
        Array.Resize(ref _freeIdentities, capacity);
        Array.Fill(_versions, 1, length, capacity - length); // versions start at 1: 0 is Null
    }

    // Brings the native mirrors up to every identity reserved. Only while the graph is idle.
    static void MirrorIdentities()
    {
        int length = _versions.Length;
        int mirrored = _handleToSoA.IsCreated ? _handleToSoA.Length : 0;

        if (mirrored >= length)
            return;

        _handleToSoA.Resize(length);
        _handleVersions.Resize(length);

        for (int i = mirrored; i < length; i++)
            _handleToSoA[i] = AmbiSlot.NONE;

        NativeArray<int>.Copy(_versions, mirrored, _handleVersions, mirrored, length - mirrored);
    }

    // Mutation-tolerant, swap-back-aware walk over the registered objects, for main-thread code such as culling in
    // IAmbiSystem.BeforeFlush, or anywhere else after AmbiScheduler.Complete(). Any request is safe while walking: each object
    // registered throughout is yielded exactly once, one that leaves first is never yielded, and one that arrives meanwhile may be.
    public readonly struct Registrations
    {
        public Enumerator GetEnumerator() => new(++_visits);

        public struct Enumerator
        {
            readonly int _visit;
            int _index;
            T _current;

            internal Enumerator(int visit)
            {
                _visit = visit;
                _index = _registeredCount;
                _current = null;
            }

            public readonly T Current => _current;

            // Backwards: an object that leaves is replaced by the last one, which is already behind us. Only when the one leaving is
            // still ahead does the last one move ahead again, and its stamp skips it.
            public bool MoveNext()
            {
                for (_index = Math.Min(_index, _registeredCount) - 1; _index >= 0; _index--)
                {
                    T instance = _registered[_index];
                    ref AmbiSlot slot = ref instance.Slot;

                    if (slot.Visit == _visit)
                        continue;

                    slot.Visit = _visit;
                    _current = instance;
                    return true;
                }

                return false;
            }
        }
    }
}
