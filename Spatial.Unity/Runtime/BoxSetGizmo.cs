using Unity.Collections;
using UnityEngine;

namespace Yewnyx.Spatial {
    /// <summary>
    /// Draws a <see cref="BoxSet"/> in the scene view as it changes: each item's box, coloured by
    /// whether the last Schedule worked it out again or left it resting, and the tree's nodes.
    /// </summary>
    /// <remarks>
    /// Give it the set from code. Drawing waits for the set's jobs, so it is for looking, not for a
    /// build that must be quick.
    /// </remarks>
    [AddComponentMenu("Spatial/Box Set Gizmo")]
    public sealed class BoxSetGizmo : MonoBehaviour {
        /// <summary>Draw each item's box.</summary>
        public bool drawBoxes = true;
        /// <summary>Draw the tree's nodes.</summary>
        public bool drawTree = true;
        /// <summary>The deepest level of the tree's nodes that is drawn.</summary>
        [Tooltip("Tree nodes deeper than this are not drawn.")]
        [Min(0)] public int deepestNode = 6;
        /// <summary>The colour of a box the last Schedule left resting.</summary>
        public Color resting = new Color(0.25f, 0.6f, 1f, 0.9f);
        /// <summary>The colour of a box the last Schedule worked out again.</summary>
        public Color awake = new Color(1f, 0.35f, 0.35f, 0.95f);
        /// <summary>The colour of the tree's nodes. The deeper a node, the fainter it is drawn.</summary>
        public Color node = new Color(1f, 1f, 1f, 0.35f);

        private bool[] _isAwake = new bool[0];
        private int[] _depth = new int[0];

        /// <summary>The set to draw. Nothing is drawn without one.</summary>
        public BoxSet Set { get; set; }

        private void OnDrawGizmos() {
            if (Set == null || Set.IsDisposed) { return; }
            if (drawTree) { _drawTree(Set.Nodes); }
            if (drawBoxes) { _drawBoxes(Set.Boxes, Set.Awake); }
        }

        private void _drawBoxes(NativeArray<Box>.ReadOnly boxes, NativeArray<int>.ReadOnly awakeBoxes) {
            if (_isAwake.Length < boxes.Length) { _isAwake = new bool[boxes.Length]; }
            for (var i = 0; i < boxes.Length; i++) { _isAwake[i] = false; }
            for (var i = 0; i < awakeBoxes.Length; i++) {
                if ((uint)awakeBoxes[i] < (uint)boxes.Length) { _isAwake[awakeBoxes[i]] = true; }
            }
            for (var i = 0; i < boxes.Length; i++) {
                Gizmos.color = _isAwake[i] ? awake : resting;
                var box = boxes[i];
                _wire(box.MinX, box.MinY, box.MinZ, box.MaxX, box.MaxY, box.MaxZ);
            }
        }

        private void _drawTree(NativeArray<BoxNode>.ReadOnly nodes) {
            // A node's children come after it, so one pass forwards gives every node its depth.
            if (_depth.Length < nodes.Length) { _depth = new int[nodes.Length]; }
            if (nodes.Length > 0) { _depth[0] = 0; }
            for (var i = 0; i < nodes.Length; i++) {
                var treeNode = nodes[i];
                if (treeNode.Count <= 0 && treeNode.First > i && treeNode.First < nodes.Length) {
                    _depth[i + 1] = _depth[i] + 1;
                    _depth[treeNode.First] = _depth[i] + 1;
                }
                if (_depth[i] > deepestNode) { continue; }
                // Deeper nodes are drawn fainter.
                var colour = node;
                colour.a = node.a / (1 + _depth[i]);
                Gizmos.color = colour;
                _wire(treeNode.MinX, treeNode.MinY, treeNode.MinZ, treeNode.MaxX, treeNode.MaxY, treeNode.MaxZ);
            }
        }

        // A side left open by a not-a-number coordinate, or one without end, cannot be drawn.
        private static void _wire(float minX, float minY, float minZ, float maxX, float maxY, float maxZ) {
            var size = new Vector3(maxX - minX, maxY - minY, maxZ - minZ);
            if (float.IsNaN(size.x + size.y + size.z) || float.IsInfinity(size.x + size.y + size.z)) { return; }
            Gizmos.DrawWireCube(new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f), size);
        }
    }
}
