using UnityEngine;

namespace CutOnce.Vision
{
    /// <summary>
    /// One thing the vision model saw in one camera frame. Purely 2D — it knows nothing about the
    /// room yet. <see cref="Object3DLocator"/> is what turns this into a place you can stand next to.
    /// </summary>
    public struct DetectedObject
    {
        public int classId;
        public string className;
        public float confidence;

        /// <summary>Box in original CAMERA-IMAGE pixels, origin TOP-LEFT, after undoing the model's letterbox padding and scale.</summary>
        public Rect boundingBox;

        /// <summary>Centre of <see cref="boundingBox"/>, same space.</summary>
        public Vector2 centerPixel;

        /// <summary>Original camera-image dimensions, needed to normalise the box for the camera projection (not the padded model dimensions).</summary>
        public Vector2 inputSize;

        public override string ToString() => $"{className} {confidence:0.00}";
    }
}
