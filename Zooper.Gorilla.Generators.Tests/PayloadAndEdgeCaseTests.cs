using Xunit;

namespace Zooper.Gorilla.Generators.Tests;

/// <summary>
/// Shapes a consuming codebase actually produces: payloads richer than string/bool/int, unions
/// carrying other unions, configured discriminator names, and the degenerate declarations.
/// </summary>
public class PayloadAndEdgeCaseTests
{
    private static string Run(string source, string method)
    {
        var result = GeneratorTestHelper.Run(source);
        GeneratorTestHelper.AssertNoErrors(result);
        var assembly = GeneratorTestHelper.EmitToAssembly(result);
        return GeneratorTestHelper.InvokeStaticStringMethod(assembly, "Usage", method);
    }

    [Fact]
    public void UnionWithNoVariantsAndNoSubUnions_EmitsCompilableSource()
    {
        var result = GeneratorTestHelper.Run(TestSources.VariantlessUnion);

        GeneratorTestHelper.AssertNoErrors(result);
    }

    [Fact]
    public void UnionWithOnlySubUnions_MatchesSwitchesAndRoundTrips()
        => Assert.Equal(
            "{\"$type\":\"Validation\",\"field\":\"email\"}|email|rejected",
            Run(TestSources.SubUnionsOnlyUnion, "MatchAndRoundTrip"));

    [Fact]
    public void Stj_CollectionAndEnumPayloads_RoundTrip()
        => Assert.Equal(
            "{\"$type\":\"Tagged\",\"tags\":[\"a\",\"b\"],\"severity\":1}|tagged:a+b:High",
            Run(TestSources.PayloadTypes, "StjCollectionAndEnum"));

    [Fact]
    public void Stj_NullPayload_RoundTrips()
        => Assert.Equal(
            "{\"$type\":\"Optional\",\"note\":null}|optional:<null>",
            Run(TestSources.PayloadTypes, "StjNullPayload"));

    [Fact]
    public void Stj_UnionCarryingAnotherUnion_RoundTrips()
        => Assert.Equal(
            "{\"$type\":\"Wrapped\",\"payload\":{\"$type\":\"Leaf\",\"value\":7}}|wrapped:7",
            Run(TestSources.PayloadTypes, "StjUnionInsideUnion"));

    [Fact]
    public void Newtonsoft_CollectionAndEnumPayloads_RoundTrip()
        => Assert.Equal(
            "{\"$type\":\"Tagged\",\"tags\":[\"a\",\"b\"],\"severity\":1}|tagged:a+b:High",
            Run(TestSources.PayloadTypes, "NewtonsoftCollectionAndEnum"));

    [Fact]
    public void Newtonsoft_UnionCarryingAnotherUnion_RoundTrips()
        => Assert.Equal(
            "{\"$type\":\"Wrapped\",\"payload\":{\"$type\":\"Leaf\",\"value\":7}}|wrapped:7",
            Run(TestSources.PayloadTypes, "NewtonsoftUnionInsideUnion"));

    [Fact]
    public void Newtonsoft_NullToken_ReadsAsNull()
        => Assert.Equal("null", Run(TestSources.PayloadTypes, "NewtonsoftNullValue"));

    [Fact]
    public void Stj_ConfiguredDiscriminatorFieldName_RoundTrips()
        => Assert.Equal(
            "{\"kind\":\"Alpha\",\"name\":\"first\"}|alpha:first",
            Run(TestSources.ConfiguredDiscriminatorRoundTrip, "StjRoundTrip"));

    [Fact]
    public void Newtonsoft_ConfiguredDiscriminatorFieldName_RoundTrips()
        => Assert.Equal(
            "{\"kind\":\"Alpha\",\"name\":\"first\"}|alpha:first",
            Run(TestSources.ConfiguredDiscriminatorRoundTrip, "NewtonsoftRoundTrip"));

    [Fact]
    public void Stj_DiscriminatorValueMatchIsCaseInsensitive()
        => Assert.Equal("card:4111", Run(TestSources.ConfiguredDiscriminatorRoundTrip, "StjDiscriminatorIsCaseInsensitive"));

    [Fact]
    public void Newtonsoft_DiscriminatorValueMatchIsCaseInsensitive()
        => Assert.Equal("card:4111", Run(TestSources.ConfiguredDiscriminatorRoundTrip, "NewtonsoftDiscriminatorIsCaseInsensitive"));

    [Fact]
    public void Newtonsoft_FieldlessVariants_RoundTrip()
        => Assert.Equal(
            "{\"$type\":\"InvalidCredentials\"}|invalidCredentials",
            Run(TestSources.NewtonsoftEdgeCases, "FieldlessRoundTrip"));

    [Fact]
    public void Newtonsoft_VariantsWithIdenticalShapes_RoundTrip()
        => Assert.Equal(
            "{\"$type\":\"Memo\",\"body\":\"hi\"}|memo:hi",
            Run(TestSources.NewtonsoftEdgeCases, "IdenticalShapesRoundTrip"));

    [Fact]
    public void Newtonsoft_NullValue_SerializesToNull()
        => Assert.Equal("null", Run(TestSources.NewtonsoftEdgeCases, "NullSerializesToNull"));
}
