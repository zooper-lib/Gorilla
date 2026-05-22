using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using OneOf;
using Xunit;
using Zooper.Gorilla.Attributes;
using Zooper.Gorilla.Generators;

namespace Zooper.Gorilla.Generators.Tests;

internal static class GeneratorTestHelper
{
    public static GeneratorTestResult Run(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            assemblyName: $"GeneratorTests_{Guid.NewGuid():N}",
            syntaxTrees: new[] { syntaxTree },
            references: GetMetadataReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new DiscriminatedUnionGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);

        var runResult = driver.GetRunResult();
        var generatedSources = runResult.Results
            .SelectMany(static result => result.GeneratedSources)
            .ToDictionary(sourceResult => sourceResult.HintName, sourceResult => sourceResult.SourceText.ToString(), StringComparer.Ordinal);

        return new GeneratorTestResult(
            (CSharpCompilation)outputCompilation,
            generatorDiagnostics,
            generatedSources);
    }

    public static void AssertNoErrors(GeneratorTestResult result)
    {
        Assert.True(result.Errors.Length == 0, FormatDiagnostics(result.Errors));
    }

    public static Assembly EmitToAssembly(GeneratorTestResult result)
    {
        using var assemblyStream = new MemoryStream();
        var emitResult = result.OutputCompilation.Emit(assemblyStream);
        Assert.True(emitResult.Success, FormatDiagnostics(emitResult.Diagnostics));
        assemblyStream.Position = 0;
        return Assembly.Load(assemblyStream.ToArray());
    }

    public static string InvokeStaticStringMethod(Assembly assembly, string typeName, string methodName)
    {
        var type = assembly.GetType(typeName, throwOnError: true)!;
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, Array.Empty<object>())!;
    }

    private static ImmutableArray<MetadataReference> GetMetadataReferences()
    {
        var references = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
        var trustedPlatformAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator)
            ?? Array.Empty<string>();

        foreach (var assemblyPath in trustedPlatformAssemblies)
        {
            references[assemblyPath] = MetadataReference.CreateFromFile(assemblyPath);
        }

        foreach (var assembly in new[]
        {
            typeof(object).Assembly,
            typeof(Enumerable).Assembly,
            typeof(System.Text.Json.JsonSerializer).Assembly,
            typeof(Newtonsoft.Json.JsonConvert).Assembly,
            typeof(OneOfBase<>).Assembly,
            typeof(DiscriminatedUnionAttribute).Assembly,
        })
        {
            if (!string.IsNullOrWhiteSpace(assembly.Location))
            {
                references[assembly.Location] = MetadataReference.CreateFromFile(assembly.Location);
            }
        }

        return references.Values.ToImmutableArray();
    }

    private static string FormatDiagnostics(IEnumerable<Diagnostic> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => diagnostic.ToString()));
}

internal sealed record GeneratorTestResult(
    CSharpCompilation OutputCompilation,
    ImmutableArray<Diagnostic> GeneratorDiagnostics,
    IReadOnlyDictionary<string, string> GeneratedSources)
{
    public ImmutableArray<Diagnostic> CompilationDiagnostics => OutputCompilation.GetDiagnostics();

    public ImmutableArray<Diagnostic> AllDiagnostics => GeneratorDiagnostics.AddRange(CompilationDiagnostics);

    public ImmutableArray<Diagnostic> Errors => AllDiagnostics
        .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
        .ToImmutableArray();
}
