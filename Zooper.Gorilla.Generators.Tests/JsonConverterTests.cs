using Xunit;

namespace Zooper.Gorilla.Generators.Tests;

public class JsonConverterTests
{
    [Fact]
    public void NestedSealedUnion_SystemTextJson_RoundTrips()
    {
        var result = GeneratorTestHelper.Run(TestSources.NestedSealedJsonRoundTrip);
        GeneratorTestHelper.AssertNoErrors(result);

        var assembly = GeneratorTestHelper.EmitToAssembly(result);
        var output = GeneratorTestHelper.InvokeStaticStringMethod(assembly, "Usage", "RoundTrip");

        Assert.Equal("alpha:True", output);
    }

    [Fact]
    public void OuterAbstractUnion_LeafVariant_SystemTextJson_RoundTrips()
    {
        var result = GeneratorTestHelper.Run(TestSources.HierarchicalJsonRoundTrip);
        GeneratorTestHelper.AssertNoErrors(result);

        var assembly = GeneratorTestHelper.EmitToAssembly(result);
        var output = GeneratorTestHelper.InvokeStaticStringMethod(assembly, "Usage", "RoundTripLeaf");

        Assert.Equal("c-42", output);
    }

    [Fact]
    public void OuterAbstractUnion_SubUnion_SystemTextJson_RoundTrips()
    {
        var result = GeneratorTestHelper.Run(TestSources.HierarchicalJsonRoundTrip);
        GeneratorTestHelper.AssertNoErrors(result);

        var assembly = GeneratorTestHelper.EmitToAssembly(result);
        var output = GeneratorTestHelper.InvokeStaticStringMethod(assembly, "Usage", "RoundTripSubUnion");

        Assert.Equal("expired", output);
    }

    [Fact]
    public void InnerSubUnion_NewtonsoftJson_RoundTrips()
    {
        var result = GeneratorTestHelper.Run(TestSources.HierarchicalJsonRoundTrip);
        GeneratorTestHelper.AssertNoErrors(result);

        var assembly = GeneratorTestHelper.EmitToAssembly(result);
        var output = GeneratorTestHelper.InvokeStaticStringMethod(assembly, "Usage", "RoundTripInnerNewtonsoft");

        Assert.Equal("expired", output);
    }
}
