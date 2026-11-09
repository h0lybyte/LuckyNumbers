using System;
using System.Collections.Generic;
using LuckyNumbers.Model;
using UnityEngine;
using UnityEngine.UIElements;

namespace LuckyNumbers.Title
{
    /// <summary>Dice row on the title UI. d6 show pips; other dice show a number. No logic: DicePresenter drives it.</summary>
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
            foreach (var die in dice)
            {
                var body = new VisualElement { pickingMode = PickingMode.Ignore };
                body.AddToClassList("ln-die");
                var c = die.Color;
                body.style.backgroundColor = new Color(c.R, c.G, c.B, c.A);
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
                    number.style.color = ink;
                    body.Add(number);
                }
                _dice.Add(body);
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
        }

        public void SetLuckyNumber(string text) => _lucky.text = text;
        public int Count => _dieElements.Count;
        public void SetRolling(bool rolling) => _roll.SetEnabled(!rolling);
    }
}
