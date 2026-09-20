using System;

namespace CutOnce.Core.Vision
{
    /// <summary>
    /// A low-pass whose cutoff rises with speed: still when the object is still, quick when it moves. Cheaper and
    /// better behaved than a Kalman filter for one unforced dimension, and it is what removes the last of the
    /// label shimmer without adding lag to a real move.
    /// </summary>
    public sealed class OneEuro
    {
        /// <summary>Settable, not frozen at construction: the owner keeps the tuning and pushes it in before filtering.</summary>
        public float MinCutoffHz = 1f, Beta = 0.15f, DerivativeCutoffHz = 1f;
        float _value, _derivative;
        bool _started;

        public float Value => _value;

        public void Reset(float value) { _value = value; _derivative = 0f; _started = true; }

        public float Filter(float target, float dt)
        {
            if (!_started) { Reset(target); return _value; }
            if (dt <= 0f) return _value;
            var rate = (target - _value) / dt;
            _derivative = Lerp(_derivative, rate, Alpha(DerivativeCutoffHz, dt));
            var cutoff = MinCutoffHz + Beta * Math.Abs(_derivative);
            _value = Lerp(_value, target, Alpha(cutoff, dt));
            return _value;
        }

        static float Alpha(float cutoffHz, float dt)
        {
            var tau = 1f / (2f * (float)Math.PI * Math.Max(cutoffHz, 0.001f));
            return 1f / (1f + tau / dt);
        }

        static float Lerp(float a, float b, float t) => a + (b - a) * BoxMath.Clamp01(t);
    }

    /// <summary>
    /// Holds one object's box still across frames.
    ///
    /// Three things it is careful about:
    /// 1. DIMENSIONS ARE NOT POSE. A box that breathes by a centimetre a frame looks broken even when its centre is
    ///    perfect, so extents get their own slower filter, and fits that disagree with what is held move them slower
    ///    still — enough that a run of them eventually wins, not enough for one of them to matter.
    /// 2. A BAD FIRST SIGHTING MUST NOT BE PERMANENT. Gating jumps against the current estimate protects a good box
    ///    from a bad measurement — and traps a box that was born wrong, because every correct measurement afterwards
    ///    looks like a jump. Consecutive jumps that agree WITH EACH OTHER are therefore adopted: one bad batch is
    ///    noise, three that tell the same story are the truth.
    /// 3. GEOMETRY IS NOT SEMANTICS. How sure we are that it is a bottle and how sure we are of its box are different
    ///    numbers; only the second one decides whether the full box is drawn.
    /// </summary>
    public sealed class BoxSmoother
    {
        public float PositionCutoffHz = 1.2f, PositionBeta = 0.12f;
        /// <summary>
        /// How long the held extents take to move half way to a new measurement. Slower than the pose filter: size
        /// should settle, not track. Stated as a time, like everything else here, so the behaviour does not change
        /// when the scan rate does.
        /// </summary>
        public float SizeHalfLifeS = 1.0f;
        public float YawHalfLifeS = 1.0f;
        /// <summary>
        /// The same, for fits that DISAGREE with the extents already held. Much slower on purpose: one bad
        /// measurement should barely move a settled box, while a new truth repeated over several seconds still wins.
        /// </summary>
        public float DisagreeingSizeHalfLifeS = 6.0f;
        /// <summary>How quickly the held confidence follows the newest fit's.</summary>
        public float ConfidenceRiseHalfLifeS = 0.45f;
        /// <summary>A measurement further than this from the held centre is treated as a bad depth batch.</summary>
        public float JumpM = 0.35f;
        /// <summary>…unless this many in a row agree with each other, which means the object moved (or was born wrong).</summary>
        public int JumpsBeforeAdopt = 3;
        /// <summary>Fits whose extents differ by less than this count as agreeing.</summary>
        public float SizeAgreeM = 0.06f;
        /// <summary>Agreeing fits needed before the full box is drawn rather than a hint.</summary>
        public int FitsBeforeStable = 3;
        /// <summary>
        /// How long confidence takes to halve while nothing can be measured. A half-life rather than a per-cycle
        /// factor, because the scan rate is not fixed: a fixed factor fades ten times faster at 30 Hz than at 3.
        /// </summary>
        public float ConfidenceHalfLifeS = 0.6f;

        readonly OneEuro _x = new OneEuro(), _y = new OneEuro(), _z = new OneEuro();
        bool _started;
        int _agreeing, _jumps;
        P3 _firstJump;

        public P3 Centre { get; private set; }
        public P3 Size { get; private set; }
        public float YawDeg { get; private set; }
        /// <summary>The newest fit's own confidence, decayed while nothing is measured.</summary>
        public float GeometryConfidence { get; private set; }
        /// <summary>Enough agreeing fits to draw the full box. Until then the caller should show a hint, not a claim.</summary>
        public bool Stable => _agreeing >= FitsBeforeStable;
        public bool HasBox => _started;

        /// <summary>A cycle in which the object was detected but no box could be measured.</summary>
        public void Miss(float dt)
        {
            if (dt <= 0f) return;
            GeometryConfidence *= Decay(dt, ConfidenceHalfLifeS);
            if (_agreeing > 0) _agreeing--;
            // A jump story told either side of a gap is not one story: between the two the object could have been
            // anywhere. Without this, three jumps minutes apart are adopted as though they were consecutive.
            _jumps = 0;
        }

        /// <summary>How far to move towards a target in `dt` seconds, closing half the gap every `halfLife`.</summary>
        static float Approach(float dt, float halfLife) => 1f - Decay(dt, halfLife);

        /// <summary>What a value is multiplied by after `dt` seconds of halving every `halfLife`.</summary>
        static float Decay(float dt, float halfLife)
        {
            if (dt <= 0f || halfLife <= 0f) return 1f;
            return (float)Math.Pow(0.5, dt / halfLife);
        }

        public void Update(FitResult fit, float dt)
        {
            if (!fit.Ok) { Miss(dt); return; }

            if (!_started)
            {
                Adopt(fit);
                return;
            }

            // No time passed, so there is nothing to filter — and nothing has been CONFIRMED either. Counting these
            // would let three zero-length cycles declare a box stable.
            if (dt <= 0f) return;

            if ((fit.Centre - Centre).Length > JumpM)
            {
                // Hold the box, but remember the story the jumps are telling.
                if (_jumps == 0 || (fit.Centre - _firstJump).Length > JumpM) { _jumps = 1; _firstJump = fit.Centre; }
                else _jumps++;
                if (_jumps >= JumpsBeforeAdopt) Adopt(fit);
                else { GeometryConfidence *= Decay(dt, ConfidenceHalfLifeS * 2f); if (_agreeing > 0) _agreeing--; }
                return;
            }

            _jumps = 0;
            _x.MinCutoffHz = _y.MinCutoffHz = _z.MinCutoffHz = PositionCutoffHz;   // tuning is read here, not at construction
            _x.Beta = _y.Beta = _z.Beta = PositionBeta;
            var agrees = Math.Abs(fit.Size.X - Size.X) < SizeAgreeM
                      && Math.Abs(fit.Size.Y - Size.Y) < SizeAgreeM
                      && Math.Abs(fit.Size.Z - Size.Z) < SizeAgreeM;
            _agreeing = agrees ? _agreeing + 1 : 1;

            Centre = new P3(_x.Filter(fit.Centre.X, dt), _y.Filter(fit.Centre.Y, dt), _z.Filter(fit.Centre.Z, dt));
            var toSize = Approach(dt, agrees ? SizeHalfLifeS : DisagreeingSizeHalfLifeS);
            Size = new P3(
                Mix(Size.X, fit.Size.X, toSize),
                Mix(Size.Y, fit.Size.Y, toSize),
                Mix(Size.Z, fit.Size.Z, toSize));
            YawDeg = BoxMath.Wrap90(YawDeg + BoxMath.DeltaYaw(YawDeg, fit.YawDeg) * Approach(dt, YawHalfLifeS));
            GeometryConfidence = Mix(GeometryConfidence, fit.Confidence, Approach(dt, ConfidenceRiseHalfLifeS));
        }

        void Adopt(FitResult fit)
        {
            _started = true;
            _jumps = 0;
            _agreeing = 1;
            Centre = fit.Centre;
            Size = fit.Size;
            YawDeg = fit.YawDeg;
            GeometryConfidence = fit.Confidence;
            _x.Reset(fit.Centre.X); _y.Reset(fit.Centre.Y); _z.Reset(fit.Centre.Z);
        }

        static float Mix(float from, float to, float t) => from + (to - from) * BoxMath.Clamp01(t);
    }
}
