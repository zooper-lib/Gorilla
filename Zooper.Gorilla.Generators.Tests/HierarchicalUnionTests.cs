using Microsoft.CodeAnalysis;
using Xunit;

namespace Zooper.Gorilla.Generators.Tests;

public class HierarchicalUnionTests
{
    [Fact]
    public void AbstractRecordUnion_GeneratesFactoryMethodsAndMatch()
    {
        var result = GeneratorTestHelper.Run(TestSources.AbstractHierarchicalUnion);

        GeneratorTestHelper.AssertNoErrors(result);
        Assert.Contains("public T Match<T>(", result.GeneratedSources["ContractOutcome.g.cs"]);
    }

    [Fact]
    public void InnerSubUnion_IsIncludedInOuterMatch()
    {
        var result = GeneratorTestHelper.Run(TestSources.AbstractHierarchicalUnion);

        GeneratorTestHelper.AssertNoErrors(result);
        Assert.Contains("Func<Rejected, T> rejected", result.GeneratedSources["ContractOutcome.g.cs"]);
    }

    [Fact]
    public void InnerSubUnionValue_FlowsThroughOuterMatch()
    {
        var result = GeneratorTestHelper.Run(TestSources.AbstractHierarchicalUnion);
        GeneratorTestHelper.AssertNoErrors(result);

        var assembly = GeneratorTestHelper.EmitToAssembly(result);
        var output = GeneratorTestHelper.InvokeStaticStringMethod(assembly, "Usage", "DispatchSubUnionThroughOuter");

        Assert.Equal("validation:field", output);
    }

    [Fact]
    public void DoubleNestedHierarchy_CompilesAndDispatches()
    {
        var result = GeneratorTestHelper.Run(TestSources.AbstractHierarchicalUnion);
        GeneratorTestHelper.AssertNoErrors(result);

        var assembly = GeneratorTestHelper.EmitToAssembly(result);
        var output = GeneratorTestHelper.InvokeStaticStringMethod(assembly, "Usage", "DoubleNested");

        Assert.Equal("expired", output);
    }

    [Fact]
    public void NonPartialContainingType_EmitsZGOR002()
    {
        var result = GeneratorTestHelper.Run(TestSources.NonPartialContainingType);

        var warning = Assert.Single(result.AllDiagnostics.Where(static diagnostic => diagnostic.Id == "ZGOR002"));
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
    }
}
