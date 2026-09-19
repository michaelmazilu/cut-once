using System.Collections.Generic;
using System.Linq;
using CutOnce.Core;
using NUnit.Framework;
using UnityEngine;

namespace CutOnce.AR.Tests
{
    public class HologramGeometryTests
    {
        const float Tol = 1e-4f;
        static void AreClose(Vector3 want, Vector3 got, float tol = Tol) => Assert.That((want - got).magnitude, Is.LessThan(tol), $"expected {want:F4}, got {got:F4}");

        // ── plan frame → Unity ──────────────────────────────────────────────────────────────────────────────────
        [Test]
        public void PointsMirrorXAndSizesDoNot()
        {
            AreClose(new Vector3(-1, 2, 3), ModelSpace.Point(new double[] { 1, 2, 3 }));
            AreClose(new Vector3(1, 2, 3), ModelSpace.Size(new double[] { 1, 2, 3 }));
        }

        /// <summary>Mirroring a rotated point must equal rotating the mirrored point by the mirrored rotation, for any axis.</summary>
        [TestCase(0, 1, 0, 90)]
        [TestCase(1, 0, 0, 35)]
        [TestCase(0.3, 0.5, 0.8, 120)]
        public void RotationsSurviveTheMirror(double ax, double ay, double az, double degrees)
        {
            double n = System.Math.Sqrt(ax * ax + ay * ay + az * az), half = degrees * System.Math.PI / 360, s = System.Math.Sin(half) / n;
            double[] q = { ax * s, ay * s, az * s, System.Math.Cos(half) };            // right-handed plan rotation
            double[] p = { 0.4, -0.2, 0.7 };

            // Rotate p by q in the plan's right-handed frame, with plain quaternion algebra (no Unity types).
            double[] rotated = RotateRightHanded(q, p);

            AreClose(ModelSpace.Point(rotated), ModelSpace.Rotation(q) * ModelSpace.Point(p), 1e-3f);
        }

        static double[] RotateRightHanded(double[] q, double[] v)
        {
            double x = q[0], y = q[1], z = q[2], w = q[3];
            double[] t = { 2 * (y * v[2] - z * v[1]), 2 * (z * v[0] - x * v[2]), 2 * (x * v[1] - y * v[0]) };
            return new[] { v[0] + w * t[0] + (y * t[2] - z * t[1]), v[1] + w * t[1] + (z * t[0] - x * t[2]), v[2] + w * t[2] + (x * t[1] - y * t[0]) };
        }

        // ── meshes ──────────────────────────────────────────────────────────────────────────────────────────────
        [Test]
        public void ABoxIsItsTrueSizeWithFlatFaces()
        {
            var mesh = ShapeFactory.Box(new Vector3(1f, 0.034f, 0.6f));
            AreClose(new Vector3(1f, 0.034f, 0.6f), mesh.bounds.size);
            Assert.That(mesh.vertexCount, Is.EqualTo(24));
            Assert.That(mesh.triangles.Length, Is.EqualTo(36));
            AssertFacesPointOutwards(mesh);
        }

        [Test]
        public void ACylinderRunsAlongYAtItsTrueSize()
        {
            var mesh = ShapeFactory.Cylinder(0.02f, 0.7f, 24);
            AreClose(new Vector3(0.04f, 0.7f, 0.04f), mesh.bounds.size, 1e-3f);
            AssertFacesPointOutwards(mesh);
        }

        [Test]
        public void ATubeFollowsItsRouteAndSkipsRepeatedPoints()
        {
            var route = new List<Vector3> { Vector3.zero, Vector3.zero, new Vector3(0.5f, 0, 0), new Vector3(0.5f, 0.3f, 0) };
            var mesh = ShapeFactory.Tube(route, 0.004f, 10);
            Assert.That(mesh, Is.Not.Null);
            // The run along X ends in rings that face along X, so only the upright run's radius adds width: 0.5 + 0.004.
            Assert.That(mesh.bounds.size.x, Is.EqualTo(0.504f).Within(1e-3f));
            Assert.That(mesh.bounds.size.y, Is.EqualTo(0.304f).Within(1e-3f));
            Assert.That(ShapeFactory.Tube(new List<Vector3> { Vector3.one, Vector3.one }, 0.004f, 10), Is.Null);
        }

        /// <summary>Winding decides which side is "front"; the shader dims back faces, so an inside-out mesh would look wrong, not crash.</summary>
        static void AssertFacesPointOutwards(Mesh mesh)
        {
            var v = mesh.vertices; var t = mesh.triangles; var n = mesh.normals;
            for (int i = 0; i < t.Length; i += 3)
            {
                var face = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
                Assert.That(Vector3.Dot(face, n[t[i]]), Is.GreaterThan(0f), $"triangle {i / 3} winds against its normal");
                Assert.That(Vector3.Dot(n[t[i]], (v[t[i]] + v[t[i + 1]] + v[t[i + 2]]) / 3f - mesh.bounds.center), Is.GreaterThan(-1e-5f), $"normal of triangle {i / 3} points inwards");
            }
        }

        [Test]
        public void EveryShapeKindBuildsAtItsMirroredPosition()
        {
            var leg = new PartDto { part_id = "part_leg", name = "Leg", position = new double[] { 0.1, 0.35, 0.5 }, shape = new ShapeDto { type = "cylinder", axis = "y", diameter = 0.04, length = 0.7 } };
            var built = ShapeFactory.Build(leg);
            AreClose(new Vector3(-0.1f, 0.35f, 0.5f), built.LocalPosition);
            Assert.That(built.EdgeMode, Is.EqualTo(ShapeFactory.EdgeCylinder));
            AreClose(new Vector3(0.02f, 0.35f, 0.02f), built.HalfSize);

            var rail = new PartDto { part_id = "part_rail", name = "Rail", position = new double[] { 0.5, 0.4, 0.1 }, shape = new ShapeDto { type = "cylinder", axis = "x", diameter = 0.04, length = 0.8 } };
            var railBuilt = ShapeFactory.Build(rail);
            AreClose(Vector3.right, (railBuilt.LocalRotation * Vector3.up).Abs(), 1e-3f);

            var model = new PartDto { part_id = "part_model", name = "Model", position = new double[] { 0, 0, 0 },
                shape = new ShapeDto { type = "mesh", uri = "model.glb", node = "part_model", bounds = new BoundsDto { min = new double[] { 0, 0, 0 }, max = new double[] { 40, 4.5, 90 } } } };
            var stand = ShapeFactory.Build(model);
            AreClose(new Vector3(-20f, 2.25f, 45f), stand.LocalPosition);
            AreClose(new Vector3(40f, 4.5f, 90f), stand.Mesh.bounds.size, 1e-2f);

            // A cable route: its points are offsets from the part's position, as in the web viewer and the validator.
            var cable = new PartDto { part_id = "part_cable", name = "Cable", position = new double[] { 0.6, 0.0, 0.1 },
                shape = new ShapeDto { type = "polyline", diameter = 0.008, points = new List<double[]> { new double[] { 0, 0.03, 0 }, new double[] { 0.3, 0.03, 0 } } } };
            var route = ShapeFactory.Build(cable);
            AreClose(new Vector3(-0.6f, 0f, 0.1f), route.LocalPosition);
            Assert.That(route.Mesh.bounds.min.x, Is.EqualTo(-0.3f).Within(1e-3f), "the run heads toward -X after the mirror");
            Assert.That(route.Mesh.bounds.max.x, Is.EqualTo(0f).Within(1e-3f));

            Assert.That(ShapeFactory.Build(new PartDto { part_id = "part_x", shape = new ShapeDto { type = "hologram" } }), Is.Null);
        }

        // ── placement ───────────────────────────────────────────────────────────────────────────────────────────
        [Test]
        public void StandingAModelOnASurfaceRestsItsLowestFaceThereAndCentresItsFootprint()
        {
            var model = new Bounds(new Vector3(-0.5f, 0.3f, 0.3f), new Vector3(1f, 0.734f, 0.6f));        // extends below y = 0, like the desk's tabletop
            var surface = new Vector3(2f, 0.72f, 1f);

            var pose = PlacementMath.StandOn(surface, 90f, model);

            Vector3 lowestCentre = pose.position + pose.rotation * new Vector3(model.center.x, model.min.y, model.center.z);
            AreClose(surface, lowestCentre);
            AreClose(Vector3.up, pose.rotation * Vector3.up);                                           // still level
        }

        [Test]
        public void ThePointerFindsTheFloorOnlyWhenItPointsAtIt()
        {
            Assert.That(PlacementMath.HitHorizontalPlane(new Ray(new Vector3(0, 1.5f, 0), new Vector3(0, -1, 1).normalized), 0f, 6f, out var p), Is.True);
            AreClose(new Vector3(0, 0, 1.5f), p);
            Assert.That(PlacementMath.HitHorizontalPlane(new Ray(new Vector3(0, 1.5f, 0), Vector3.up), 0f, 6f, out _), Is.False);
            Assert.That(PlacementMath.HitHorizontalPlane(new Ray(new Vector3(0, 1.5f, 0), Vector3.forward), 0f, 6f, out _), Is.False);
            Assert.That(PlacementMath.HitHorizontalPlane(new Ray(new Vector3(0, 1.5f, 0), new Vector3(0, -0.01f, 1).normalized), 0f, 6f, out _), Is.False, "too far away");
        }

        [Test]
        public void ANudgeTurnsAboutTheFootprintCentreAndSlidesInTheModelsOwnFrame()
        {
            var start = new Pose(new Vector3(1, 0, 2), Quaternion.AngleAxis(30f, Vector3.up));
            var pivot = new Vector3(-0.5f, 0f, 0.3f);
            Vector3 pivotBefore = start.position + start.rotation * pivot;

            var turned = PlacementMath.Nudge(start, Vector3.zero, 15f, pivot);
            AreClose(pivotBefore, turned.position + turned.rotation * pivot);

            var slid = PlacementMath.Nudge(start, new Vector3(0.01f, 0, 0), 0f, pivot);
            AreClose(start.rotation * new Vector3(0.01f, 0, 0), slid.position - start.position);
        }

        [Test]
        public void AFreshPlacementFacesTheOperator()
        {
            float yaw = PlacementMath.YawToward(Vector3.zero, new Vector3(0, 1.6f, -2f));
            AreClose(new Vector3(0, 0, -1), Quaternion.AngleAxis(yaw, Vector3.up) * Vector3.forward);
        }

        // ── the hologram itself ─────────────────────────────────────────────────────────────────────────────────
        [Test]
        public void AssemblyViewBuildsOneViewPerPartAndTakesALook()
        {
            var plan = new PlanDto { plan_id = "plan_test", name = "Test", revision = 1 };
            plan.parts.Add(new PartDto { part_id = "part_top", name = "Top", layer = "structure", position = new double[] { 0.5, -0.017, 0.3 }, shape = new ShapeDto { type = "box", size = new double[] { 1, 0.034, 0.6 } } });
            plan.parts.Add(new PartDto { part_id = "part_leg", name = "Leg", layer = "structure", position = new double[] { 0.1, 0.35, 0.1 }, rests_on = { "part_top" },
                shape = new ShapeDto { type = "cylinder", axis = "y", diameter = 0.04, length = 0.7 }, external_ids = new Dictionary<string, string> { ["source"] = "assumed", ["tolerance_m"] = "unknown" } });

            var root = new GameObject("AssemblyRoot (test)");
            try
            {
                var view = root.AddComponent<AssemblyView>();
                Assert.That(view.Build(plan), Is.Empty);
                Assert.That(view.Build(plan), Is.Empty, "building twice replaces the hologram instead of doubling it");
                Assert.That(root.transform.childCount, Is.EqualTo(2));

                var palette = new HologramPalette();
                foreach (BaseVisual b in System.Enum.GetValues(typeof(BaseVisual))) palette.bases[b.ToString()] = new VisualStyle { fill = "#22D3EE", edge = "#67E8F9", fillAlpha = 0.2, edgeAlpha = 1, edgeWidthPx = 2 };
                view.Show(plan.parts.ToDictionary(p => p.part_id, p => new PartVisual { Base = BaseVisual.MISSING }), palette);

                Assert.That(view.ViewOf("part_leg").Style.dashed, Is.True, "an assumed part draws dashed");
                Assert.That(view.ViewOf("part_top").Style.dashed, Is.False);
                Assert.That(view.ViewOf("part_top").GetComponent<BoxCollider>().size.y, Is.EqualTo(0.034f + 2 * PartView.ColliderPadding).Within(1e-5f));
                Assert.That(view.ViewOf("part_nope"), Is.Null);
            }
            finally { Object.DestroyImmediate(root); }
        }
    }

    static class VectorExtensions
    {
        public static Vector3 Abs(this Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    }
}
