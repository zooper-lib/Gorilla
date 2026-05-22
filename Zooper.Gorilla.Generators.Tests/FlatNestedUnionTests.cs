using Xunit;

namespace Zooper.Gorilla.Generators.Tests;

public class FlatNestedUnionTests
{
    [Fact]
    public void TopLevelFlatUnion_GeneratesCorrectly()
    {
        var result = GeneratorTestHelper.Run(TestSources.TopLevelFlatUnion);

        GeneratorTestHelper.AssertNoErrors(result);
        Assert.Contains("EntityState.g.cs", result.GeneratedSources.Keys);
    }

    [Fact]
    public void NestedUnionInsideClass_GeneratesFactoryMethodsAndMatch()
    {
        var result = GeneratorTestHelper.Run(TestSources.NestedUnionInsideClass);

        GeneratorTestHelper.AssertNoErrors(result);
        Assert.Contains("Container.State.g.cs", result.GeneratedSources.Keys);
    }

    [Fact]
    public void NestedUnionInsideInterface_GeneratesFactoryMethodsAndMatch()
    {
        var result = GeneratorTestHelper.Run(TestSources.NestedUnionInsideInterface);

        GeneratorTestHelper.AssertNoErrors(result);
        Assert.Contains("IContainer.State.g.cs", result.GeneratedSources.Keys);
    }

    [Fact]
    public void DeeplyNestedUnion_GeneratesCorrectly()
    {
        var result = GeneratorTestHelper.Run(TestSources.DeeplyNestedUnion);

        GeneratorTestHelper.AssertNoErrors(result);
        Assert.Contains("IOuter.IInner.V1.g.cs", result.GeneratedSources.Keys);
    }

    [Fact]
    public void SameShortNameNestedUnions_UseDistinctSourceHints()
    {
        var result = GeneratorTestHelper.Run(TestSources.DistinctHintNames);

        GeneratorTestHelper.AssertNoErrors(result);
        Assert.Contains("IContractA.V1.g.cs", result.GeneratedSources.Keys);
        Assert.Contains("IContractB.V1.g.cs", result.GeneratedSources.Keys);
    }

    [Fact]
    public void NestedUnionImplementingContainingInterface_Compiles()
    {
        var result = GeneratorTestHelper.Run(TestSources.NestedImplementsContainingInterface);

        GeneratorTestHelper.AssertNoErrors(result);
    }

    [Fact]
    public void PrimaryNestedExample_CompilesWithMatch()
    {
        var result = GeneratorTestHelper.Run(TestSources.PrimaryNestedExample);

        GeneratorTestHelper.AssertNoErrors(result);
    }
}
