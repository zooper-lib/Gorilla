using Microsoft.CodeAnalysis;
using Xunit;

namespace Zooper.Gorilla.Generators.Tests;

/// <summary>
/// A union is an abstract base type with sealed nested variant subtypes, a private constructor
/// closing the hierarchy, and equality left to the declared keyword.
/// </summary>
public class UnionRepresentationTests
{
    private static string Run(string source, string method)
    {
        var result = GeneratorTestHelper.Run(source);
        GeneratorTestHelper.AssertNoErrors(result);
        var assembly = GeneratorTestHelper.EmitToAssembly(result);
        return GeneratorTestHelper.InvokeStaticStringMethod(assembly, "Usage", method);
    }

    /// <summary>
    /// Nothing is generated for equality or ToString, so both follow the declared keyword.
    /// The synthesized ToString lists the union's public properties, which now includes the
    /// generated IsX accessors alongside the payload — the type name is not repeated.
    /// </summary>
    [Fact]
    public void RecordUnion_HasValueEqualityAndSynthesizedToString()
        => Assert.Equal(
            "True|True|CardVariant { IsCard = True, Number = 4111 }",
            Run(TestSources.EqualitySemantics, "RecordEquality"));

    [Fact]
    public void ClassUnion_KeepsReferenceEquality()
        => Assert.Equal("False|True", Run(TestSources.EqualitySemantics, "ClassEquality"));

    [Fact]
    public void GeneratedSource_ReferencesNoOneOf()
    {
        var result = GeneratorTestHelper.Run(TestSources.TopLevelFlatUnion);

        GeneratorTestHelper.AssertNoErrors(result);
        var source = result.GeneratedSources["EntityState.g.cs"];
        Assert.DoesNotContain("OneOf", source);
    }

    [Fact]
    public void ExternalSubtype_FailsToCompile()
    {
        var result = GeneratorTestHelper.Run(TestSources.ExternalSubtype);

        Assert.Contains(result.Errors, static diagnostic => diagnostic.Id == "CS0122");
    }

    [Fact]
    public void SealedUnion_ReportsZgor003()
    {
        var result = GeneratorTestHelper.Run(TestSources.NonAbstractUnion);

        var diagnostic = Assert.Single(result.AllDiagnostics.Where(static d => d.Id == "ZGOR003"));
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("EntityState", diagnostic.GetMessage());
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void PlainPartialUnion_ReportsZgor003()
    {
        var result = GeneratorTestHelper.Run(TestSources.PlainPartialUnion);

        Assert.Contains(result.AllDiagnostics, static d => d.Id == "ZGOR003");
    }

    [Fact]
    public void AbstractUnion_ReportsNoZgor003()
    {
        var result = GeneratorTestHelper.Run(TestSources.AbstractHierarchicalUnion);

        Assert.DoesNotContain(result.AllDiagnostics, static d => d.Id == "ZGOR003");
    }

    [Fact]
    public void VariantNameCollidingWithAccessor_FailsToCompile()
    {
        var result = GeneratorTestHelper.Run(TestSources.AccessorNameCollision);

        Assert.Contains(result.Errors, static diagnostic => diagnostic.Id == "CS0102");
    }

    [Fact]
    public void StructUnion_IsRejected()
    {
        var result = GeneratorTestHelper.Run(TestSources.StructUnion);

        // The attribute targets classes, so a struct declaration never reaches the generator.
        Assert.Contains(result.Errors, static diagnostic => diagnostic.Id == "CS0592");
        Assert.Empty(result.GeneratedSources);
    }
}
