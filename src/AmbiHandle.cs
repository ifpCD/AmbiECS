using System;

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
