using UnityEngine;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// A picture-in-picture view of the hands and what they hold (top left), for trying weapons: right side, left side,
    /// top, the hands up close (right, left, below), off. A second camera drawing only the arms' layer from beside the
    /// eyes, so walls never block it and it moves with your look. V cycles it (the dev range and the station's range).
    /// </summary>
    public sealed class HandsSideView
    {
        // Eye-space camera spots: where it sits, what it looks at (the held gun's usual place). Wide views look at the
        // whole gun; close ones at the hands on it (where the fitted holds put the grip).
        static readonly Vector3 Gun = new(0.05f, -0.2f, 0.45f), Hands = new(0.1f, -0.24f, 0.42f);
        static readonly (string name, Vector3 at, Vector3 focus, Vector3 up)[] Views =
        {
            ("off", default, default, default),
            ("right side", new Vector3(1.25f, -0.12f, 0.45f), Gun, Vector3.up),
            ("left side", new Vector3(-1.25f, -0.12f, 0.45f), Gun, Vector3.up),
            ("top", new Vector3(0.05f, 1.15f, 0.45f), Gun, Vector3.forward),
            ("hands, right close", new Vector3(0.6f, -0.18f, 0.42f), Hands, Vector3.up),
            ("hands, left close", new Vector3(-0.4f, -0.18f, 0.42f), Hands, Vector3.up),
            ("hands, below", new Vector3(0.1f, -0.75f, 0.45f), Hands, Vector3.forward),
        };

        readonly PlayerView _player;
        readonly ArmsView _arms;
        Camera _side;
        int _view;   // index into Views (0: off)

        public HandsSideView(PlayerView player, ArmsView arms)
        {
            _player = player;
            _arms = arms;
        }

        public bool On => _view != 0;

        /// <summary>The next view; returns its name ("off" when it switches off).</summary>
        public string Cycle()
        {
            _view = (_view + 1) % Views.Length;
            Apply();
            return Views[_view].name;
        }

        public void Off()
        {
            _view = 0;
            Apply();
        }

        void Apply()
        {
            var (name, at, focus, up) = Views[_view];
            if (_side == null && _view != 0)
            {
                var eyes = _player.Camera;
                var go = new GameObject("SideViewCamera");
                go.transform.SetParent(eyes.transform, false);
                _side = go.AddComponent<Camera>();
                _side.cullingMask = 1 << _arms.gameObject.layer;   // arms and what they hold only
                _side.clearFlags = CameraClearFlags.SolidColor;
                _side.backgroundColor = new Color(0.08f, 0.09f, 0.12f);
                _side.fieldOfView = 45f;
                _side.nearClipPlane = 0.05f;
                _side.farClipPlane = 4f;   // (from 1.25 m: shoulder to muzzle fits)
                _side.depth = eyes.depth + 10f;   // a base camera drawn after the main view, into its own rect
                _side.rect = new Rect(0.01f, 0.56f, 0.34f, 0.42f);
            }
            if (_side == null) return;
            _side.enabled = _view != 0;
            if (_view != 0) _side.transform.SetLocalPositionAndRotation(at, Quaternion.LookRotation(focus - at, up));
            Debug.Log($"[SideView] {name}");
        }
    }
}
