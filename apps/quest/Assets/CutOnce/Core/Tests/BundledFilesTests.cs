using System.IO;
using NUnit.Framework;

namespace CutOnce.Core.Tests
{
    /// <summary>
    /// The app carries copies of two repo files in a Resources folder (a build cannot read outside Assets). A copy
    /// that drifts is a silent bug, so this fails until the copy is refreshed: `pnpm quest:bundle`.
    /// </summary>
    public class BundledFilesTests
    {
        static string Bundled(string name) => Path.Combine(RepoFiles.Root, "apps", "quest", "Assets", "CutOnce", "AR", "Resources", "CutOnce", name);

        [TestCase("data/fixtures/hologram-palette.json", "hologram-palette.json")]
        public void TheBundledCopyMatchesItsSource(string source, string bundled)
        {
            var original = File.ReadAllBytes(Path.Combine(RepoFiles.Root, source));
            // The .glb is in Git LFS: a checkout without it (CI) has a text pointer there, so there is nothing to compare.
            if (System.Text.Encoding.ASCII.GetString(original, 0, System.Math.Min(40, original.Length)).StartsWith("version https://git-lfs"))
                Assert.Ignore($"{source} is a Git LFS pointer in this checkout");
            Assert.That(File.ReadAllBytes(Bundled(bundled)), Is.EqualTo(original), $"{bundled} is out of date: run `pnpm quest:bundle`");
        }
    }
}
