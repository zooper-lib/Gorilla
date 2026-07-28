using Microsoft.CodeAnalysis;
using Xunit;

namespace Zooper.Gorilla.Generators.Tests;

public class GenericUnionTests
{
    private static string RunGenericUnions(string method)
    {
        var result = GeneratorTestHelper.Run(GenericUnionSources.GenericUnions);
        GeneratorTestHelper.AssertNoErrors(result);
        return GeneratorTestHelper.InvokeStaticStringMethod(GeneratorTestHelper.EmitToAssembly(result), "Usage", method);
    }

    [Fact]
    public void GenericUnion_FactoryAndMatch_Dispatch()
        => Assert.Equal("visible:x", RunGenericUnions("MatchVisible"));

    [Fact] // payload is a construction over the type parameter, not the parameter itself
    public void GenericUnion_ListPayload_Dispatches()
        => Assert.Equal("many:3", RunGenericUnions("MatchListPayload"));

    [Fact]
    public void GenericUnion_PayloadFreeVariant_Dispatches()
        => Assert.Equal("notProvided", RunGenericUnions("MatchPayloadFree"));

    [Fact]
    public void GenericUnion_Switch_Dispatches()
        => Assert.Equal("visible:y", RunGenericUnions("SwitchVisible"));

    [Fact]
    public void GenericUnion_Accessors_Work()
        => Assert.Equal("True:z:False", RunGenericUnions("Accessors"));

    [Fact]
    public void TwoTypeParameters_PreserveDeclaredOrder()
        => Assert.Equal("ok:7|fail:bad", RunGenericUnions("ResultArgumentOrder"));

    [Fact]
    public void GenericUnion_CompilesWarningClean()
    {
        var result = GeneratorTestHelper.Run(GenericUnionSources.GenericUnions);

        GeneratorTestHelper.AssertNoErrors(result);
        GeneratorTestHelper.AssertGeneratedOutputHasNoWarnings(result);
    }

    [Fact]
    public void GenericUnion_SelfReferencesCarryTypeArguments_ConstructorStaysBare()
    {
        var result = GeneratorTestHelper.Run(GenericUnionSources.GenericUnions);
        var source = result.GeneratedSources["Disclosure_1.g.cs"];

        Assert.Contains("public abstract partial record Disclosure<T>", source);
        Assert.Contains("public static partial Disclosure<T> Visible(T value)", source);
        // An identifier here, not a type.
        Assert.Contains("private Disclosure() { }", source);
    }

    [Fact]
    public void GenericUnion_VariantClass_DeclaresNoTypeParameterListOfItsOwn()
    {
        var result = GeneratorTestHelper.Run(GenericUnionSources.GenericUnions);
        var source = result.GeneratedSources["Disclosure_1.g.cs"];

        Assert.Contains("public sealed record VisibleVariant : Disclosure<T>", source);
        Assert.DoesNotContain("VisibleVariant<", source);
        Assert.DoesNotContain("record VisibleVariant : Disclosure<T> where", source);
    }

    [Fact] // D9 and D12: no companion factory class, no covariant interface, no mapping method
    public void GenericUnion_EmitsNoCompanionClassAndNoVarianceHelpers()
    {
        var result = GeneratorTestHelper.Run(GenericUnionSources.GenericUnions);
        var source = result.GeneratedSources["Disclosure_1.g.cs"];

        Assert.DoesNotContain("static class Disclosure", source);
        Assert.DoesNotContain("interface IDisclosure", source);
        Assert.DoesNotContain("Select<", source);
    }

    [Fact]
    public void NonGenericUnion_MatchResultParameterIsTResult()
    {
        var result = GeneratorTestHelper.Run(TestSources.AbstractHierarchicalUnion);

        GeneratorTestHelper.AssertNoErrors(result);
        Assert.Contains("public TResult Match<TResult>(", result.GeneratedSources["ContractOutcome.g.cs"]);
    }

    [Fact]
    public void GenericUnion_MatchResultParameterDoesNotShadowOwnParameter()
    {
        var result = GeneratorTestHelper.Run(GenericUnionSources.GenericUnions);

        Assert.Contains("public TResult Match<TResult>(", result.GeneratedSources["Disclosure_1.g.cs"]);
        GeneratorTestHelper.AssertGeneratedOutputHasNoWarnings(result);
    }

    [Fact]
    public void UnionDeclaringTResult_UniquifiesTheResultParameter()
    {
        var result = GeneratorTestHelper.Run(GenericUnionSources.ResultTypeParameterCollisions);

        GeneratorTestHelper.AssertNoErrors(result);
        // CS0693 would fire on a re-used name, and is a warning, not an error.
        GeneratorTestHelper.AssertGeneratedOutputHasNoWarnings(result);
        Assert.Contains("public TResult1 Match<TResult1>(", result.GeneratedSources["Box_1.g.cs"]);
        Assert.Equal("filled:a", GeneratorTestHelper.InvokeStaticStringMethod(
            GeneratorTestHelper.EmitToAssembly(result), "Usage", "BoxMatch"));
    }

    [Fact] // the taken-name set must include the containing types' parameters, not only the union's own
    public void UnionNestedInGenericContainerNamedTResult_UniquifiesTheResultParameter()
    {
        var result = GeneratorTestHelper.Run(GenericUnionSources.ResultTypeParameterCollisions);

        GeneratorTestHelper.AssertNoErrors(result);
        Assert.Contains("public TResult1 Match<TResult1>(", result.GeneratedSources["Holder_1.Leaf.g.cs"]);
        Assert.Equal("one:5", GeneratorTestHelper.InvokeStaticStringMethod(
            GeneratorTestHelper.EmitToAssembly(result), "Usage", "LeafMatch"));
    }

    [Fact]
    public void SameNameDifferentArity_DoNotCollideOnTheSourceHint()
    {
        var result = GeneratorTestHelper.Run(GenericUnionSources.SameNameDifferentArity);

        GeneratorTestHelper.AssertNoErrors(result);
        Assert.Contains("Sample.Foo.g.cs", result.GeneratedSources.Keys);
        Assert.Contains("Sample.Foo_1.g.cs", result.GeneratedSources.Keys);
        Assert.Contains("Sample.Outer.Leaf.g.cs", result.GeneratedSources.Keys);
        Assert.Contains("Sample.Outer_1.Leaf.g.cs", result.GeneratedSources.Keys);
        Assert.Equal(4, result.GeneratedSources.Count);
    }

    [Fact]
    public void NonAbstractGenericUnion_ZGOR003NamesTheUnionInDisplayForm()
    {
        var result = GeneratorTestHelper.Run(GenericUnionSources.NonAbstractGenericUnion);

        var diagnostic = Assert.Single(result.AllDiagnostics.Where(static d => d.Id == "ZGOR003"));
        Assert.Contains("Disclosure<T>", diagnostic.GetMessage());
    }

    [Fact]
    public void MismatchedSubUnionBase_ReportsZGOR005NamingBothTypes()
    {
        var result = GeneratorTestHelper.Run(GenericUnionSources.MismatchedSubUnionBase);

        var diagnostic = Assert.Single(result.AllDiagnostics.Where(static d => d.Id == "ZGOR005"));
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("Outcome<int>", diagnostic.GetMessage());
        Assert.Contains("Outcome<T>", diagnostic.GetMessage());
    }

    [Fact]
    public void MismatchedSubUnion_IsAbsentFromTheParentMatch_CorrectSiblingIsNot()
    {
        var result = GeneratorTestHelper.Run(GenericUnionSources.MismatchedSubUnionBase);

        GeneratorTestHelper.AssertNoErrors(result);
        var source = result.GeneratedSources["Outcome_1.g.cs"];
        Assert.DoesNotContain("Weird", source);
        Assert.Contains("Func<Rejected, TResult> rejected", source);
    }

    [Fact]
    public void NestedUnionDerivingFromAnUnrelatedUnion_ReportsNothing()
    {
        var result = GeneratorTestHelper.Run(GenericUnionSources.NestedUnionWithUnrelatedBase);

        GeneratorTestHelper.AssertNoErrors(result);
        Assert.Empty(result.AllDiagnostics.Where(static d => d.Id == "ZGOR005"));
    }

    [Theory] // D5: the converter is a separate generic type, so it repeats the clause the union may omit
    [InlineData("NotNullBox_1.g.cs", "public class NotNullBoxJsonConverter<T> : System.Text.Json.Serialization.JsonConverter<NotNullBox<T>> where T : notnull")]
    [InlineData("ClassBox_1.g.cs", "public class ClassBoxJsonConverter<T> : System.Text.Json.Serialization.JsonConverter<ClassBox<T>> where T : class")]
    [InlineData("StructBox_1.g.cs", "public class StructBoxJsonConverter<T> : System.Text.Json.Serialization.JsonConverter<StructBox<T>> where T : struct")]
    [InlineData("UnmanagedBox_1.g.cs", "public class UnmanagedBoxJsonConverter<T> : System.Text.Json.Serialization.JsonConverter<UnmanagedBox<T>> where T : unmanaged")]
    [InlineData("ComparableBox_1.g.cs", "public class ComparableBoxJsonConverter<T> : System.Text.Json.Serialization.JsonConverter<ComparableBox<T>> where T : System.IComparable<T>, new()")]
    [InlineData("CacheBox_1.g.cs", "public class CacheBoxJsonConverter<T> : System.Text.Json.Serialization.JsonConverter<CacheBox<T>> where T : System.Collections.Generic.IEqualityComparer<T>, new()")]
    [InlineData("CompoundBox_1.g.cs", "public class CompoundBoxJsonConverter<T> : System.Text.Json.Serialization.JsonConverter<CompoundBox<T>> where T : Root, System.IComparable<T>, new()")]
    public void Constraints_AreRepeatedOnTheConverter(string hint, string expectedDeclaration)
    {
        var result = GeneratorTestHelper.Run(GenericUnionSources.ConstrainedGenericUnions);

        GeneratorTestHelper.AssertNoErrors(result);
        Assert.Contains(expectedDeclaration, result.GeneratedSources[hint]);
    }

    [Fact]
    public void ConstrainedGenericUnions_CompileWarningClean()
    {
        var result = GeneratorTestHelper.Run(GenericUnionSources.ConstrainedGenericUnions);

        GeneratorTestHelper.AssertNoErrors(result);
        GeneratorTestHelper.AssertGeneratedOutputHasNoWarnings(result);
    }

    [Fact] // CS0416 on the previous emitter: the converter landed inside the generic container
    public void NonGenericUnionInGenericContainer_Compiles()
    {
        var result = GeneratorTestHelper.Run(GenericUnionSources.UnionsInGenericContainers);

        GeneratorTestHelper.AssertNoErrors(result);
        GeneratorTestHelper.AssertGeneratedOutputHasNoWarnings(result);
        Assert.Empty(result.AllDiagnostics.Where(static d => d.Id == "CS0416"));
    }

    [Fact]
    public void ConverterInGenericContainer_IsHoistedAndAbsorbsTheContainerParameters()
    {
        var result = GeneratorTestHelper.Run(GenericUnionSources.UnionsInGenericContainers);
        var source = result.GeneratedSources["Outer_1.Leaf_1.g.cs"];

        Assert.Contains(
            "public class Outer_LeafJsonConverter<TOuter, T> : System.Text.Json.Serialization.JsonConverter<Outer<TOuter>.Leaf<T>> where TOuter : notnull where T : class",
            source);
        // The attribute argument names the non-generic factory, so it carries no type parameter.
        Assert.Contains("[System.Text.Json.Serialization.JsonConverter(typeof(Outer_LeafJsonConverterFactory))]", source);
        // Reopened with its own parameter list, not as a distinct non-generic type.
        Assert.Contains("public partial class Outer<TOuter> where TOuter : notnull", source);
    }

    [Fact] // D3: only the shape that does not compile today moves; every other converter keeps its name
    public void NoGenericContainer_ConverterKeepsItsPositionAndName()
    {
        var result = GeneratorTestHelper.Run(TestSources.NestedSealedJsonRoundTrip);
        var source = result.GeneratedSources["IEntityPayload.V1.g.cs"];

        GeneratorTestHelper.AssertNoErrors(result);
        // Emitted inside the containing type, at its indentation, under its original name.
        Assert.Contains("    public class V1JsonConverter : System.Text.Json.Serialization.JsonConverter<V1>", source);
        Assert.DoesNotContain("JsonConverterFactory", source);
    }

    [Fact]
    public void GenericUnionReflectionSites_CarryTheAotSuppression()
    {
        var result = GeneratorTestHelper.Run(GenericUnionSources.GenericUnionJson);
        var source = result.GeneratedSources["Disclosure_1.g.cs"];

        GeneratorTestHelper.AssertNoErrors(result);
        // One in the System.Text.Json factory, one in the Newtonsoft shim.
        Assert.Equal(2, CountOccurrences(source, "#pragma warning disable IL3050"));
        Assert.Equal(2, CountOccurrences(source, "#pragma warning restore IL3050"));
        Assert.Empty(result.AllDiagnostics.Where(static d => d.Id == "CS1691"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
            i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
