using System;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshRemoteMotionTrackTests
    {
        private const double Tick = 1000.0 / 60.0;
        private const float Speed = 4f; // m/s along x

        [TestCase(-1.0, 20.0, 100.0, 15f, "delayIntervals")]
        [TestCase(1.5, -1.0, 100.0, 15f, "minDelayMilliseconds")]
        [TestCase(1.5, 20.0, double.NaN, 15f, "maxExtrapolationMilliseconds")]
        [TestCase(1.5, 20.0, 100.0, 0f, "correctionRate")]
        public void Constructor_Invalid_Throws(double delay, double minDelay, double extrapolation, float rate, string param)
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => new MeshRemoteMotionTrack(delay, minDelay, extrapolation, rate));

            Assert.That(thrown.ParamName, Is.EqualTo(param));
        }

        [Test]
        public void TryPose_BeforeTwoSamples_IsFalse_AndLeavesTheCurrentPosition()
        {
            MeshRemoteMotionTrack track = new MeshRemoteMotionTrack();
            track.Add(1000, 1005, Vector3.zero, false);

            Assert.That(track.TryPose(1010, 0.01f, new Vector3(1, 0, 0), out Vector3 pose), Is.False);
            Assert.That(pose, Is.EqualTo(new Vector3(1, 0, 0)));
        }

        [Test]
        public void Add_TwoStatesWithinTheMergeWindow_AreOneSample_TheLaterWins()
        {
            MeshRemoteMotionTrack track = new MeshRemoteMotionTrack();
            track.Add(1000, 1005, Vector3.zero, false);
            track.Add(1001, 1005, Vector3.one, false);

            Assert.That(track.Count, Is.EqualTo(1));
        }

        [Test]
        public void Add_AnOlderState_IsDropped()
        {
            MeshRemoteMotionTrack track = new MeshRemoteMotionTrack();
            track.Add(1000, 1005, Vector3.zero, false);
            track.Add(1020, 1025, Vector3.one, false);

            Assert.That(track.Add(1010, 1030, Vector3.one * 2, false), Is.False);
            Assert.That(track.Count, Is.EqualTo(2));
        }

        [Test]
        public void Add_ASnap_ForgetsTheEarlierSamples()
        {
            MeshRemoteMotionTrack track = new MeshRemoteMotionTrack();
            track.Add(1000, 1005, Vector3.zero, false);
            track.Add(1020, 1025, Vector3.one, false);
            track.TryPose(1030, 0.01f, Vector3.zero, out _);

            track.Add(1040, 1045, Vector3.one * 50, true);

            Assert.That(track.Count, Is.EqualTo(1));
            Assert.That(track.IsDrawing, Is.False);
        }

        [Test]
        public void TryPose_FirstPose_StartsWhereTheCharacterIsDrawn()
        {
            MeshRemoteMotionTrack track = new MeshRemoteMotionTrack();
            track.Add(1000, 1005, Vector3.zero, false);
            track.Add(1000 + Tick, 1005 + Tick, Vector3.right * 0.07f, false);
            Vector3 drawn = new Vector3(0.2f, 0f, 0f);

            Assert.That(track.TryPose(1030, 0.007f, drawn, out Vector3 pose), Is.True);
            Assert.That(Vector3.Distance(pose, drawn), Is.LessThan(1e-4f));
        }

        /// <summary>
        /// A cell at 60 Hz whose frames sometimes run no tick and then two (the second tick re-sends the same position),
        /// states arriving in 15 ms transport batches, drawn at 144 fps: every drawn frame moves at the walking speed.
        /// </summary>
        [Test]
        public void UnevenStatesAndBatchedArrivals_DrawAnEvenWalk()
        {
            MeshRemoteMotionTrack track = new MeshRemoteMotionTrack();
            System.Random random = new System.Random(7);
            const double drawFrame = 1000.0 / 144.0;
            double serverFrame = 0.0;      // server frame start, ms
            double nextTick = 0.0;
            double clientNow = 0.0;
            double nextSend = 15.0;        // the transport's 15 ms batches
            System.Collections.Generic.List<(double stamp, Vector3 position)> queued = new System.Collections.Generic.List<(double, Vector3)>();
            System.Collections.Generic.List<(double stamp, Vector3 position)> inFlight = new System.Collections.Generic.List<(double, Vector3)>();
            Vector3 last = Vector3.zero;
            bool drawing = false;
            float slowest = float.MaxValue, fastest = 0f;
            int judged = 0;

            for (int step = 0; step < 144 * 5; ++step)
            {
                clientNow += drawFrame;
                // The server's frames: 16.7 ms on average, jittered so some run no tick and some two.
                while (serverFrame + 16.0 < clientNow)
                {
                    serverFrame += 12.0 + random.NextDouble() * 9.0;
                    Vector3 position = Vector3.right * (float)(Speed * serverFrame / 1000.0);
                    int ticks = 0;
                    while (nextTick <= serverFrame) { nextTick += Tick; ticks++; }
                    for (int t = 0; t < ticks; ++t)
                        queued.Add((serverFrame + t * 0.2, position));
                    if (serverFrame >= nextSend)
                    {
                        nextSend += 15.0;
                        inFlight.AddRange(queued);
                        queued.Clear();
                    }
                }
                foreach ((double stamp, Vector3 position) in inFlight)
                    track.Add(stamp, clientNow, position, false);
                inFlight.Clear();

                if (!track.TryPose(clientNow, (float)(drawFrame / 1000.0), last, out Vector3 pose))
                    continue;
                if (drawing && step > 144) // a second to settle
                {
                    float speed = (pose.x - last.x) / (float)(drawFrame / 1000.0);
                    slowest = Mathf.Min(slowest, speed);
                    fastest = Mathf.Max(fastest, speed);
                    judged++;
                }
                drawing = true;
                last = pose;
            }

            Assert.That(judged, Is.GreaterThan(400));
            Assert.That(slowest, Is.GreaterThan(Speed * 0.8f), $"slowest drawn frame {slowest:0.00} m/s, fastest {fastest:0.00}");
            Assert.That(fastest, Is.LessThan(Speed * 1.2f), $"slowest drawn frame {slowest:0.00} m/s, fastest {fastest:0.00}");
        }

        [Test]
        public void ALateState_IsWalkedThrough_AndItsArrivalDoesNotStep()
        {
            MeshRemoteMotionTrack track = new MeshRemoteMotionTrack();
            const double frame = 1000.0 / 144.0;
            Vector3 last = Vector3.zero;
            double stamp = 0.0, now = 0.0;
            float biggest = 0f;
            for (int step = 0; step < 144 * 2; ++step)
            {
                now += frame;
                // States every tick, except that none arrive for 70 ms halfway through, then all at once.
                bool held = now > 1000.0 && now < 1070.0;
                while (!held && stamp + Tick <= now)
                {
                    stamp += Tick;
                    track.Add(stamp, now, Vector3.right * (float)(Speed * stamp / 1000.0), false);
                }
                if (!track.TryPose(now, (float)(frame / 1000.0), last, out Vector3 pose))
                    continue;
                if (now > 500.0)
                    biggest = Mathf.Max(biggest, Mathf.Abs((pose.x - last.x) / (float)(frame / 1000.0) - Speed));
                last = pose;
            }

            Assert.That(biggest, Is.LessThan(Speed * 0.5f), $"the drawn speed strayed {biggest:0.00} m/s from {Speed}");
        }
    }
}
