using System;
using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Net;

/// <summary>
/// Fixed-capacity ring buffer of authoritative poses indexed by simulation step.
/// The multiplayer server uses it to rewind aircraft to the pose a client saw when
/// it fired, so projectiles are tested against lag-compensated hitboxes.
/// </summary>
public sealed class PoseHistory
{
    private readonly Vec3[] _positions;
    private readonly long[] _indices;

    public PoseHistory(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _positions = new Vec3[capacity];
        _indices = new long[capacity];
        Array.Fill(_indices, long.MinValue);
    }

    public int Capacity => _positions.Length;

    public long NewestIndex { get; private set; } = long.MinValue;

    public long OldestIndex => NewestIndex < 0 ? long.MinValue : System.Math.Max(0, NewestIndex - Capacity + 1);

    public void Add(long index, Vec3 position)
    {
        int slot = Slot(index);
        _positions[slot] = position;
        _indices[slot] = index;
        if (index > NewestIndex)
        {
            NewestIndex = index;
        }
    }

    public bool TryGet(long index, out Vec3 position)
    {
        position = Vec3.Zero;
        if (NewestIndex == long.MinValue || index < OldestIndex || index > NewestIndex)
        {
            return false;
        }

        int slot = Slot(index);
        if (_indices[slot] != index)
        {
            return false;
        }

        position = _positions[slot];
        return true;
    }

    public void Clear()
    {
        Array.Fill(_indices, long.MinValue);
        NewestIndex = long.MinValue;
    }

    private int Slot(long index) => (int)(index % _positions.Length);
}
