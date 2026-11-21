using System;

namespace AmbiECS.Glue;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class AmbiSynchronization : Attribute { }

// Can either be class-wide, or property specific
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = false)]
public class SkipAutoDirtyFlagging : Attribute { }

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public class InputBuffer(int strideCapacity = 0) : Attribute
{
    public int StrideCapacity { get; } = strideCapacity;
}

// Exclusive to fields already annotated with InputBuffer
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public class SkipPostLoadSync : Attribute { }

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public class ScratchBuffer(int allocationCapacity) : Attribute
{
    public int AllocationCapacity { get; } = allocationCapacity;
}

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public class ManualExpand : Attribute { }

// unmanaged structs or blittable types only
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public class StatefulBuffer(object defaultValue = null) : Attribute
{
    public object DefaultValue { get; } = defaultValue;
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class GluedProperty(string BufferName) : Attribute
{
    public string BufferName { get; } = BufferName;
}

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public class GluedStride(string BufferName) : Attribute
{
    public string BufferName { get; } = BufferName;
}
