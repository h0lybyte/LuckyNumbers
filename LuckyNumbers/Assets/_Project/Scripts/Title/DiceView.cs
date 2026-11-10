using System;
using System.Collections.Generic;
using LuckyNumbers.Model;
using UnityEngine;
using UnityEngine.UIElements;

namespace LuckyNumbers.Title
{
    /// <summary>
    /// Dice row on the title UI. d6 show pips; d20s are drawn as a d20 (hexagon, centre face, facets) with the number
    /// on the face; other dice show a number. Hovering (or tapping) a die lights it and shows what it decides (content:
    /// Die.name / tooltip) and its current roll, beside the dice panel on whichever side has room, with an arrow to the
    /// die (above the panel only when neither side fits), so it never covers the menu. No logic: DicePresenter drives it.
    /// </summary>
    [View]
    public sealed partial class DiceView : MonoBehaviour
    {
        // Pip cells (3x3, row-major) lit for each d6 face.
        static readonly int[][] PipPattern =
        {
            Array.Empty<int>(), new[] { 4 }, new[] { 0, 8 }, new[] { 0, 4, 8 },
            new[] { 0, 2, 6, 8 }, new[] { 0, 2, 4, 6, 8 }, new[] { 0, 2, 3, 5, 6, 8 },
        };

        [SerializeField] UIDocument document;

        [Ui("dice")] VisualElement _dice;
        [Ui("roll")] Button _roll;
        [Ui("lucky-number")] Label _lucky;

        readonly List<VisualElement> _dieElements = new();
        readonly List<VisualElement[]> _pips = new();
        readonly List<Label> _numbers = new();
        readonly List<Die> _config = new();
        readonly List<int> _shown = new();
        VisualElement _tip, _tipArrow;
        Label _tipText;
        int _tipFor = -1;
        const float TipGap = 14f;     // panel edge to tip
        const float ArrowHalf = 6f;   // the arrow is a 12 px square turned 45 degrees
        bool _bound;

        public void Bind()
        {
            if (_bound) return;
            BindUi(document.rootVisualElement);
            _bound = true;
        }

        public void Build(IReadOnlyList<Die> dice, string rollLabel, Action onRoll)
        {
            if (onRoll != null)   // the intro shows the dice without a Roll button
            {
                _roll.text = rollLabel;
                _roll.clicked += onRoll;
            }
            if (_tip == null && _dice.parent != null)
            {
                _tip = new VisualElement { pickingMode = PickingMode.Ignore };
                _tip.AddToClassList("ln-dice-tip");
                _tip.style.display = DisplayStyle.None;
                _tipText = new Label { pickingMode = PickingMode.Ignore, enableRichText = true };
                _tipText.AddToClassList("ln-dice-tip-text");
                _tipArrow = new VisualElement { pickingMode = PickingMode.Ignore };
                _tipArrow.AddToClassList("ln-dice-tip-arrow");
                _tip.Add(_tipText);
                _tip.Add(_tipArrow);
                _dice.parent.Add(_tip);
            }
            foreach (var die in dice)
            {
                var index = _dieElements.Count;
                var body = new VisualElement { pickingMode = PickingMode.Position };
                body.AddToClassList("ln-die");
                if (die.Sides != 6) body.AddToClassList("ln-die--number");
                body.RegisterCallback<PointerEnterEvent>(_ => ShowTip(index));
                body.RegisterCallback<PointerLeaveEvent>(_ => HideTip(index));
                body.RegisterCallback<PointerDownEvent>(e =>   // touch has no hover: a tap toggles it
                {
                    if (e.pointerType == UnityEngine.UIElements.PointerType.mouse) return;
                    if (_tipFor == index) HideTip(index); else ShowTip(index);
                });
                var c = die.Color;
                var color = new Color(c.R, c.G, c.B, c.A);
                if (die.Sides == 20)
                {
                    body.AddToClassList("ln-die--d20");
                    body.generateVisualContent += ctx => DrawD20(ctx, body, color, _tipFor == index);
                }
                else body.style.backgroundColor = color;
                var ink = 0.299f * c.R + 0.587f * c.G + 0.114f * c.B > 0.6f ? new Color(0.08f, 0.08f, 0.12f) : Color.white;

                VisualElement[] pips = null;
                Label number = null;
                if (die.Sides == 6)
                {
                    pips = new VisualElement[9];
                    for (var i = 0; i < 9; i++)
                    {
                        var cell = new VisualElement { pickingMode = PickingMode.Ignore };
                        cell.AddToClassList("ln-pip-cell");
                        var pip = new VisualElement { pickingMode = PickingMode.Ignore };
                        pip.AddToClassList("ln-pip");
                        pip.style.backgroundColor = ink;
                        cell.Add(pip);
                        body.Add(cell);
                        pips[i] = pip;
                    }
                }
                else
                {
                    number = new Label { pickingMode = PickingMode.Ignore };
                    number.AddToClassList("ln-die-number");
                    if (die.Sides == 20)
                    {
                        // Lit in the die's colour on the dark face.
                        number.style.color = Color.Lerp(color, Color.white, 0.65f);
                        var glowColor = color;
                        glowColor.a = 0.9f;
                        number.style.textShadow = new TextShadow { color = glowColor, blurRadius = 6f };
                    }
                    else number.style.color = ink;
                    body.Add(number);
                }
                _dice.Add(body);
                _config.Add(die);
                _shown.Add(die.InitialFace);
                _dieElements.Add(body);
                _pips.Add(pips);
                _numbers.Add(number);
            }
        }

        /// <summary>Show a face; tumble tilts the die (degrees) while rolling, 0 when it lands.</summary>
        public void ShowFace(int index, int face, float tumble)
        {
            var pips = _pips[index];
            if (pips != null)
            {
                var lit = PipPattern[Mathf.Clamp(face, 0, 6)];
                for (var i = 0; i < 9; i++) pips[i].EnableInClassList("ln-pip--off", Array.IndexOf(lit, i) < 0);
            }
            else
            {
                _numbers[index].text = face.ToString();
            }
            _dieElements[index].style.rotate = new Rotate(tumble);
            _shown[index] = face;
            if (_tipFor == index) ShowTip(index);
        }

        void ShowTip(int index)
        {
            if (_tip == null || index >= _config.Count) return;
            var die = _config[index];
            var face = _shown[index];
            var text = (die.Tooltip ?? "").Replace("{face}", face.ToString()).Replace("{sides}", die.Sides.ToString());
            var name = string.IsNullOrEmpty(die.Name) ? die.Id : die.Name;
            _tipText.text = $"<b>{name}</b>  d{die.Sides}, rolled {face}\n{text}";
            _tip.style.display = DisplayStyle.Flex;
            var previous = _tipFor;
            _tipFor = index;
            Highlight(previous);
            Highlight(index);
            Place(index);
        }

        void HideTip(int index)
        {
            if (_tip == null || _tipFor != index) return;
            _tip.style.display = DisplayStyle.None;
            _tipFor = -1;
            Highlight(index);
        }

        void Highlight(int index)
        {
            if (index < 0 || index >= _dieElements.Count) return;
            _dieElements[index].EnableInClassList("ln-die--active", _tipFor == index);
            _dieElements[index].MarkDirtyRepaint();
        }

        /// <summary>
        /// Beside the panel, on the side with more room (right if both fit), its arrow level with the die; above the
        /// panel, arrow down to the die, when neither side has room for the tip.
        /// </summary>
        void Place(int index)
        {
            var panel = _tip.parent;
            var screen = document.rootVisualElement.worldBound;
            var box = panel.worldBound;
            var die = _dieElements[index].worldBound;
            var width = _tip.resolvedStyle.width > 0f ? _tip.resolvedStyle.width : 280f;
            var right = screen.xMax - box.xMax - TipGap;
            var left = box.xMin - screen.xMin - TipGap;
            var side = right >= width ? "right" : left >= width ? "left" : "above";
            _tip.EnableInClassList("ln-dice-tip--right", side == "right");
            _tip.EnableInClassList("ln-dice-tip--left", side == "left");
            _tip.EnableInClassList("ln-dice-tip--above", side == "above");
            if (side == "above")
            {
                _tip.style.top = StyleKeyword.Null;
                _tipArrow.style.top = StyleKeyword.Null;
                _tipArrow.style.left = die.center.x - box.xMin - ArrowHalf;
            }
            else
            {
                // The tip's top sits a little above the die's middle; the arrow points at that middle.
                const float arrowY = 18f;
                _tip.style.top = die.center.y - box.yMin - arrowY;
                _tipArrow.style.top = arrowY - ArrowHalf;
                _tipArrow.style.left = StyleKeyword.Null;
            }
        }

        // ---- d20 -------------------------------------------------------------------------------------------

        static readonly Color Neon = new(0f, 0.9f, 1f, 1f);

        /// <summary>
        /// A d20 seen face-on, as a hologram: dark glass tinted in the die's colour (outer facets darkest, the upward
        /// centre face where the number sits brightest), neon facet lines and a glowing outline in that colour.
        /// Active (hovered): the glow turns bright cyan.
        /// </summary>
        static void DrawD20(MeshGenerationContext ctx, VisualElement el, Color c, bool active)
        {
            var r = el.contentRect;
            var center = r.center;
            var radius = Mathf.Min(r.width, r.height) * 0.5f - 4f;   // room for the glow
            if (radius <= 2f) return;
            Vector2 At(float degrees, float distance) =>
                center + new Vector2(Mathf.Cos(degrees * Mathf.Deg2Rad), Mathf.Sin(degrees * Mathf.Deg2Rad)) * distance;
            // Hexagon from the top, clockwise; centre face: top, lower right, lower left.
            var hex = new Vector2[6];
            for (var i = 0; i < 6; i++) hex[i] = At(-90f + 60f * i, radius);
            var tri = new[] { At(-90f, radius * 0.7f), At(30f, radius * 0.7f), At(150f, radius * 0.7f) };

            Color Tint(float k, float a) { var t = Color.Lerp(Color.black, c, k); t.a = a; return t; }
            Color Line(float a) { var t = Color.Lerp(c, Color.white, 0.2f); t.a = a; return t; }
            var glow = active ? Neon : c;

            var p = ctx.painter2D;
            p.lineJoin = LineJoin.Round;
            // Glow: wide faint strokes under the body.
            for (var i = 0; i < 2; i++)
            {
                var g = glow;
                g.a = active ? (i == 0 ? 0.18f : 0.35f) : (i == 0 ? 0.1f : 0.2f);
                p.strokeColor = g;
                p.lineWidth = i == 0 ? 7f : 4f;
                Polygon(p, hex);
                p.Stroke();
            }

            p.fillColor = Tint(0.16f, 0.94f);
            Polygon(p, hex);
            p.Fill();
            p.fillColor = Tint(0.26f, 0.95f);
            Polygon(p, tri[0], hex[1], tri[1]);
            p.Fill();
            Polygon(p, tri[1], hex[3], tri[2]);
            p.Fill();
            Polygon(p, tri[2], hex[5], tri[0]);
            p.Fill();
            p.fillColor = Tint(0.4f, 0.96f);
            Polygon(p, tri);
            p.Fill();

            p.strokeColor = Line(0.55f);
            p.lineWidth = 1f;
            p.BeginPath();
            Segment(p, tri[0], hex[0]); Segment(p, tri[0], hex[1]); Segment(p, tri[0], hex[5]);
            Segment(p, tri[1], hex[1]); Segment(p, tri[1], hex[2]); Segment(p, tri[1], hex[3]);
            Segment(p, tri[2], hex[3]); Segment(p, tri[2], hex[4]); Segment(p, tri[2], hex[5]);
            p.Stroke();
            p.strokeColor = Line(0.9f);
            p.lineWidth = 1.3f;
            Polygon(p, tri);
            p.Stroke();

            p.strokeColor = active ? Neon : Line(1f);
            p.lineWidth = active ? 2.2f : 1.6f;
            Polygon(p, hex);
            p.Stroke();
        }

        static void Polygon(Painter2D p, params Vector2[] points)
        {
            p.BeginPath();
            p.MoveTo(points[0]);
            for (var i = 1; i < points.Length; i++) p.LineTo(points[i]);
            p.ClosePath();
        }

        static void Segment(Painter2D p, Vector2 a, Vector2 b)
        {
            p.MoveTo(a);
            p.LineTo(b);
        }

        public void SetLuckyNumber(string text) => _lucky.text = text;
        public int Count => _dieElements.Count;
        public void SetRolling(bool rolling) => _roll.SetEnabled(!rolling);
    }
}
