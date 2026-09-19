using NUnit.Framework;
using UnityEngine;
using CutOnce.AR;

public class RevealTests
{
    static readonly Bounds Floor3 = new(new Vector3(20, 10.6f, 45), new Vector3(42, 4.2f, 91)); // a building floor; its pivot is irrelevant

    [Test] public void StartsBelowThePart() => Assert.Less(Reveal.CutHeight(Floor3, 0f), Floor3.min.y);
    [Test] public void EndsAtTheTop() => Assert.AreEqual(Floor3.max.y, Reveal.CutHeight(Floor3, 1f), 1e-5f);
    [Test] public void RisesMonotonically() => Assert.Less(Reveal.CutHeight(Floor3, 0.3f), Reveal.CutHeight(Floor3, 0.6f));
}
