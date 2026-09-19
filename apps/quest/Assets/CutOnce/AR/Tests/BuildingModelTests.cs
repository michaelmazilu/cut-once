using NUnit.Framework;
using UnityEngine;

namespace CutOnce.AR.Tests
{
    /// <summary>Models as the headset draws them: crease lines from a model file's mesh, and the display scale.</summary>
    public class BuildingModelTests
    {
        [Test]
        public void ACubeHasTwelveCreaseLinesAndNoDiagonals()
        {
            // A unit cube as a glTF exporter writes it: 8 shared corners, 12 triangles, a diagonal across each face.
            var p = new float[] { 0,0,0, 1,0,0, 1,1,0, 0,1,0, 0,0,1, 1,0,1, 1,1,1, 0,1,1 };
            var i = new[] { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 3,7,6, 3,6,2, 0,4,7, 0,7,3, 1,2,6, 1,6,5 };
            var lines = GlbMeshes.EdgeLines(new GlbMesh { Name = "cube", Positions = p, Indices = i }, 0.01f);
            Assert.That(lines.triangles.Length / 3, Is.EqualTo(12 * 4), "12 edges, each two crossed quads of two triangles");
        }

        [Test]
        public void AScaledModelStillStandsOnThePointedSpot()
        {
            var model = new Bounds(new Vector3(-20f, 17f, 45f), new Vector3(42f, 34f, 91f));
            var surface = new Vector3(1f, 0.74f, 2f);
            const float scale = 1f / 200f;
            var pose = PlacementMath.StandOn(surface, 30f, model, scale);
            Vector3 lowestCentre = pose.position + pose.rotation * (new Vector3(model.center.x, model.min.y, model.center.z) * scale);
            Assert.That((lowestCentre - surface).magnitude, Is.LessThan(1e-4f));
        }

        [TestCase(300f, 1f / 500f)]
        public void TheDisplayScaleIsTheLargestArchitecturalScaleThatFitsATable(float span, float expected) =>
            Assert.That(AssemblyView.ScaleFor(new Bounds(Vector3.zero, new Vector3(span, 3f, span * 0.5f))), Is.EqualTo(expected));
    }
}
