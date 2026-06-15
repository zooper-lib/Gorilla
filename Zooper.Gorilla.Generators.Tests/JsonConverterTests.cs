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

    private static string RunOptionsAware(string method)
    {
        var result = GeneratorTestHelper.Run(TestSources.OptionsAwareConverters);
        GeneratorTestHelper.AssertNoErrors(result);
        var assembly = GeneratorTestHelper.EmitToAssembly(result);
        return GeneratorTestHelper.InvokeStaticStringMethod(assembly, "Usage", method);
    }

    [Fact] // 4.1
    public void Stj_CamelCase_FlatMultiParam_RoundTrips()
        => Assert.Equal("box:True", RunOptionsAware("StjCamelRoundTrip"));

    [Fact] // 4.1 — naming policy actually transforms the key off the PascalCase property
    public void Stj_SnakeCase_TransformsKeyAndKeepsDiscriminatorLiteral()
    {
        var json = RunOptionsAware("StjSnakeKey");
        Assert.Contains("\"is_visible\":true", json);
        Assert.Contains("\"label\":\"box\"", json);
        Assert.Contains("\"$type\":\"Rectangle\"", json); // 4.8 discriminator unaffected
        Assert.DoesNotContain("isVisible", json);
    }

    [Fact] // 4.2
    public void Stj_NamingPolicy_Hierarchical_RoundTrips()
        => Assert.Equal("validation:email", RunOptionsAware("StjHierarchicalRoundTrip"));

    [Fact] // 4.3
    public void Stj_CaseInsensitive_MixedCaseKeys_Deserialize()
        => Assert.Equal("box:True", RunOptionsAware("StjCaseInsensitive"));

    [Fact] // 4.4
    public void Stj_Inference_UnderCamelCase_NoDiscriminator()
        => Assert.Equal("box:True", RunOptionsAware("StjInferenceCamel"));

    [Fact] // 4.5
    public void Newtonsoft_CamelCaseResolver_Flat_RoundTrips()
        => Assert.Equal("box:True", RunOptionsAware("NewtonsoftCamelRoundTrip"));

    [Fact] // 4.5
    public void Newtonsoft_CamelCaseResolver_Hierarchical_RoundTrips()
        => Assert.Equal("validation:email", RunOptionsAware("NewtonsoftHierarchicalRoundTrip"));

    [Fact] // 4.5 — resolver actually transforms the key off the PascalCase property
    public void Newtonsoft_SnakeCase_TransformsKeyAndKeepsDiscriminatorLiteral()
    {
        var json = RunOptionsAware("NewtonsoftSnakeKey");
        Assert.Contains("\"is_visible\":true", json);
        Assert.Contains("\"label\":\"box\"", json);
        Assert.Contains("\"$type\":\"Rectangle\"", json); // 4.8 discriminator unaffected
        Assert.DoesNotContain("isVisible", json);
    }

    [Fact] // 4.6
    public void Newtonsoft_Inference_UnderCamelCase_NoDiscriminator()
        => Assert.Equal("box:True", RunOptionsAware("NewtonsoftInferenceCamel"));

    [Fact] // 4.7 — byte-identical default output (System.Text.Json)
    public void Stj_DefaultOptions_OutputUnchanged()
        => Assert.Equal("{\"$type\":\"Rectangle\",\"label\":\"box\",\"isVisible\":true}", RunOptionsAware("StjDefaultJson"));

    [Fact] // 4.7 — byte-identical default output (Newtonsoft)
    public void Newtonsoft_DefaultResolver_OutputUnchanged()
        => Assert.Equal("{\"$type\":\"Rectangle\",\"label\":\"box\",\"isVisible\":true}", RunOptionsAware("NewtonsoftDefaultJson"));
}
