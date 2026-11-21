using System.ComponentModel;

namespace AmbiECS;

// Where one Ambi object stands. Lives in the object (a generated field); the registry reaches it by reference.
// Opaque outside Core: generated code only creates it and reads Row and IsRegistered.
[EditorBrowsable(EditorBrowsableState.Never)]
public struct AmbiSlot
{
    internal const int NONE = -1;

    internal const int DIRTY = 1; // its row must be synced from the object
    internal const int RELOAD = 2; // registered again before its old row was removed: the row starts over
    internal const int QUEUED = 4; // waiting for the next flush
    internal const int DESTROYED = 8; // gone for good: its identity retires when it lands

    internal int Registered; // place among the registered objects, NONE while unregistered: the one truth behind IsRegistered
    internal int SoAIndex; // row, NONE while it has none
    internal int Identity; // identity slot, NONE until its Handle is first read
    internal int Visit; // the last walk over the registered objects that yielded it
    internal int Flags;

    public static AmbiSlot Unassigned =>
        new()
        {
            Registered = NONE,
            SoAIndex = NONE,
            Identity = NONE,
        };

    public readonly bool IsRegistered => Registered >= 0;

    // the row the graph last ran on, NONE while it has none
    public readonly int Row => SoAIndex;
}
