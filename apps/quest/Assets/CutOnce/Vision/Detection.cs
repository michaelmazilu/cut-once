using UnityEngine;

namespace CutOnce.Vision
{
    /// <summary>
    /// Timing for one detector batch. Live time is Unity's monotonic clock when the app acquired
    /// the camera texture, NOT the sensor exposure time or MRUK's UTC timestamp. Its age is only
    /// a lower bound on real camera age. Default metadata is for recorded/legacy inference.
    /// </summary>
    public readonly struct DetectionFrameTiming
    {
        public readonly bool isLiveCapture;
        public readonly double acquiredAtRealtimeSeconds;

        public DetectionFrameTiming(double acquiredAtRealtimeSeconds)
        {
            isLiveCapture = true;
            this.acquiredAtRealtimeSeconds = acquiredAtRealtimeSeconds;
        }
    }

    public enum DetectionPublicationRejection
    {
        None,
        PausedOrSuperseded,
        InvalidLiveTimestamp,
        StaleLiveFrame,
    }

    /// <summary>Pure policy shared by production publication and deterministic tests.</summary>
    public static class DetectionPublicationGate
    {
        public static DetectionPublicationRejection Evaluate(DetectionFrameTiming frame, double now,
            double maximumLiveAge, int startedGeneration, int currentGeneration, bool paused)
        {
            if (paused || startedGeneration != currentGeneration)
                return DetectionPublicationRejection.PausedOrSuperseded;
            if (!frame.isLiveCapture) return DetectionPublicationRejection.None;
            if (!Finite(frame.acquiredAtRealtimeSeconds) || frame.acquiredAtRealtimeSeconds < 0d ||
                !Finite(now) || now < frame.acquiredAtRealtimeSeconds || !Finite(maximumLiveAge) || maximumLiveAge <= 0d)
                return DetectionPublicationRejection.InvalidLiveTimestamp;
            return now - frame.acquiredAtRealtimeSeconds > maximumLiveAge
                ? DetectionPublicationRejection.StaleLiveFrame : DetectionPublicationRejection.None;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

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
