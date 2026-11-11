using UnityEngine;
using UnityEngine.UIElements;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// Crash-site overlay (full-screen fade, controls hint, dialogue box, flight HUD) and the scene's ships: the
    /// freighter wreck and your fighter, with the chase camera you fly it from.
    /// </summary>
    [View]
    public sealed partial class PlanetView : MonoBehaviour
    {
        [SerializeField] UIDocument document;
        [SerializeField] MeshRenderer sun;   // star sphere far away in the sky
        [SerializeField] LuckyNumbers.Core.ShipView wreck;    // the museum freighter
        [SerializeField] LuckyNumbers.Core.ShipView fighter;  // your ship
        [SerializeField] Camera flightCamera;                 // chase camera while you fly (off otherwise)
        [SerializeField] Transform wreckBeacon;               // unlit beam marking the wreck while you fly in (off otherwise)
        [SerializeField] Transform amuletTemplate;            // relic shapes (off): copied per relic at the crash site
        [SerializeField] Transform vaseTemplate;
        [SerializeField] Transform relicBeamTemplate;         // thin beam over a revealed relic
        [SerializeField] MonsterRig[] monsterModels = System.Array.Empty<MonsterRig>();   // monsters' models (off), by name (MonsterType.model)
        [SerializeField] Transform landingPad;                // ring on the ground where to land (flight assist; off otherwise)

        [Ui("flash")] VisualElement _flash;
        [Ui("hint")] Label _hint;
        [Ui("map-hint")] Label _mapHint;
        [Ui("crosshair")] VisualElement _crosshair;
        [Ui("dialogue")] VisualElement _dialogue;
        [Ui("speaker")] Label _speaker;
        [Ui("line")] Label _line;
        [Ui("flight-hud")] VisualElement _flightHud;
        [Ui("flight-readout")] Label _flightReadout;
        [Ui("flight-message")] Label _flightMessage;

        public MeshRenderer Sun => sun;
        public LuckyNumbers.Core.ShipView Wreck => wreck;
        public LuckyNumbers.Core.ShipView Fighter => fighter;
        public Camera FlightCamera => flightCamera;
        public Transform WreckBeacon => wreckBeacon;
        public Transform AmuletTemplate => amuletTemplate;
        public Transform VaseTemplate => vaseTemplate;
        public Transform RelicBeamTemplate => relicBeamTemplate;

        /// <summary>A monster's model template by name (MonsterType.model), or null (it's a placeholder block).</summary>
        public MonsterRig MonsterModel(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var t in monsterModels) if (t != null && t.name == name) return t;
            return null;
        }
        public Transform LandingPad => landingPad;

        /// <summary>Flight HUD: readout line (null = hide the HUD) and a big centred message (null = none).</summary>
        public void Flight(string readout, string message)
        {
            _flightHud.EnableInClassList("ln-hidden", readout == null);
            _flightReadout.text = readout ?? "";
            _flightMessage.text = message ?? "";
            _crosshair.EnableInClassList("ln-hidden", readout != null);
        }

        /// <summary>
        /// Flight HUD readout from a span (a ZString builder's), no message: the label only gets a new string when the
        /// text reads differently, so a frame whose rounded numbers didn't move allocates nothing.
        /// </summary>
        public void Flight(System.ReadOnlySpan<char> readout)
        {
            _flightHud.EnableInClassList("ln-hidden", false);
            if (!System.MemoryExtensions.SequenceEqual(readout, System.MemoryExtensions.AsSpan(_flightReadout.text))) _flightReadout.text = readout.ToString();
            if (_flightMessage.text.Length > 0) _flightMessage.text = "";
            _crosshair.EnableInClassList("ln-hidden", true);
        }

        public void Bind() => BindUi(document.rootVisualElement);

        public void Fade(float alpha) => _flash.style.opacity = Mathf.Clamp01(alpha);

        public void Dialogue(string speaker, string text, int visibleChars)
        {
            _dialogue.EnableInClassList("ln-hidden", text == null);
            if (text == null) return;
            _speaker.text = speaker;
            _line.text = text.Substring(0, Mathf.Clamp(visibleChars, 0, text.Length));
        }

        /// <summary>Map controls line (null = map closed: hide it, show the crosshair).</summary>
        public void MapHint(string text)
        {
            _mapHint.text = text ?? "";
            _mapHint.EnableInClassList("ln-hidden", text == null);
            _crosshair.EnableInClassList("ln-hidden", text != null);
            _hint.EnableInClassList("ln-hidden", text != null);
        }

        public void Hint(string text, float alpha)
        {
            _hint.text = text;
            _hint.style.opacity = Mathf.Clamp01(alpha);
        }
    }
}
