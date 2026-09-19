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

        /// <summary>Box in MODEL-INPUT pixels (640x640), origin TOP-LEFT, as the tensor reports it.</summary>
        public Rect boundingBox;

        /// <summary>Centre of <see cref="boundingBox"/>, same space.</summary>
        public Vector2 centerPixel;

        /// <summary>Model input dimensions, needed to normalise the box for the camera projection.</summary>
        public Vector2 inputSize;

        public override string ToString() => $"{className} {confidence:0.00}";
    }
}
