using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace LuckyNumbers.Planet
{
    /// <summary>First-person arms: plays a looping clip (by name, e.g. "relax") and takes a sway/bob offset.</summary>
    [View]
    public sealed class ArmsView : MonoBehaviour
    {
        [SerializeField] Transform pivot;          // under the camera; sway rotates/moves this
        [SerializeField] Animator animator;
        [SerializeField] AnimationClip[] clips;    // from arms_rig.fbx, set by the scene builder
        PlayableGraph _graph;
        AnimationClipPlayable _playable;
        AnimationClip _clip;
        Vector3 _restPosition;
        Quaternion _restRotation;

        void Awake()
        {
            _restPosition = pivot.localPosition;
            _restRotation = pivot.localRotation;
        }

        public void Play(string clipName)
        {
            foreach (var c in clips)
                if (c.name == clipName || c.name.EndsWith("|" + clipName)) _clip = c;
            if (_clip == null) { Debug.LogWarning($"[Arms] no clip {clipName}"); return; }
            if (_graph.IsValid()) _graph.Destroy();
            _playable = AnimationPlayableUtilities.PlayClip(animator, _clip, out _graph);
            Debug.Log($"[Arms] {_clip.name}");
        }

        public void SetVisible(bool visible) => pivot.gameObject.SetActive(visible);

        /// <summary>Offset from the rest pose (camera space): bob/sway.</summary>
        public void Pose(Vector3 offset, Quaternion tilt)
        {
            pivot.localPosition = _restPosition + offset;
            pivot.localRotation = tilt * _restRotation;
        }

        void Update()
        {
            if (_clip != null && _playable.IsValid()) _playable.SetTime(Time.time % _clip.length); // loop
        }

        void OnDestroy()
        {
            if (_graph.IsValid()) _graph.Destroy();
        }
    }
}
