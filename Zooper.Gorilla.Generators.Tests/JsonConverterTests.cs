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

    [Fact]
    public void Stj_CamelCase_FlatMultiParam_RoundTrips()
        => Assert.Equal("box:True", RunOptionsAware("StjCamelRoundTrip"));

    [Fact] // naming policy actually transforms the key off the PascalCase property
    public void Stj_SnakeCase_TransformsKeyAndKeepsDiscriminatorLiteral()
    {
        var json = RunOptionsAware("StjSnakeKey");
        Assert.Contains("\"is_visible\":true", json);
        Assert.Contains("\"label\":\"box\"", json);
        Assert.Contains("\"$type\":\"Rectangle\"", json); // discriminator unaffected
        Assert.DoesNotContain("isVisible", json);
    }

    [Fact]
    public void Stj_NamingPolicy_Hierarchical_RoundTrips()
        => Assert.Equal("validation:email", RunOptionsAware("StjHierarchicalRoundTrip"));

    [Fact]
    public void Stj_CaseInsensitive_MixedCaseKeys_Deserialize()
        => Assert.Equal("box:True", RunOptionsAware("StjCaseInsensitive"));

    [Fact]
    public void Newtonsoft_CamelCaseResolver_Flat_RoundTrips()
        => Assert.Equal("box:True", RunOptionsAware("NewtonsoftCamelRoundTrip"));

    [Fact]
    public void Newtonsoft_CamelCaseResolver_Hierarchical_RoundTrips()
        => Assert.Equal("validation:email", RunOptionsAware("NewtonsoftHierarchicalRoundTrip"));

    [Fact] // resolver actually transforms the key off the PascalCase property
    public void Newtonsoft_SnakeCase_TransformsKeyAndKeepsDiscriminatorLiteral()
    {
        var json = RunOptionsAware("NewtonsoftSnakeKey");
        Assert.Contains("\"is_visible\":true", json);
        Assert.Contains("\"label\":\"box\"", json);
        Assert.Contains("\"$type\":\"Rectangle\"", json); // discriminator unaffected
        Assert.DoesNotContain("isVisible", json);
    }

    [Fact] // byte-identical default output (System.Text.Json)
    public void Stj_DefaultOptions_OutputUnchanged()
        => Assert.Equal("{\"$type\":\"Rectangle\",\"label\":\"box\",\"isVisible\":true}", RunOptionsAware("StjDefaultJson"));

    [Fact] // byte-identical default output (Newtonsoft)
    public void Newtonsoft_DefaultResolver_OutputUnchanged()
        => Assert.Equal("{\"$type\":\"Rectangle\",\"label\":\"box\",\"isVisible\":true}", RunOptionsAware("NewtonsoftDefaultJson"));

    // Every test above serializes through the union's declared type. A value written by its runtime
    // type resolves [JsonConverter] on the variant instead, and without the attribute there it is
    // written by default logic with no discriminator — nothing can read the payload back.

    private static string RunRuntimeTypeDiscriminator(string method)
    {
        var result = GeneratorTestHelper.Run(GenericUnionSources.RuntimeTypeDiscriminator);
        GeneratorTestHelper.AssertNoErrors(result);
        return GeneratorTestHelper.InvokeStaticStringMethod(GeneratorTestHelper.EmitToAssembly(result), "Usage", method);
    }

    [Fact]
    public void Stj_NonGenericUnion_ThroughVariantStaticType_KeepsDiscriminator()
        => Assert.Equal("{\"$type\":\"Visible\",\"value\":\"hello\"}", RunRuntimeTypeDiscriminator("ThroughVariantStaticType"));

    [Fact]
    public void Stj_NonGenericUnion_ThroughObject_KeepsDiscriminator()
        => Assert.Equal("{\"$type\":\"Visible\",\"value\":\"hello\"}", RunRuntimeTypeDiscriminator("ThroughObject"));

    [Fact] // {} would be indistinguishable from any other payload-free variant
    public void Stj_PayloadFreeVariant_ThroughVariantStaticType_KeepsDiscriminator()
        => Assert.Equal("{\"$type\":\"NotProvided\"}", RunRuntimeTypeDiscriminator("PayloadFreeThroughVariantStaticType"));

    [Fact]
    public void Newtonsoft_NonGenericUnion_ThroughObject_KeepsDiscriminator()
        => Assert.Equal("{\"$type\":\"Visible\",\"value\":\"hello\"}", RunRuntimeTypeDiscriminator("NewtonsoftThroughObject"));

    [Fact]
    public void NewtonsoftConverterAttribute_IsNotEmittedOnVariantClasses()
    {
        var result = GeneratorTestHelper.Run(GenericUnionSources.RuntimeTypeDiscriminator);
        var source = result.GeneratedSources["Plain.g.cs"];

        // Once, on the union declaration. Newtonsoft honours it by inheritance; duplicating it would
        // make the converter resolve objectType as the variant and return a union instance.
        Assert.Equal(1, source.Split("[Newtonsoft.Json.JsonConverterAttribute(").Length - 1);
        // Three times for System.Text.Json: the union and both variants.
        Assert.Equal(3, source.Split("[System.Text.Json.Serialization.JsonConverter(").Length - 1);
    }

    private static string RunGenericUnionJson(string method)
    {
        var result = GeneratorTestHelper.Run(GenericUnionSources.GenericUnionJson);
        GeneratorTestHelper.AssertNoErrors(result);
        return GeneratorTestHelper.InvokeStaticStringMethod(GeneratorTestHelper.EmitToAssembly(result), "Usage", method);
    }

    [Fact]
    public void Stj_GenericUnion_StringPayload_RoundTrips()
        => Assert.Equal("{\"$type\":\"Visible\",\"value\":\"hello\"}|visible:hello|True", RunGenericUnionJson("StjStringRoundTrip"));

    [Fact]
    public void Stj_GenericUnion_IntPayload_RoundTrips()
        => Assert.Equal("{\"$type\":\"Visible\",\"value\":42}|visible:42|True", RunGenericUnionJson("StjIntRoundTrip"));

    [Fact] // identical wire format to System.Text.Json
    public void Newtonsoft_GenericUnion_StringPayload_RoundTrips()
        => Assert.Equal("{\"$type\":\"Visible\",\"value\":\"hello\"}|visible:hello|True", RunGenericUnionJson("NewtonsoftStringRoundTrip"));

    [Fact]
    public void Newtonsoft_GenericUnion_IntPayload_RoundTrips()
        => Assert.Equal("{\"$type\":\"Visible\",\"value\":42}|visible:42|True", RunGenericUnionJson("NewtonsoftIntRoundTrip"));

    [Fact] // the factory resolves the closed union from the variant's base types
    public void Stj_GenericUnion_ThroughVariantStaticType_KeepsDiscriminator()
        => Assert.Equal("{\"$type\":\"Visible\",\"value\":\"hello\"}", RunGenericUnionJson("StjThroughVariantStaticType"));

    [Fact]
    public void Stj_GenericUnion_ThroughObject_KeepsDiscriminator()
        => Assert.Equal("{\"$type\":\"Visible\",\"value\":\"hello\"}", RunGenericUnionJson("StjThroughObject"));

    [Fact] // a generic-type-definition comparison would answer false for the variant
    public void NewtonsoftShim_CanConvert_AcceptsTheUnionAndItsVariants()
        => Assert.Equal("True:True:False", RunGenericUnionJson("ShimCanConvert"));

    [Fact]
    public void NewtonsoftShim_ManuallyRegistered_SerializesAVariantWithItsDiscriminator()
        => Assert.Equal("{\"$type\":\"Visible\",\"value\":\"hello\"}", RunGenericUnionJson("ShimManualRegistration"));

    [Fact]
    public void Stj_GenericUnion_AsDtoProperty_RoundTrips()
        => Assert.Equal("{\"Payload\":{\"$type\":\"Visible\",\"value\":\"in-dto\"}}|visible:in-dto", RunGenericUnionJson("DtoPropertyRoundTrip"));

    private static string RunUnionsInGenericContainers(string method)
    {
        var result = GeneratorTestHelper.Run(GenericUnionSources.UnionsInGenericContainers);
        GeneratorTestHelper.AssertNoErrors(result);
        return GeneratorTestHelper.InvokeStaticStringMethod(GeneratorTestHelper.EmitToAssembly(result), "Usage", method);
    }

    [Fact]
    public void Stj_NonGenericUnionInGenericContainer_RoundTrips()
        => Assert.Equal("{\"$type\":\"Tagged\",\"tag\":\"t\"}|t", RunUnionsInGenericContainers("NonGenericLeafRoundTrip"));

    [Fact]
    public void Stj_GenericUnionInGenericContainer_RoundTrips()
        => Assert.Equal("{\"$type\":\"Pair\",\"key\":7,\"value\":\"v\"}|7:v", RunUnionsInGenericContainers("GenericLeafStjRoundTrip"));

    [Fact]
    public void Newtonsoft_GenericUnionInGenericContainer_RoundTrips()
        => Assert.Equal("{\"$type\":\"Pair\",\"key\":7,\"value\":\"v\"}|7:v", RunUnionsInGenericContainers("GenericLeafNewtonsoftRoundTrip"));
}
