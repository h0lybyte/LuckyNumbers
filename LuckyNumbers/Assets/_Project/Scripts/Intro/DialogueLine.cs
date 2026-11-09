using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Model;
using UnityEngine;

namespace LuckyNumbers.Intro
{
    /// <summary>Types out a Dialogue line (typewriter), holds it, clears it. `show(speaker, text, visibleChars)`, null text = hide.</summary>
    public static class DialogueLine
    {
        public static async UniTask Play(Dialogue line, Action<string, string, int> show, CancellationToken ct)
        {
            var text = line.Text ?? "";
            var duration = Mathf.Max(0.5f, line.DurationSeconds);
            var cps = line.CharsPerSecond;
            for (var t = 0f; t < duration; t += Time.deltaTime)
            {
                show(line.Speaker, text, cps > 0f ? (int)(t * cps) : text.Length);
                await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate, ct);
            }
            show(line.Speaker, text, text.Length);
            await UniTask.Delay(400, cancellationToken: ct);
            show(null, null, 0);
        }
    }
}
