using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace Ambi;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class AmbiGenerated(bool AutoDirtyFlagging = true) : Attribute
{
    public bool AutoDirtyFlagging { get; } = AutoDirtyFlagging;
}

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public class InputBuffer(int StrideCapacity = 0) : Attribute
{
    public int StrideCapacity { get; } = StrideCapacity;
}

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public class SkipPostLoadSync : Attribute { }

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public class ScratchBuffer(int AllocationCapacity) : Attribute
{
    public int AllocationCapacity { get; } = AllocationCapacity;
}

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public class StatefulBuffer() : Attribute { }

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
