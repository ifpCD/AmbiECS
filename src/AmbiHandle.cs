using System;

// Generational name of one Ambi object, valid from the first read of its Handle until it is destroyed, whether or not it is
// registered in between. Plain data: safe to store in NativeArrays and to resolve in Burst jobs through AmbiLookup.
//
// default(AmbiHandle) is Null. Registries issue versions starting at 1, so zeroed native memory never names an object.
public struct AmbiHandle : IEquatable<AmbiHandle>
{
    public int Index;
    public int Version;

    public static readonly AmbiHandle Null = default;

    public readonly bool IsNull => Version == 0;

    public readonly bool Equals(AmbiHandle other) => Index == other.Index && Version == other.Version;

    public override readonly bool Equals(object obj) => obj is AmbiHandle other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Index, Version);

    public override readonly string ToString() => IsNull ? "AmbiHandle(Null)" : $"AmbiHandle({Index}:{Version})";

    public static bool operator ==(AmbiHandle left, AmbiHandle right) => left.Equals(right);

    public static bool operator !=(AmbiHandle left, AmbiHandle right) => !left.Equals(right);
}
