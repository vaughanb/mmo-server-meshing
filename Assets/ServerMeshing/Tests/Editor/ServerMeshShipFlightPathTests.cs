using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshShipFlightPathTests
    {
        private const int Seed = 12345;
        private static readonly Rect Cell = new Rect(-300f, -300f, 600f, 600f);

        private static ServerMeshShipFlightPath Path(int index, float factor = 1f)
        {
            ServerMeshShipScale scale = new ServerMeshShipScale(factor);
            Rect area = new Rect(Cell.x * factor, Cell.y * factor, Cell.width * factor, Cell.height * factor);
            Assert.That(ServerMeshShipFlightPath.TryChooseCentre(Seed, index, area, 0f, scale, out Vector3 centre, out string error), Is.True, error);
            return new ServerMeshShipFlightPath(Seed, index, centre, scale);
        }

        private static Vector3 PositionAt(ServerMeshShipFlightPath path, double time)
        {
            path.Evaluate(time, out Vector3 position, out _, out _, out _);
            return position;
        }

        [TestCase(500f, 600f, "500.0 x 600.0")]
        [TestCase(600f, 559f, "600.0 x 559.0")]
        public void TryChooseCentre_AreaNarrowerThanTwoWidestAxes_FailsNamingItsSize(float width, float height, string size)
        {
            bool chosen = ServerMeshShipFlightPath.TryChooseCentre(Seed, 0, new Rect(0f, 0f, width, height), 0f, new ServerMeshShipScale(1f), out _, out string error);

            Assert.That(chosen, Is.False);
            Assert.That(error, Does.Contain(size).And.Not.Contain("{"));
        }

        [Test]
        public void TryChooseCentre_SmallAreaAtSmallScale_Succeeds()
        {
            bool chosen = ServerMeshShipFlightPath.TryChooseCentre(Seed, 0, new Rect(-30f, -30f, 60f, 60f), 5f, new ServerMeshShipScale(0.03f), out Vector3 centre, out string error);

            Assert.That(chosen, Is.True, error);
            Assert.That(centre.y, Is.EqualTo(65f));
            Assert.That(Mathf.Abs(centre.x), Is.LessThanOrEqualTo(30f - 280f * 0.03f + 1e-4f));
            Assert.That(Mathf.Abs(centre.z), Is.LessThanOrEqualTo(30f - 280f * 0.03f + 1e-4f));
        }

        [TestCase(-1)]
        [TestCase(-3)]
        public void Constructor_NoPathIndex_ThrowsNamingTheIndex(int index)
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => new ServerMeshShipFlightPath(Seed, index, Vector3.zero, new ServerMeshShipScale(1f)));

            Assert.That(thrown.ParamName, Is.EqualTo("index"));
        }

        [Test]
        public void Constructor_NullScale_ThrowsNamingIt()
        {
            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => new ServerMeshShipFlightPath(Seed, 0, Vector3.zero, null));

            Assert.That(thrown.ParamName, Is.EqualTo("scale"));
        }

        [Test]
        public void DistanceToPath_NegativeWindow_ThrowsNamingIt()
        {
            ServerMeshShipFlightPath path = Path(0);

            Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => path.DistanceToPath(Vector3.zero, 10d, -0.1d, 0.05d, out _)).ParamName, Is.EqualTo("behindSeconds"));
            Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => path.DistanceToPath(Vector3.zero, 10d, 0.25d, double.NaN, out _)).ParamName, Is.EqualTo("aheadSeconds"));
        }

        private static IEnumerable<TestCaseData> MixSeedCases()
        {
            yield return new TestCaseData(12345, 0, 1918325410).SetName("MixSeed_DefaultSeedIndex0");
            yield return new TestCaseData(12345, 1, 1935102775).SetName("MixSeed_DefaultSeedIndex1");
            yield return new TestCaseData(12345, 1000, -1705937830).SetName("MixSeed_DefaultSeedPhantomBase");
            yield return new TestCaseData(0, 0, 97).SetName("MixSeed_Zeros");
            yield return new TestCaseData(-1, 5, -435858075).SetName("MixSeed_NegativeSeed");
            yield return new TestCaseData(int.MaxValue, int.MaxValue, 502965227).SetName("MixSeed_Overflow");
            yield return new TestCaseData(int.MinValue, -1, 2130706126).SetName("MixSeed_MinSeedNegativeIndex");
            yield return new TestCaseData(42, -2, 1021397717).SetName("MixSeed_ParkedIndex");
        }

        [TestCaseSource(nameof(MixSeedCases))]
        public void MixSeed_IsTheLoadGeneratorFormula(int seed, int k, int expected)
        {
            Assert.That(ServerMeshShipFlightPath.MixSeed(seed, k), Is.EqualTo(expected));
            Assert.That(ServerMeshShipFlightPath.MixSeed(seed, k), Is.EqualTo(unchecked((seed * 486187739) ^ (k * 16777619 + 97))));
        }

        [Test]
        public void SameSeedAndIndex_GiveIdenticalPaths_DifferentIndicesDiffer()
        {
            ServerMeshShipFlightPath a = Path(3), b = Path(3), other = Path(4);

            for (int i = 0; i < 50; ++i)
            {
                double time = 1000d + i * 0.37d;
                Assert.That(PositionAt(a, time), Is.EqualTo(PositionAt(b, time)));
            }
            Assert.That(a.Centre, Is.EqualTo(b.Centre));
            Assert.That(PositionAt(a, 1000d), Is.Not.EqualTo(PositionAt(other, 1000d)));
        }

        [TestCase(1f)]
        [TestCase(0.03f)]
        public void TwoHundredPaths_StayInsideTheArea_WithinTheShapeLimits(float factor)
        {
            Rect area = new Rect(Cell.x * factor, Cell.y * factor, Cell.width * factor, Cell.height * factor);
            for (int index = 0; index < 200; ++index)
            {
                ServerMeshShipFlightPath path = Path(index, factor);
                Assert.That(path.MaxSpeed, Is.InRange(50f * factor, 125f * factor), $"path {index} speed");
                Assert.That(path.SemiMajor, Is.InRange(200f * factor, 280f * factor), $"path {index} semi-major");
                Assert.That(path.SemiMinor / path.SemiMajor, Is.InRange(ServerMeshShipFlightPath.MinAxisRatio - 1e-6f, 1f), $"path {index} axis ratio");
                for (int sample = 0; sample < 50; ++sample)
                {
                    Vector3 position = PositionAt(path, 1_790_000_000d + sample * 1.7d);
                    Assert.That(position.x, Is.InRange(area.xMin, area.xMax), $"path {index} x");
                    Assert.That(position.z, Is.InRange(area.yMin, area.yMax), $"path {index} z");
                    Assert.That(position.y, Is.EqualTo(ServerMeshShipFlightPath.AltitudeMetres));
                }
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(1001)]
        public void Evaluate_VelocityAndYawRate_MatchTheFiniteDifference(int index)
        {
            ServerMeshShipFlightPath path = Path(index);
            const double H = 0.01d;
            for (int i = 0; i < 20; ++i)
            {
                double time = 500d + i * 1.3d;
                path.Evaluate(time, out _, out Quaternion rotation, out Vector3 velocity, out Vector3 angular);
                path.Evaluate(time - H, out Vector3 before, out _, out Vector3 velocityBefore, out _);
                path.Evaluate(time + H, out Vector3 after, out _, out Vector3 velocityAfter, out _);
                Vector3 difference = (after - before) / (float)(2d * H);
                Assert.That((difference - velocity).magnitude, Is.LessThanOrEqualTo(0.001f * velocity.magnitude), $"time {time} velocity");
                double yawRate = DeltaAngle(Math.Atan2(velocityAfter.x, velocityAfter.z), Math.Atan2(velocityBefore.x, velocityBefore.z)) / (2d * H);
                Assert.That(angular.y, Is.EqualTo((float)yawRate).Within(0.001f * Math.Abs((float)yawRate) + 1e-6f), $"time {time} yaw rate");
                Assert.That(Vector3.Angle(rotation * Vector3.forward, velocity), Is.LessThan(0.1f), $"time {time} rotation");
            }
        }

        [Test]
        public void Evaluate_NearAUnixTimestamp_IsContinuous()
        {
            ServerMeshShipFlightPath path = Path(7);
            Vector3 previous = PositionAt(path, 1_790_000_000d);
            for (int i = 1; i <= 2000; ++i)
            {
                Vector3 position = PositionAt(path, 1_790_000_000d + i * 0.001d);
                Assert.That(Vector3.Distance(position, previous), Is.LessThanOrEqualTo(path.MaxSpeed * 0.001f * 1.01f + 1e-3f), $"step {i}");
                previous = position;
            }
        }

        [Test]
        public void DistanceToPath_PointsOnThePathBetweenScanSamples_AreZero()
        {
            ServerMeshShipFlightPath path = Path(2);
            const double T = 1000d;
            for (int k = 0; k < 60; k += 3)
            {
                double time = T - 0.25d + k * ServerMeshShipFlightPath.ScanStepSeconds + 0.0025d;
                float distance = path.DistanceToPath(PositionAt(path, time), T, 0.25d, 0.05d, out double lag);
                Assert.That(distance, Is.LessThanOrEqualTo(1e-4f), $"sample {k}");
                Assert.That(lag, Is.EqualTo(T - time).Within(1e-4d), $"sample {k} lag");
            }
        }

        [TestCase(0.3f)]
        [TestCase(-0.2f)]
        public void DistanceToPath_PointMovedOffTheNormal_EqualsTheOffset(float offset)
        {
            ServerMeshShipFlightPath path = Path(5);
            const double T = 2000d;
            path.Evaluate(T - 0.0125d, out Vector3 position, out _, out Vector3 velocity, out _);
            Vector3 normal = new Vector3(velocity.z, 0f, -velocity.x).normalized;

            float distance = path.DistanceToPath(position + normal * offset, T, 0.25d, 0.05d, out double lag);

            Assert.That(distance, Is.EqualTo(Mathf.Abs(offset)).Within(1e-4f));
            Assert.That(lag, Is.EqualTo(0.0125d).Within(1e-3d));
        }

        [Test]
        public void DistanceToPath_PointFortyMillisecondsBehind_ReportsTheLag()
        {
            ServerMeshShipFlightPath path = Path(9);
            const double T = 3000d;

            float distance = path.DistanceToPath(PositionAt(path, T - 0.040d), T, 0.25d, 0.05d, out double lag);

            Assert.That(distance, Is.LessThanOrEqualTo(1e-4f));
            Assert.That(lag, Is.EqualTo(0.040d).Within(1e-4d));
        }

        [Test]
        public void ParkedPath_StaysAtItsCentre()
        {
            Vector3 centre = new Vector3(10f, 83.09f, -20f);
            ServerMeshShipFlightPath path = new ServerMeshShipFlightPath(Seed, ServerMeshShipFlightPath.ParkedPath, centre, new ServerMeshShipScale(0.03f));

            path.Evaluate(1_790_000_000d, out Vector3 position, out Quaternion rotation, out Vector3 velocity, out Vector3 angular);
            float distance = path.DistanceToPath(centre + new Vector3(0f, 0.5f, 0f), 1_790_000_000d, 0.25d, 0.05d, out double lag);

            Assert.That(path.IsParked, Is.True);
            Assert.That(position, Is.EqualTo(centre));
            Assert.That(rotation, Is.EqualTo(Quaternion.identity));
            Assert.That(velocity, Is.EqualTo(Vector3.zero));
            Assert.That(angular, Is.EqualTo(Vector3.zero));
            Assert.That(distance, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(lag, Is.EqualTo(0d));
        }

        [TestCase(0.03f, 20.09f)]
        [TestCase(1f, 23f)]
        public void ParkedLiftMetres_IsTwentyPlusThreeScaled(float factor, float expected)
        {
            Assert.That(ServerMeshShipFlightPath.ParkedLiftMetres(new ServerMeshShipScale(factor)), Is.EqualTo(expected).Within(1e-5f));
        }

        private static double DeltaAngle(double a, double b)
        {
            double delta = a - b;
            while (delta > Math.PI)
                delta -= 2d * Math.PI;
            while (delta < -Math.PI)
                delta += 2d * Math.PI;
            return delta;
        }
    }
}
