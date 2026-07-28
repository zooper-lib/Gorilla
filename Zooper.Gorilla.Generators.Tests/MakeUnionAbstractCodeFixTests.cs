using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Xunit;

namespace Zooper.Gorilla.Generators.Tests;

/// <summary>
/// The ZGOR003 code fix is the whole migration path for consuming codebases, so it has to
/// produce exactly the declaration the user would have written by hand.
/// </summary>
public class MakeUnionAbstractCodeFixTests
{
    private static async Task<string> ApplyFixAsync(string source)
    {
        var generatorResult = GeneratorTestHelper.Run(source);
        var diagnostic = Assert.Single(generatorResult.AllDiagnostics.Where(static d => d.Id == "ZGOR003"));

        using var workspace = new AdhocWorkspace();
        var document = workspace.CurrentSolution
            .AddProject("Fix", "Fix", LanguageNames.CSharp)
            .AddDocument("Union.cs", source);

        var actions = new List<CodeAction>();
        var context = new CodeFixContext(
            document,
            diagnostic,
            (action, _) => actions.Add(action),
            CancellationToken.None);

        await new MakeUnionAbstractCodeFixProvider().RegisterCodeFixesAsync(context);

        var operations = await Assert.Single(actions).GetOperationsAsync(CancellationToken.None);
        var changed = operations.OfType<ApplyChangesOperation>().Single()
            .ChangedSolution.GetDocument(document.Id)!;

        return (await changed.GetTextAsync()).ToString();
    }

    [Fact]
    public async Task Sealed_IsReplacedByAbstract()
    {
        var fixedSource = await ApplyFixAsync(TestSources.NonAbstractUnion);

        Assert.Contains("public abstract partial class EntityState", fixedSource);
        Assert.DoesNotContain("sealed", fixedSource);
    }

    [Fact]
    public async Task PlainDeclaration_GainsAbstract()
    {
        var fixedSource = await ApplyFixAsync(TestSources.PlainPartialUnion);

        Assert.Contains("public abstract partial record Outcome", fixedSource);
    }

    [Fact]
    public async Task RecordAccessibilityIsPreserved()
    {
        var fixedSource = await ApplyFixAsync(TestSources.InternalSealedRecordUnion);

        Assert.Contains("internal abstract partial record Outcome", fixedSource);
    }

    [Fact]
    public void FixIsRegisteredForZgor003AndSupportsFixAll()
    {
        var provider = new MakeUnionAbstractCodeFixProvider();

        Assert.Equal(new[] { "ZGOR003" }, provider.FixableDiagnosticIds);
        Assert.Equal(WellKnownFixAllProviders.BatchFixer, provider.GetFixAllProvider());
    }
}
