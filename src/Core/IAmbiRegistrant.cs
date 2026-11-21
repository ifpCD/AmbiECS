using System.ComponentModel;

namespace AmbiECS;

// The contract between Ambi<T> and one Ambi type. [Ambi] types get it from the generator.
//
// Type level members describe the columns, which are static, so no instance is needed to reach them.
// Row level members run on the object that owns the row, only at the flush, while the graph is idle: they write that row and
// nothing else, and issue no requests.
public interface IAmbiRegistrant<TSelf>
    where TSelf : class, IAmbiRegistrant<TSelf>
{
    static abstract int InitialCapacity { get; }

    // false: exceeding InitialCapacity throws instead of growing
    static virtual bool CanGrow => true;

    // false: the columns are freed whenever a flush leaves the type empty
    static virtual bool RetainColumnsWhenEmpty => true;

    static abstract void AllocateColumns(int capacity);

    // rows [0, rowCount) are live and survive the resize
    static abstract void ResizeColumns(int rowCount, int capacity);

    static abstract void DeallocateColumns();

    void LoadRow(int row);

    void SyncRow(int row);

    // Called on the departing object, before its slot lets go of the row: lastRow's contents move into row, unless they are the same.
    void RemoveRow(int row, int lastRow);

    [EditorBrowsable(EditorBrowsableState.Never)]
    ref AmbiSlot Slot { get; }
}
