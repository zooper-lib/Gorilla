using Xunit;

namespace Zooper.Gorilla.Generators.Tests;

/// <summary>
/// The generated public surface: Match/Switch as the exhaustive door, and the variant-named
/// IsX / AsX() / TryPickX accessors over variants and sub-unions alike.
/// </summary>
public class UnionApiSurfaceTests
{
    private static string Run(string source, string method)
    {
        var result = GeneratorTestHelper.Run(source);
        GeneratorTestHelper.AssertNoErrors(result);
        var assembly = GeneratorTestHelper.EmitToAssembly(result);
        return GeneratorTestHelper.InvokeStaticStringMethod(assembly, "Usage", method);
    }

    [Fact]
    public void Switch_OnUnionWithoutSubUnions_Dispatches()
        => Assert.Equal("card:4111", Run(TestSources.SwitchAndAccessors, "SwitchWithoutSubUnions"));

    [Fact]
    public void Switch_OnUnionWithSubUnions_Dispatches()
        => Assert.Equal("rejected:email", Run(TestSources.SwitchAndAccessors, "SwitchWithSubUnions"));

    [Fact]
    public void Accessors_CoverVariants()
    {
        var output = Run(TestSources.SwitchAndAccessors, "VariantAccessors");

        Assert.Equal(
            "IsCard=True|IsCash=False|AsCard=4111|TryPickCard=True:4111|TryPickCash=False:True|"
            + "AsCash=Cannot access this Pay as 'CashVariant' because it is 'CardVariant'.",
            output);
    }

    [Fact]
    public void Accessors_CoverSubUnions()
        => Assert.Equal(
            "IsSuccess=False|IsRejected=True|AsRejected=email|TryPickRejected=True:True|TryPickSuccess=False:True",
            Run(TestSources.SwitchAndAccessors, "SubUnionAccessors"));

    /// <summary>
    /// The guard that keeps AsX from regressing to a property: every public property getter is
    /// invoked by reflection and none may throw, so only IsX booleans and the payload may exist.
    /// </summary>
    [Fact]
    public void PublicPropertyGetters_NeverThrow()
        => Assert.Equal("IsCard,IsCash,Number", Run(TestSources.SwitchAndAccessors, "WalkPublicProperties"));

    [Fact]
    public void PositionalMembers_AreNotEmitted()
        => Assert.Equal(string.Empty, Run(TestSources.SwitchAndAccessors, "NoPositionalMembers"));

    [Fact]
    public void UnionWithMoreThanNineVariants_CompilesAndDispatches()
        => Assert.Equal("11:done|11:done", Run(TestSources.TenVariantUnion, "Run"));
}
