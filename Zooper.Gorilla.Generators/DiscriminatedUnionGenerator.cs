using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Zooper.Gorilla.Generators;

[Generator]
public class DiscriminatedUnionGenerator : IIncrementalGenerator
{
	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		var classDeclarations = context.SyntaxProvider
			.CreateSyntaxProvider(
				predicate: (
					node,
					_) => node is ClassDeclarationSyntax,
				transform: (
					ctx,
					_) => GetSemanticTargetForGeneration(ctx)
			)
			.Where(m => m is not null)!;

		var compilationAndClasses = context.CompilationProvider.Combine(classDeclarations.Collect());

		context.RegisterSourceOutput(
			compilationAndClasses,
			(
				spc,
				source) => Execute(spc, source.Left, source.Right, spc.CancellationToken)
		);
	}

	private static ClassDeclarationSyntax? GetSemanticTargetForGeneration(GeneratorSyntaxContext context)
	{
		var classDecl = (ClassDeclarationSyntax)context.Node;

		var hasAttribute = classDecl.AttributeLists
			.SelectMany(al => al.Attributes)
			.Any(
				attr =>
				{
					var name = attr.Name.ToString();
					return name is "DiscriminatedUnion" or "DiscriminatedUnionAttribute";
				}
			);

		return hasAttribute ? classDecl : null;
	}

	private void Execute(
		SourceProductionContext context,
		Compilation compilation,
		ImmutableArray<ClassDeclarationSyntax> classDeclarations,
		CancellationToken cancellationToken)
	{
		if (classDeclarations.IsDefaultOrEmpty)
		{
			return;
		}

		foreach (var classDecl in classDeclarations)
		{
			// Get the SemanticModel for the syntax tree that contains the current class declaration
			var model = compilation.GetSemanticModel(classDecl.SyntaxTree);

			// Now, use the SemanticModel to get the symbol for the current class declaration
			var classSymbol = model.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;

			if (classSymbol == null)
			{
				continue;
			}

			// Proceed with the rest of your code generation logic
			var variants = classSymbol.GetMembers()
				.OfType<IMethodSymbol>()
				.Where(
					method => method.GetAttributes()
						.Any(
							attr =>
							{
								var name = attr.AttributeClass?.Name;
								return name is "VariantAttribute" or "Variant";
							}
						)
				)
				.ToList();

			var source = GenerateSource(classSymbol, variants);
			context.AddSource($"{classSymbol.Name}.g.cs", SourceText.From(source, Encoding.UTF8));
		}
	}

	private string GenerateSource(
		INamedTypeSymbol classSymbol,
		List<IMethodSymbol> variants)
	{
		var namespaceName = classSymbol.ContainingNamespace.ToDisplayString();
		var className = classSymbol.Name;

		var sb = new StringBuilder();
		sb.AppendLine("using OneOf;");
		sb.AppendLine("using System;");
		sb.AppendLine();

		sb.AppendLine($"namespace {namespaceName}");
		sb.AppendLine("{");

		GenerateClassHeader(sb, className, variants);
		sb.AppendLine("    {");

		GenerateConstructor(sb, className, variants);
		sb.AppendLine();

		GenerateVariantMethods(sb, className, variants);

		GenerateVariantClasses(sb, className, variants);

		sb.AppendLine("    }");
		sb.AppendLine("}");

		return sb.ToString();
	}

	private void GenerateClassHeader(
		StringBuilder sb,
		string className,
		List<IMethodSymbol> variants)
	{
		var variantTypeNames = variants.Select(v => $"{className}.{v.Name}Variant").ToList();
		sb.AppendLine($"    public partial class {className} : OneOfBase<{string.Join(", ", variantTypeNames)}>");
	}

	private void GenerateConstructor(
		StringBuilder sb,
		string className,
		List<IMethodSymbol> variants)
	{
		var variantTypeNames = variants.Select(v => $"{className}.{v.Name}Variant").ToList();
		sb.AppendLine($"        private {className}(OneOf<{string.Join(", ", variantTypeNames)}> value) : base(value) {{ }}");
	}

	private void GenerateVariantMethods(
		StringBuilder sb,
		string className,
		List<IMethodSymbol> variants)
	{
		foreach (var variant in variants)
		{
			var variantName = variant.Name;
			var parameters = string.Join(", ", variant.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"));
			var args = string.Join(", ", variant.Parameters.Select(p => p.Name));
			var variantTypeName = $"{variantName}Variant";
			var methodSignature = $"public static partial {className} {variantName}({parameters})";
			var newVariantInstance = string.IsNullOrEmpty(args)
				? $"new {variantTypeName}()"
				: $"new {variantTypeName}({args})";

			sb.AppendLine($"        {methodSignature} => new {className}({newVariantInstance});");
		}
	}

	private void GenerateVariantClasses(
		StringBuilder sb,
		string className,
		List<IMethodSymbol> variants)
	{
		foreach (var variant in variants)
		{
			var variantName = variant.Name;
			var variantTypeName = $"{variantName}Variant";
			var parameters = variant.Parameters;
			var fields = parameters.Select(p => $"            public {p.Type.ToDisplayString()} {FirstCharToUpper(p.Name)} {{ get; }}")
				.ToList();
			var ctorParameters = string.Join(", ", parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"));
			var assignments = parameters.Select(p => $"                this.{FirstCharToUpper(p.Name)} = {p.Name};").ToList();

			sb.AppendLine();
			sb.AppendLine($"        public class {variantTypeName}");
			sb.AppendLine("        {");

			if (parameters.Any())
			{
				// Make the constructor internal or private
				sb.AppendLine($"            internal {variantTypeName}({ctorParameters})");
				sb.AppendLine("            {");

				foreach (var assignment in assignments)
				{
					sb.AppendLine(assignment);
				}

				sb.AppendLine("            }");
				sb.AppendLine();

				foreach (var field in fields)
				{
					sb.AppendLine(field);
				}
			}
			else
			{
				sb.AppendLine($"            internal {variantTypeName}() {{ }}");
			}

			sb.AppendLine("        }");
		}
	}

	// Helper methods
	private string FirstCharToLower(string input) =>
		string.IsNullOrEmpty(input) ? input : char.ToLower(input[0]) + input.Substring(1);

	private string FirstCharToUpper(string input) =>
		string.IsNullOrEmpty(input) ? input : char.ToUpper(input[0]) + input.Substring(1);
}