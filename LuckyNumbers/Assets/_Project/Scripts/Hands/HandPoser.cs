using System.Collections.Generic;
using LuckyNumbers.Model;
using UnityEngine;

namespace LuckyNumbers.Hands
{
    /// <summary>
    /// One hand's fingers blending toward named poses (content/hands/poses/*) on top of the animated pose.
    /// Curls are degrees per joint from the bind pose, about axes measured by HandRigMap.MeasureFingers.
    /// </summary>
    public sealed class HandPoser
    {
        readonly Arm _arm;
        readonly Dictionary<string, (float[][] curl, float[] spread, float fidget)> _poses = new();
        readonly float[][] _curl;
        readonly float[] _spread;
        (float[][] curl, float[] spread, float fidget) _target;
        float _weight, _targetWeight;

        /// <summary>The pose being blended toward (null = the animation's own fingers).</summary>
        public string Current { get; private set; }
        /// <summary>How much idle fidget should show through now (1 = no pose holding the fingers).</summary>
        public float Fidget => Mathf.Lerp(1f, _target.curl != null ? _target.fidget : 1f, _weight);

        public HandPoser(Arm arm, IEnumerable<HandPose> poses)
        {
            _arm = arm;
            _curl = new float[arm.Fingers.Length][];
            _spread = new float[arm.Fingers.Length];
            for (var i = 0; i < _curl.Length; i++) _curl[i] = new float[arm.Fingers[i].Joints.Length];
            foreach (var p in poses)
            {
                var curl = new float[arm.Fingers.Length][];
                var spread = new float[arm.Fingers.Length];
                for (var i = 0; i < curl.Length; i++)
                {
                    curl[i] = new float[arm.Fingers[i].Joints.Length];
                    foreach (var fp in p.Fingers)
                    {
                        if (fp.Finger != arm.Fingers[i].Name) continue;
                        for (var j = 0; j < curl[i].Length; j++)
                            curl[i][j] = fp.Curl.Count == 0 ? 0f : fp.Curl[Mathf.Min(j, fp.Curl.Count - 1)];
                        spread[i] = fp.Spread;
                    }
                }
                _poses[p.Name] = (curl, spread, p.Fidget);
            }
        }

        public bool Has(string pose) => !string.IsNullOrEmpty(pose) && _poses.ContainsKey(pose);

        /// <summary>Blend toward a pose (null or unknown = back to the animation's fingers).</summary>
        public void Set(string pose, float weight = 1f)
        {
            if (Has(pose)) { _target = _poses[pose]; Current = pose; _targetWeight = Mathf.Clamp01(weight); }
            else { Current = null; _targetWeight = 0f; }
        }

        /// <summary>Ease the fingers toward the pose (speed = blend per second) and write them.</summary>
        public void Apply(float dt, float speed)
        {
            var k = 1f - Mathf.Exp(-Mathf.Max(0.1f, speed) * dt);
            _weight = Mathf.Lerp(_weight, _targetWeight, k);
            if (_target.curl == null) return;
            for (var i = 0; i < _curl.Length; i++)
            {
                var f = _arm.Fingers[i];
                _spread[i] = Mathf.Lerp(_spread[i], _target.spread[i], k);
                for (var j = 0; j < _curl[i].Length; j++)
                {
                    _curl[i][j] = Mathf.Lerp(_curl[i][j], _target.curl[i][j], k);
                    var joint = f.Joints[j];
                    if (joint == null || _weight < 1e-3f || f.CurlAxes == null) continue;
                    var spread = j == 0 ? Quaternion.AngleAxis(_spread[i] * f.SpreadSign, f.SpreadAxis) : Quaternion.identity;
                    var posed = f.BindJoints[j] * spread * Quaternion.AngleAxis(_curl[i][j], f.CurlAxes[j]);
                    joint.localRotation = Quaternion.Slerp(joint.localRotation, posed, _weight);
                }
            }
        }
    }
}
