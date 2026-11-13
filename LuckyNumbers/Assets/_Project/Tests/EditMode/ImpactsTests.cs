using LuckyNumbers.Station;
using NUnit.Framework;
using UnityEngine;

namespace LuckyNumbers.Tests
{
    /// <summary>Impacts (crashes): damage by closing speed, bounces that keep momentum, the hull pushing ships out.</summary>
    public sealed class ImpactsTests
    {
        [Test]
        public void Damage_ByClosingSpeedAboveTheThreshold()
        {
            Assert.AreEqual(0, Impacts.Damage(8f, 10f, 0.5f, 1f), "a bump");
            Assert.AreEqual(10, Impacts.Damage(30f, 10f, 0.5f, 1f));
            Assert.AreEqual(20, Impacts.Damage(30f, 10f, 0.5f, 5f), "size counts, up to double");
            Assert.AreEqual(5, Impacts.Damage(30f, 10f, 0.5f, 0.1f), "and down to half");
        }

        [Test]
        public void Bounce_KeepsMomentumAndParts()
        {
            var a = new Vector3(0f, 0f, -20f);   // a moving down into b
            var b = new Vector3(0f, 0f, 10f);
            var n = Vector3.forward;              // from b to a
            var before = a * 2f + b * 1f;
            var closing = Impacts.Bounce(ref a, ref b, n, 2f, 1f, 0.5f, 1f);
            Assert.AreEqual(30f, closing, 1e-3f);
            Assert.That((a * 2f + b * 1f - before).magnitude, Is.LessThan(1e-3f), "momentum kept");
            Assert.AreEqual(15f, Vector3.Dot(a - b, n), 1e-3f, "parting at restitution x closing");
            var again = Impacts.Bounce(ref a, ref b, n, 2f, 1f, 0.5f, 1f);
            Assert.AreEqual(0f, again, "already parting: nothing");
        }

        [Test]
        public void OutOfBoxes_BouncesOffTheFaceAndKeepsTheSlide()
        {
            var boxes = new[] { new Bounds(Vector3.zero, new Vector3(10f, 10f, 10f)) };
            var p = new Vector3(5.5f, 0f, 0f);    // 1 m radius: 0.5 m into the +x face
            var v = new Vector3(-20f, 0f, 8f);
            var hit = Impacts.OutOfBoxes(boxes, ref p, ref v, 1f, 0.4f, 0.9f, out var n);
            Assert.AreEqual(20f, hit, 1e-3f);
            Assert.AreEqual(Vector3.right, n);
            Assert.AreEqual(6f, p.x, 1e-3f, "pushed out");
            Assert.AreEqual(8f, v.x, 1e-3f, "bounced at 0.4");
            Assert.AreEqual(7.2f, v.z, 1e-3f, "scrape keeps 0.9 of the slide");
            var p2 = new Vector3(5.5f, 0f, 0f);
            var slide = new Vector3(0f, 0f, 8f);
            Assert.AreEqual(0f, Impacts.OutOfBoxes(boxes, ref p2, ref slide, 1f, 0.4f, 0.9f, out _), "sliding along: no crash");
            Assert.AreEqual(8f, slide.z, 1e-3f, "and no speed lost");
        }
    }
}
