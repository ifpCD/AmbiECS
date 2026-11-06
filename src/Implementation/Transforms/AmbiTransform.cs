using Unity.Scripting.LifecycleManagement;
using UnityEngine;

// instead of doing TransformAccessArray on each instance of an arbitrary Ambi component
// we instead have a reference counted shared transform access class called AmbiTransform
// essentially - whenever a MonoBehaviour component needs to know its transform data
// it asks AmbiTransforms for an instance of AmbiTransform by passing its Entity ID (component's EntityID)
// if another component has already asked for an AmbiTransform, it simply gets an existing AmbiTransform reference
//
// during Burst Parallel Jobs, components requiring its position/rotation/matrix data do an indirection call using their AmbiHandle for AmbiTransform
// otherwise, they can schedule a separate parallel job that deals with the indirection
//
// additionally, if a component want its transform to track another transform
// instead of doing so on the main thread - they instead pass a transform reference they want their AmbiTransform to track, and it does so in the job graph
//
// the ultimate goal is to both reduce as much main thread interop for transform operations, and also have as little TransformAccessArray additions/removals as they cost quite a bit too.
[NoAutoStaticsCleanup]
public sealed partial class AmbiTransform : AmbiBase<AmbiTransform>
{
    internal static int DrivenCount;

    internal readonly Transform Target;
    internal readonly EntityId Key;
    internal int Holders;

    AmbiTransform _leader;
    int _drivenIndex = AmbiSlot.NONE;

    internal AmbiTransform(Transform target, EntityId key)
    {
        Target = target;
        Key = key;
    }

    public AmbiTransform Leader
    {
        get => _leader;
        set
        {
            if (_leader == value)
                return;

            _leader = value;
            MarkDirty();
        }
    }

    void Drive()
    {
        _drivenIndex = DrivenCount;
        _driven[DrivenCount] = this;
        DrivenRows[DrivenCount++] = Handle;
        DrivenTransforms.Add(Target);
    }

    void Undrive()
    {
        int index = _drivenIndex;
        AmbiTransform last = _driven[--DrivenCount];

        _driven[index] = last;
        last._drivenIndex = index;
        DrivenRows[index] = DrivenRows[DrivenCount];
        _driven[DrivenCount] = null;
        DrivenTransforms.RemoveAtSwapBack(index);

        _drivenIndex = AmbiSlot.NONE;
    }
}
