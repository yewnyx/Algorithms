using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Yewnyx.Spatial;

/// <summary>
/// A few hundred boxes, most resting and some drifting, and one query each frame: the boxes within
/// a radius of this object. Move the object about in the scene view, with gizmos on, to watch it.
/// </summary>
/// <remarks>
/// Each frame is three jobs in a row, and this thread touches no box: one moves the drifting boxes,
/// the set takes their new poses from that job's arrays, and the query runs after the set is up to
/// date. The answer shown is the one asked for a frame earlier, picked up once its jobs were done.
/// Resting boxes are blue and the ones worked out again this frame red (drawn by a
/// <see cref="BoxSetGizmo"/> on the same object); the boxes the query found are filled in green.
/// </remarks>
public sealed class MovingBoxes : MonoBehaviour {
    [Min(1)] public int count = 300;
    [Range(0, 1), Tooltip("The share of the boxes that drift. The rest never move.")]
    public float moving = 0.1f;
    [Min(0)] public float radius = 6;
    [Tooltip("Walk the tree, or test every box.")]
    public bool tree = true;
    public Vector3 size = new Vector3(60, 20, 60);
    public Color found = new Color(0.3f, 1f, 0.4f, 0.5f);

    private BoxSet _set;
    private float3 _low, _high;
    // The drifting boxes: their ids, and the poses the set reads each frame.
    private NativeArray<int> _ids;
    private NativeArray<float3> _positions, _velocities;
    private NativeArray<quaternion> _rotations;
    private NativeArray<float4> _sphere;
    private NativeArray<int> _results, _counts, _totals;
    private bool[] _wasFound;
    private JobHandle _query;

    /// <summary>How many boxes the last finished query found.</summary>
    public int Found { get; private set; }

    /// <summary>The set, for a test to look at.</summary>
    public BoxSet Set => _set;

    private void OnEnable() {
        var half = (float3)size * 0.5f;
        _low = (float3)transform.position - half;
        _high = (float3)transform.position + half;
        _set = new BoxSet(count, new Box(_low.x, _low.y, _low.z, _high.x, _high.y, _high.z));
        var drifting = Mathf.RoundToInt(moving * count);
        _ids = new NativeArray<int>(drifting, Allocator.Persistent);
        _positions = new NativeArray<float3>(drifting, Allocator.Persistent);
        _velocities = new NativeArray<float3>(drifting, Allocator.Persistent);
        _rotations = new NativeArray<quaternion>(drifting, Allocator.Persistent);
        _sphere = new NativeArray<float4>(1, Allocator.Persistent);
        _results = new NativeArray<int>(count, Allocator.Persistent);
        _counts = new NativeArray<int>(1, Allocator.Persistent);
        _totals = new NativeArray<int>(1, Allocator.Persistent);
        _wasFound = new bool[count];

        var random = new Unity.Mathematics.Random(12345);
        for (var id = 0; id < count; id++) {
            var position = random.NextFloat3(_low, _high);
            var rotation = random.NextQuaternionRotation();
            _set.Add(id, float3.zero, random.NextFloat3(0.2f, 1.2f), position, rotation);
            // The first share of the boxes drift. The rest are never spoken of again.
            if (id >= drifting) { continue; }
            _ids[id] = id;
            _positions[id] = position;
            _rotations[id] = rotation;
            _velocities[id] = random.NextFloat3Direction() * random.NextFloat(0.5f, 4f);
        }
        var gizmo = GetComponent<BoxSetGizmo>();
        if (gizmo != null) { gizmo.Set = _set; }
    }

    private void Update() => Step(Time.deltaTime);

    /// <summary>One frame: pick up the last answer, then schedule the drift, the set and the next query.</summary>
    public void Step(float seconds) {
        // The answer asked for last frame. Its ids are the boxes' own, whatever has changed since.
        _query.Complete();
        for (var id = 0; id < count; id++) { _wasFound[id] = false; }
        Found = math.max(_counts[0], 0);
        for (var i = 0; i < Found; i++) { _wasFound[_results[i]] = true; }

        var drift = new DriftJob { Positions = _positions, Velocities = _velocities, Low = _low, High = _high, Seconds = seconds }
            .Schedule(_positions.Length, 64);
        // The set reads the drifting boxes' poses after the drift, and leaves the other boxes alone.
        _set.Schedule(_ids, _positions, _rotations, drift);
        _sphere[0] = new float4(transform.position, radius);
        _query = _set.ScheduleInSphere(_sphere, _results, _counts, _totals, count, tree);
    }

    private void OnDisable() {
        _query.Complete();
        var gizmo = GetComponent<BoxSetGizmo>();
        if (gizmo != null) { gizmo.Set = null; }
        _set.Dispose();
        _ids.Dispose(); _positions.Dispose(); _velocities.Dispose(); _rotations.Dispose();
        _sphere.Dispose(); _results.Dispose(); _counts.Dispose(); _totals.Dispose();
        _set = null;
    }

    private void OnDrawGizmos() {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, radius);
        if (_set == null) { return; }
        var boxes = _set.Boxes;
        var ids = _set.Ids;
        Gizmos.color = found;
        for (var i = 0; i < boxes.Length; i++) {
            if (!_wasFound[ids[i]]) { continue; }
            var box = boxes[i];
            Gizmos.DrawCube(new Vector3((box.MinX + box.MaxX) * 0.5f, (box.MinY + box.MaxY) * 0.5f, (box.MinZ + box.MaxZ) * 0.5f),
                new Vector3(box.MaxX - box.MinX, box.MaxY - box.MinY, box.MaxZ - box.MinZ));
        }
    }

    // Moves each drifting box along, and turns it back at the walls.
    [BurstCompile]
    private struct DriftJob : IJobParallelFor {
        public NativeArray<float3> Positions, Velocities;
        public float3 Low, High;
        public float Seconds;

        public void Execute(int index) {
            var position = Positions[index] + Velocities[index] * Seconds;
            var outside = (position < Low) | (position > High);
            Velocities[index] = math.select(Velocities[index], -Velocities[index], outside);
            Positions[index] = math.clamp(position, Low, High);
        }
    }
}
