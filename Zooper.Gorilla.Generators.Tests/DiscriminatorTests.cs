using Xunit;

namespace Zooper.Gorilla.Generators.Tests;

/// <summary>
/// The discriminator field is required, and the two ways a payload can be unrecognized —
/// missing field, unknown value — are distinct errors that survive the sub-union search.
/// </summary>
public class DiscriminatorTests
{
    private static string Run(string source, string method)
    {
        var result = GeneratorTestHelper.Run(source);
        GeneratorTestHelper.AssertNoErrors(result);
        var assembly = GeneratorTestHelper.EmitToAssembly(result);
        return GeneratorTestHelper.InvokeStaticStringMethod(assembly, "Usage", method);
    }

    private static string Failure(string method) => Run(TestSources.DeserializationFailures, method);

    private static string EdgeCase(string method) => Run(TestSources.RoundTripEdgeCases, method);

    [Fact]
    public void Stj_MissingDiscriminator_NamesTheFieldAndUnion()
    {
        var message = Failure("StjMissing");

        Assert.Contains("Missing discriminator field '$type'", message);
        Assert.Contains("Pay", message);
    }

    [Fact]
    public void Stj_UnrecognizedDiscriminator_NamesTheValueAndUnion()
    {
        var message = Failure("StjUnrecognized");

        Assert.Contains("Nonexistent", message);
        Assert.Contains("Pay", message);
    }

    [Fact]
    public void Stj_MissingDiscriminator_SurvivesTheSubUnionSearch()
    {
        var message = Failure("StjMissingWithSubUnions");

        Assert.Contains("Missing discriminator field '$type'", message);
        Assert.Contains("Outcome", message);
    }

    [Fact]
    public void Stj_UnrecognizedDiscriminator_SurvivesTheSubUnionSearch()
    {
        var message = Failure("StjUnrecognizedWithSubUnions");

        Assert.Contains("Nonexistent", message);
        Assert.Contains("Outcome", message);
    }

    [Fact]
    public void Stj_ConfiguredDiscriminatorFieldName_IsNamedInTheMessage()
    {
        var message = Failure("StjConfiguredFieldName");

        Assert.Contains("'kind'", message);
        Assert.DoesNotContain("$type", message);
    }

    [Fact]
    public void Stj_ForeignObject_IsNotSilentlyMisidentified()
        => Assert.Contains("Missing discriminator field '$type'", Failure("StjForeignObject"));

    [Fact]
    public void Newtonsoft_MissingDiscriminator_NamesTheFieldAndUnion()
    {
        var message = Failure("NewtonsoftMissing");

        Assert.Contains("Missing discriminator field '$type'", message);
        Assert.Contains("Pay", message);
    }

    [Fact]
    public void Newtonsoft_UnrecognizedDiscriminator_NamesTheValueAndUnion()
    {
        var message = Failure("NewtonsoftUnrecognized");

        Assert.Contains("Nonexistent", message);
        Assert.Contains("Pay", message);
    }

    [Fact]
    public void Newtonsoft_MissingDiscriminator_SurvivesTheSubUnionSearch()
        => Assert.Contains("Missing discriminator field '$type'", Failure("NewtonsoftMissingWithSubUnions"));

    [Fact]
    public void SubUnionDiscriminator_StillResolves()
        => Assert.Equal("rejected:email", Failure("SubUnionDiscriminatorResolves"));

    [Fact]
    public void FieldlessVariants_RoundTrip()
        => Assert.Equal("{\"$type\":\"InvalidCredentials\"}|invalidCredentials", EdgeCase("FieldlessRoundTrip"));

    [Fact]
    public void VariantsWithIdenticalShapes_RoundTrip()
        => Assert.Equal("{\"$type\":\"Memo\",\"body\":\"hi\"}|memo:hi", EdgeCase("IdenticalShapesRoundTrip"));

    [Fact]
    public void SupersetVariant_IsReadable()
        => Assert.Equal("cardWithCvv:4111:123", EdgeCase("SupersetVariantReads"));

    [Fact]
    public void PayloadVariant_SerializesIdentically()
        => Assert.Equal("{\"$type\":\"Card\",\"number\":\"4111\"}", EdgeCase("GoldenPayloadVariant"));

    [Fact]
    public void FieldlessVariant_SerializesIdentically()
        => Assert.Equal("{\"$type\":\"Cash\"}", EdgeCase("GoldenFieldlessVariant"));

    [Fact]
    public void StoredDocument_StillDeserializes()
        => Assert.Equal("card:4111", EdgeCase("StoredDocumentStillReads"));
}
