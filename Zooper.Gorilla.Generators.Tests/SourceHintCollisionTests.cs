using Xunit;

namespace Zooper.Gorilla.Generators.Tests;

/// <summary>
/// Regression tests for source-hint collisions. The generated file hint must be fully
/// qualified (namespace + containing-type path + class name); omitting the namespace made
/// same-named unions in different namespaces collide on a single ".g.cs" hint, which the
/// host resolved by dropping or duplicating sources and broke consuming projects.
/// </summary>
public class SourceHintCollisionTests
{
    [Fact]
    public void SameNameUnionsInDifferentNamespaces_DoNotCollide()
    {
        var result = GeneratorTestHelper.Run(TestSources.SameNameUnionsInDifferentNamespaces);

        GeneratorTestHelper.AssertNoErrors(result);

        Assert.Contains(
            "Modules.Economy.Domain.Construction.Aggregates.SectorEvolver.g.cs",
            result.GeneratedSources.Keys);
        Assert.Contains(
            "Modules.Economy.Domain.Trade.Aggregates.SectorEvolver.g.cs",
            result.GeneratedSources.Keys);
    }

    [Fact]
    public void SameNameNestedUnionsInDifferentNamespaces_DoNotCollide()
    {
        var result = GeneratorTestHelper.Run(TestSources.SameNameNestedUnionsInDifferentNamespaces);

        GeneratorTestHelper.AssertNoErrors(result);

        Assert.Contains(
            "Modules.Economy.Domain.Construction.Aggregates.Sector.Evolver.g.cs",
            result.GeneratedSources.Keys);
        Assert.Contains(
            "Modules.Economy.Domain.Trade.Aggregates.Sector.Evolver.g.cs",
            result.GeneratedSources.Keys);
    }

    [Fact]
    public void EachColludingUnionEmitsExactlyOneSource()
    {
        var result = GeneratorTestHelper.Run(TestSources.SameNameUnionsInDifferentNamespaces);

        // One file per union, no duplicate "SectorEvolver.g.cs" entries.
        Assert.Equal(2, result.GeneratedSources.Count);
    }
}
