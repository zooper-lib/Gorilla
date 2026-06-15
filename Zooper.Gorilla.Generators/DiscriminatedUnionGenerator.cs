using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Zooper.Gorilla.Generators;

[Generator]
public class DiscriminatedUnionGenerator : IIncrementalGenerator
{
	private const string DiscriminatedUnionAttributeFullName = "Zooper.Gorilla.Attributes.DiscriminatedUnionAttribute";

	private static readonly DiagnosticDescriptor GeneratorErrorDescriptor = new(
		id: "ZGOR001",
		title: "Discriminated union generator failure",
		messageFormat: "Failed to generate discriminated union for '{0}': {1}",
		category: "Zooper.Gorilla",
		defaultSeverity: DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	private static readonly DiagnosticDescriptor NonPartialContainingTypeDescriptor = new(
		id: "ZGOR002",
		title: "Containing type must be partial",
		messageFormat: "Containing type '{0}' of discriminated union '{1}' is not declared partial. The generated code will not compile unless all containing types are partial.",
		category: "Zooper.Gorilla",
		defaultSeverity: DiagnosticSeverity.Warning,
		isEnabledByDefault: true);

	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		var unions = context.SyntaxProvider
			.ForAttributeWithMetadataName(
				DiscriminatedUnionAttributeFullName,
				predicate: static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
				transform: static (ctx, ct) => CreateUnionModel(ctx, ct))
			.Where(static model => model is not null)
			.Select(static (model, _) => model!);

		var frameworkSupport = context.CompilationProvider
			.Select(static (compilation, _) => new FrameworkSupport(
				HasNewtonsoftJson: compilation.GetTypeByMetadataName("Newtonsoft.Json.JsonConverter") is not null,
				HasSystemTextJson: compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonConverter") is not null,
				HasAspNetValidation: compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNeverAttribute") is not null));

		var combined = unions.Combine(frameworkSupport);

		context.RegisterSourceOutput(combined, static (spc, pair) => Execute(spc, pair.Left, pair.Right));
	}

	private static UnionModel? CreateUnionModel(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
	{
		if (context.TargetSymbol is not INamedTypeSymbol classSymbol)
		{
			return null;
		}

		cancellationToken.ThrowIfCancellationRequested();

		var attribute = context.Attributes.FirstOrDefault();
		var config = ParseConfig(attribute);
		var containingTypes = GetContainingTypes(classSymbol);
		var subUnions = GetDirectSubUnions(classSymbol);

		var variantBuilder = ImmutableArray.CreateBuilder<VariantModel>();

		foreach (var member in classSymbol.GetMembers())
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (member is not IMethodSymbol method)
			{
				continue;
			}

			var hasVariantAttribute = method.GetAttributes()
				.Any(a => a.AttributeClass?.Name is "VariantAttribute" or "Variant");

			if (!hasVariantAttribute)
			{
				continue;
			}

			var paramBuilder = ImmutableArray.CreateBuilder<VariantParameter>();
			foreach (var parameter in method.Parameters)
			{
				paramBuilder.Add(new VariantParameter(parameter.Name, parameter.Type.ToDisplayString()));
			}

			variantBuilder.Add(new VariantModel(method.Name, new EquatableArray<VariantParameter>(paramBuilder.ToArray())));
		}

		return new UnionModel(
			Namespace: classSymbol.ContainingNamespace.IsGlobalNamespace
				? string.Empty
				: classSymbol.ContainingNamespace.ToDisplayString(),
			ClassName: classSymbol.Name,
			TypeKeyword: GetTypeKeyword(classSymbol),
			AccessModifier: GetAccessModifier(classSymbol),
			Variants: new EquatableArray<VariantModel>(variantBuilder.ToArray()),
			ContainingTypes: containingTypes,
			IsAbstract: classSymbol.IsAbstract,
			SubUnions: subUnions,
			Config: config);
	}

	private static EquatableArray<ContainingTypeInfo> GetContainingTypes(INamedTypeSymbol classSymbol)
	{
		var types = new List<ContainingTypeInfo>();
		var containingType = classSymbol.ContainingType;

		while (containingType is not null)
		{
			types.Add(new ContainingTypeInfo(
				Name: containingType.Name,
				Keyword: GetTypeKeyword(containingType),
				AccessModifier: GetAccessModifier(containingType),
				IsPartial: IsPartial(containingType),
				Location: containingType.Locations.FirstOrDefault()));
			containingType = containingType.ContainingType;
		}

		types.Reverse();
		return new EquatableArray<ContainingTypeInfo>(types.ToArray());
	}

	private static EquatableArray<string> GetDirectSubUnions(INamedTypeSymbol classSymbol)
	{
		var subUnionNames = classSymbol
			.GetTypeMembers()
			.Where(static nestedType =>
				nestedType.GetAttributes().Any(static attribute =>
					attribute.AttributeClass?.ToDisplayString() == DiscriminatedUnionAttributeFullName))
			.Where(nestedType => SymbolEqualityComparer.Default.Equals(nestedType.BaseType, classSymbol))
			.Select(static nestedType => nestedType.Name)
			.ToArray();

		return new EquatableArray<string>(subUnionNames);
	}

	private static string GetTypeKeyword(INamedTypeSymbol typeSymbol) =>
		typeSymbol.TypeKind switch
		{
			TypeKind.Interface => "interface",
			TypeKind.Class when typeSymbol.IsRecord => "record",
			TypeKind.Class => "class",
			TypeKind.Struct when typeSymbol.IsRecord => "record struct",
			TypeKind.Struct => "struct",
			_ => "class"
		};

	private static string GetAccessModifier(INamedTypeSymbol typeSymbol)
	{
		if (typeSymbol.DeclaredAccessibility == Accessibility.NotApplicable)
		{
			return string.Empty;
		}

		return typeSymbol.DeclaredAccessibility switch
		{
			Accessibility.Public => "public",
			Accessibility.Private => "private",
			Accessibility.Protected => "protected",
			Accessibility.Internal => "internal",
			Accessibility.ProtectedAndInternal => "private protected",
			Accessibility.ProtectedOrInternal => "protected internal",
			_ => string.Empty
		};
	}

	private static bool IsPartial(INamedTypeSymbol typeSymbol) =>
		typeSymbol.DeclaringSyntaxReferences
			.Select(static syntaxReference => syntaxReference.GetSyntax())
			.OfType<TypeDeclarationSyntax>()
			.Any(static declaration => declaration.Modifiers.Any(SyntaxKind.PartialKeyword));

	private static UnionConfig ParseConfig(AttributeData? attribute)
	{
		bool? suppressValidation = null;
		string discriminatorFieldName = "$type";
		bool? generateJsonConverter = null;
		bool? generateNewtonsoftJsonConverter = null;

		if (attribute is not null)
		{
			foreach (var arg in attribute.NamedArguments)
			{
				switch (arg.Key)
				{
					case "SuppressValidation":
						if (arg.Value.Value is bool sv) suppressValidation = sv;
						break;
					case "GenerateJsonConverter":
						if (arg.Value.Value is bool gjc) generateJsonConverter = gjc;
						break;
					case "DiscriminatorFieldName":
						if (arg.Value.Value is string dfn && !string.IsNullOrEmpty(dfn)) discriminatorFieldName = dfn;
						break;
					case "GenerateNewtonsoftJsonConverter":
						if (arg.Value.Value is bool gnj) generateNewtonsoftJsonConverter = gnj;
						break;
				}
			}
		}

		return new UnionConfig(
			SuppressValidation: suppressValidation,
			DiscriminatorFieldName: discriminatorFieldName,
			GenerateJsonConverter: generateJsonConverter,
			GenerateNewtonsoftJsonConverter: generateNewtonsoftJsonConverter);
	}

	private static void Execute(SourceProductionContext context, UnionModel union, FrameworkSupport frameworkSupport)
	{
		try
		{
			ReportContainingTypeDiagnostics(context, union);

			var generateJsonConverter = union.Config.GenerateJsonConverter ?? frameworkSupport.HasSystemTextJson;
			var generateNewtonsoftJsonConverter = union.Config.GenerateNewtonsoftJsonConverter ?? frameworkSupport.HasNewtonsoftJson;
			var suppressValidation = union.Config.SuppressValidation ?? frameworkSupport.HasAspNetValidation;

			var source = union.IsAbstract
				? GenerateHierarchicalSource(union, generateJsonConverter, generateNewtonsoftJsonConverter, suppressValidation)
				: GenerateFlatSource(union, generateJsonConverter, generateNewtonsoftJsonConverter, suppressValidation);
			context.AddSource(GetSourceHint(union), SourceText.From(source, Encoding.UTF8));
		}
		catch (Exception ex)
		{
			context.ReportDiagnostic(Diagnostic.Create(
				GeneratorErrorDescriptor,
				Location.None,
				union.ClassName,
				ex.Message));
		}
	}

	private static void ReportContainingTypeDiagnostics(SourceProductionContext context, UnionModel union)
	{
		foreach (var containingType in union.ContainingTypes)
		{
			if (containingType.IsPartial)
			{
				continue;
			}

			context.ReportDiagnostic(Diagnostic.Create(
				NonPartialContainingTypeDescriptor,
				containingType.Location ?? Location.None,
				containingType.Name,
				union.ClassName));
		}
	}

	private static string GetSourceHint(UnionModel union)
	{
		if (union.ContainingTypes.Count == 0)
		{
			return $"{union.ClassName}.g.cs";
		}

		var containingPath = string.Join(".", union.ContainingTypes.Select(static type => type.Name));
		return $"{containingPath}.{union.ClassName}.g.cs";
	}

	private static string GenerateFlatSource(
		UnionModel union,
		bool generateJsonConverter,
		bool generateNewtonsoftJsonConverter,
		bool suppressValidation)
	{
		var className = union.ClassName;
		var namespaceName = union.Namespace;
		var variants = union.Variants;

		var sb = new StringBuilder();
		sb.AppendLine("#nullable enable");
		sb.AppendLine("using OneOf;");
		sb.AppendLine("using System;");
		sb.AppendLine();

		var indentLevel = 0;
		if (!string.IsNullOrWhiteSpace(namespaceName))
		{
			AppendLineIndented(sb, indentLevel, $"namespace {namespaceName}");
			AppendLineIndented(sb, indentLevel, "{");
			indentLevel++;
		}

		foreach (var containingType in union.ContainingTypes)
		{
			EmitContainingTypeOpen(sb, containingType, indentLevel);
			indentLevel++;
		}

		if (generateJsonConverter)
		{
			AppendLineIndented(sb, indentLevel, $"[System.Text.Json.Serialization.JsonConverter(typeof({className}JsonConverter))]");
		}

		if (generateNewtonsoftJsonConverter)
		{
			AppendLineIndented(sb, indentLevel, $"[Newtonsoft.Json.JsonConverterAttribute(typeof({className}NewtonsoftJsonConverter))]");
		}

		if (suppressValidation)
		{
			AppendLineIndented(sb, indentLevel, "[Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNever]");
		}

		GenerateClassHeader(sb, union, variants, indentLevel);
		AppendLineIndented(sb, indentLevel, "{");

		GenerateConstructor(sb, className, variants, indentLevel + 1);
		sb.AppendLine();

		GenerateVariantMethods(sb, className, variants, indentLevel + 1);

		GenerateVariantClasses(sb, variants, indentLevel + 1);

		AppendLineIndented(sb, indentLevel, "}");

		if (generateJsonConverter)
		{
			sb.AppendLine();
			GenerateJsonConverterClass(sb, className, variants, union.Config.DiscriminatorFieldName, indentLevel);
		}

		if (generateNewtonsoftJsonConverter)
		{
			sb.AppendLine();
			GenerateNewtonsoftJsonConverterClass(sb, className, variants, union.Config.DiscriminatorFieldName, indentLevel);
		}

		for (var i = union.ContainingTypes.Count - 1; i >= 0; i--)
		{
			indentLevel--;
			EmitContainingTypeClose(sb, indentLevel);
		}

		if (!string.IsNullOrWhiteSpace(namespaceName))
		{
			indentLevel--;
			AppendLineIndented(sb, indentLevel, "}");
		}

		return sb.ToString();
	}

	private static string GenerateHierarchicalSource(
		UnionModel union,
		bool generateJsonConverter,
		bool generateNewtonsoftJsonConverter,
		bool suppressValidation)
	{
		var sb = new StringBuilder();
		sb.AppendLine("#nullable enable");
		sb.AppendLine("using System;");
		sb.AppendLine();

		var indentLevel = 0;
		if (!string.IsNullOrWhiteSpace(union.Namespace))
		{
			AppendLineIndented(sb, indentLevel, $"namespace {union.Namespace}");
			AppendLineIndented(sb, indentLevel, "{");
			indentLevel++;
		}

		foreach (var containingType in union.ContainingTypes)
		{
			EmitContainingTypeOpen(sb, containingType, indentLevel);
			indentLevel++;
		}

		if (generateJsonConverter)
		{
			AppendLineIndented(sb, indentLevel, $"[System.Text.Json.Serialization.JsonConverter(typeof({union.ClassName}JsonConverter))]");
		}

		if (generateNewtonsoftJsonConverter)
		{
			AppendLineIndented(sb, indentLevel, $"[Newtonsoft.Json.JsonConverterAttribute(typeof({union.ClassName}NewtonsoftJsonConverter))]");
		}

		if (suppressValidation)
		{
			AppendLineIndented(sb, indentLevel, "[Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNever]");
		}

		GenerateHierarchicalClassHeader(sb, union, indentLevel);
		AppendLineIndented(sb, indentLevel, "{");
		GenerateHierarchicalConstructor(sb, union.ClassName, indentLevel + 1);
		sb.AppendLine();
		GenerateHierarchicalVariantMethods(sb, union, indentLevel + 1);
		sb.AppendLine();
		GenerateHierarchicalMatchMethod(sb, union, indentLevel + 1);
		GenerateHierarchicalVariantClasses(sb, union, indentLevel + 1);
		AppendLineIndented(sb, indentLevel, "}");

		if (generateJsonConverter)
		{
			sb.AppendLine();
			GenerateHierarchicalJsonConverterClass(sb, union, union.Config.DiscriminatorFieldName, indentLevel);
		}

		if (generateNewtonsoftJsonConverter)
		{
			sb.AppendLine();
			GenerateHierarchicalNewtonsoftJsonConverterClass(sb, union, union.Config.DiscriminatorFieldName, indentLevel);
		}

		for (var i = union.ContainingTypes.Count - 1; i >= 0; i--)
		{
			indentLevel--;
			EmitContainingTypeClose(sb, indentLevel);
		}

		if (!string.IsNullOrWhiteSpace(union.Namespace))
		{
			indentLevel--;
			AppendLineIndented(sb, indentLevel, "}");
		}

		return sb.ToString();
	}

	private static void EmitContainingTypeOpen(StringBuilder sb, ContainingTypeInfo type, int indentLevel)
	{
		var accessModifier = string.IsNullOrWhiteSpace(type.AccessModifier)
			? string.Empty
			: type.AccessModifier + " ";
		AppendLineIndented(sb, indentLevel, $"{accessModifier}partial {type.Keyword} {type.Name}");
		AppendLineIndented(sb, indentLevel, "{");
	}

	private static void EmitContainingTypeClose(StringBuilder sb, int indentLevel) =>
		AppendLineIndented(sb, indentLevel, "}");

	private static void GenerateClassHeader(
		StringBuilder sb,
		UnionModel union,
		EquatableArray<VariantModel> variants,
		int indentLevel)
	{
		var variantTypeNames = variants.Select(v => $"{union.ClassName}.{v.Name}Variant").ToList();
		AppendLineIndented(sb, indentLevel, $"{GetTypeDeclarationPrefix(union, includeAbstractModifier: false)} : OneOfBase<{string.Join(", ", variantTypeNames)}>");
	}

	private static void GenerateHierarchicalClassHeader(
		StringBuilder sb,
		UnionModel union,
		int indentLevel) =>
		AppendLineIndented(sb, indentLevel, GetTypeDeclarationPrefix(union, includeAbstractModifier: true));

	private static void GenerateConstructor(
		StringBuilder sb,
		string className,
		EquatableArray<VariantModel> variants,
		int indentLevel)
	{
		var variantTypeNames = variants.Select(v => $"{className}.{v.Name}Variant").ToList();
		AppendLineIndented(sb, indentLevel, $"private {className}(OneOf<{string.Join(", ", variantTypeNames)}> value) : base(value) {{ }}");
	}

	private static void GenerateVariantMethods(
		StringBuilder sb,
		string className,
		EquatableArray<VariantModel> variants,
		int indentLevel)
	{
		foreach (var variant in variants)
		{
			var variantName = variant.Name;
			var parameters = string.Join(", ", variant.Parameters.Select(p => $"{p.Type} {p.Name}"));
			var args = string.Join(", ", variant.Parameters.Select(p => p.Name));
			var variantTypeName = $"{variantName}Variant";
			var methodSignature = $"public static partial {className} {variantName}({parameters})";
			var newVariantInstance = string.IsNullOrEmpty(args)
				? $"new {variantTypeName}()"
				: $"new {variantTypeName}({args})";

			AppendLineIndented(sb, indentLevel, $"{methodSignature} => new {className}({newVariantInstance});");
		}
	}

	private static void GenerateVariantClasses(
		StringBuilder sb,
		EquatableArray<VariantModel> variants,
		int indentLevel)
	{
		foreach (var variant in variants)
		{
			var variantName = variant.Name;
			var variantTypeName = $"{variantName}Variant";
			var parameters = variant.Parameters;
			var fields = parameters.Select(p => $"public {p.Type} {FirstCharToUpper(p.Name)} {{ get; }}")
				.ToList();
			var ctorParameters = string.Join(", ", parameters.Select(p => $"{p.Type} {p.Name}"));
			var assignments = parameters.Select(p => $"this.{FirstCharToUpper(p.Name)} = {p.Name};").ToList();

			sb.AppendLine();
			AppendLineIndented(sb, indentLevel, $"public class {variantTypeName}");
			AppendLineIndented(sb, indentLevel, "{");

			if (parameters.Count > 0)
			{
				AppendLineIndented(sb, indentLevel + 1, $"internal {variantTypeName}({ctorParameters})");
				AppendLineIndented(sb, indentLevel + 1, "{");

				foreach (var assignment in assignments)
				{
					AppendLineIndented(sb, indentLevel + 2, assignment);
				}

				AppendLineIndented(sb, indentLevel + 1, "}");
				sb.AppendLine();

				foreach (var field in fields)
				{
					AppendLineIndented(sb, indentLevel + 1, field);
				}
			}
			else
			{
				AppendLineIndented(sb, indentLevel + 1, $"internal {variantTypeName}() {{ }}");
			}

			AppendLineIndented(sb, indentLevel, "}");
		}
	}

	private static void GenerateHierarchicalConstructor(
		StringBuilder sb,
		string className,
		int indentLevel) =>
		AppendLineIndented(sb, indentLevel, $"private protected {className}() {{ }}");

	private static void GenerateHierarchicalVariantMethods(
		StringBuilder sb,
		UnionModel union,
		int indentLevel)
	{
		foreach (var variant in union.Variants)
		{
			var parameters = string.Join(", ", variant.Parameters.Select(p => $"{p.Type} {p.Name}"));
			var args = string.Join(", ", variant.Parameters.Select(p => p.Name));
			var newVariantInstance = string.IsNullOrEmpty(args)
				? $"new {variant.Name}Variant()"
				: $"new {variant.Name}Variant({args})";

			AppendLineIndented(sb, indentLevel, $"public static partial {union.ClassName} {variant.Name}({parameters}) => {newVariantInstance};");
		}
	}

	private static void GenerateHierarchicalMatchMethod(
		StringBuilder sb,
		UnionModel union,
		int indentLevel)
	{
		var matchArms = union.Variants
			.Select(static variant => ($"{variant.Name}Variant", GetHandlerName(variant.Name), GetHandlerValueName(variant.Name)))
			.Concat(union.SubUnions.Select(static subUnion => (subUnion, GetHandlerName(subUnion), GetHandlerValueName(subUnion))))
			.ToList();

		var parameters = matchArms
			.Select(static arm => $"Func<{arm.Item1}, T> {arm.Item2}")
			.ToList();

		AppendLineIndented(sb, indentLevel, "public T Match<T>(");
		for (var i = 0; i < parameters.Count; i++)
		{
			var suffix = i < parameters.Count - 1 ? "," : ") =>";
			AppendLineIndented(sb, indentLevel + 1, parameters[i] + suffix);
		}
		AppendLineIndented(sb, indentLevel + 1, "this switch");
		AppendLineIndented(sb, indentLevel + 1, "{");

		foreach (var matchArm in matchArms)
		{
			AppendLineIndented(sb, indentLevel + 2, $"{matchArm.Item1} {matchArm.Item3} => {matchArm.Item2}({matchArm.Item3}),");
		}

		AppendLineIndented(sb, indentLevel + 2, "_ => throw new InvalidOperationException($\"Unknown variant: {GetType().Name}\")");
		AppendLineIndented(sb, indentLevel + 1, "};");
	}

	private static void GenerateHierarchicalVariantClasses(
		StringBuilder sb,
		UnionModel union,
		int indentLevel)
	{
		var variantKeyword = GetHierarchicalVariantKeyword(union);

		foreach (var variant in union.Variants)
		{
			var variantTypeName = $"{variant.Name}Variant";
			var ctorParameters = string.Join(", ", variant.Parameters.Select(p => $"{p.Type} {p.Name}"));
			var propertyLines = variant.Parameters
				.Select(p => $"public {p.Type} {FirstCharToUpper(p.Name)} {{ get; }}")
				.ToList();
			var assignments = variant.Parameters
				.Select(p => $"this.{FirstCharToUpper(p.Name)} = {p.Name};")
				.ToList();

			sb.AppendLine();
			AppendLineIndented(sb, indentLevel, $"public sealed {variantKeyword} {variantTypeName} : {union.ClassName}");
			AppendLineIndented(sb, indentLevel, "{");

			if (variant.Parameters.Count > 0)
			{
				AppendLineIndented(sb, indentLevel + 1, $"internal {variantTypeName}({ctorParameters})");
				AppendLineIndented(sb, indentLevel + 1, "{");
				foreach (var assignment in assignments)
				{
					AppendLineIndented(sb, indentLevel + 2, assignment);
				}
				AppendLineIndented(sb, indentLevel + 1, "}");
				sb.AppendLine();
				foreach (var propertyLine in propertyLines)
				{
					AppendLineIndented(sb, indentLevel + 1, propertyLine);
				}
			}
			else
			{
				AppendLineIndented(sb, indentLevel + 1, $"internal {variantTypeName}() {{ }}");
			}

			AppendLineIndented(sb, indentLevel, "}");
		}
	}

	private static void GenerateHierarchicalJsonConverterClass(
		StringBuilder sb,
		UnionModel union,
		string discriminatorFieldName,
		int indentLevel)
	{
		var className = union.ClassName;
		var converterName = $"{className}JsonConverter";

		AppendLineIndented(sb, indentLevel, $"public class {converterName} : System.Text.Json.Serialization.JsonConverter<{className}>");
		AppendLineIndented(sb, indentLevel, "{");
		EmitStjConverterHelpers(sb, indentLevel + 1);
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 1, $"public override {className}? Read(ref System.Text.Json.Utf8JsonReader reader, System.Type typeToConvert, System.Text.Json.JsonSerializerOptions options)");
		AppendLineIndented(sb, indentLevel + 1, "{");
		AppendLineIndented(sb, indentLevel + 2, "using var doc = System.Text.Json.JsonDocument.ParseValue(ref reader);");
		AppendLineIndented(sb, indentLevel + 2, "var root = doc.RootElement;");
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 2, "string? typeName = null;");
		AppendLineIndented(sb, indentLevel + 2, $"if (root.TryGetProperty(\"{discriminatorFieldName}\", out var typeElement))");
		AppendLineIndented(sb, indentLevel + 2, "{");
		AppendLineIndented(sb, indentLevel + 3, "typeName = typeElement.GetString();");
		AppendLineIndented(sb, indentLevel + 2, "}");
		AppendLineIndented(sb, indentLevel + 2, "else");
		AppendLineIndented(sb, indentLevel + 2, "{");
		AppendLineIndented(sb, indentLevel + 3, "try");
		AppendLineIndented(sb, indentLevel + 3, "{");
		AppendLineIndented(sb, indentLevel + 4, "typeName = InferVariantFromProperties(root, options);");
		AppendLineIndented(sb, indentLevel + 3, "}");
		AppendLineIndented(sb, indentLevel + 3, "catch (System.Text.Json.JsonException)");
		AppendLineIndented(sb, indentLevel + 3, "{");
		AppendLineIndented(sb, indentLevel + 4, "typeName = null;");
		AppendLineIndented(sb, indentLevel + 3, "}");
		AppendLineIndented(sb, indentLevel + 2, "}");
		sb.AppendLine();

		var isFirst = true;
		foreach (var variant in union.Variants)
		{
			var keyword = isFirst ? "if" : "else if";
			AppendLineIndented(sb, indentLevel + 2, $"{keyword} (string.Equals(typeName, \"{variant.Name}\", System.StringComparison.OrdinalIgnoreCase))");
			AppendLineIndented(sb, indentLevel + 2, "{");
			if (variant.Parameters.Count > 0)
			{
				foreach (var param in variant.Parameters)
				{
					AppendLineIndented(sb, indentLevel + 3, $"var @{param.Name} = System.Text.Json.JsonSerializer.Deserialize<{param.Type}>(GetMember(root, ResolvePropertyName(\"{FirstCharToUpper(param.Name)}\", \"{param.Name}\", options), options).GetRawText(), options)!;");
				}
				var args = string.Join(", ", variant.Parameters.Select(p => $"@{p.Name}"));
				AppendLineIndented(sb, indentLevel + 3, $"return {className}.{variant.Name}({args});");
			}
			else
			{
				AppendLineIndented(sb, indentLevel + 3, $"return {className}.{variant.Name}();");
			}
			AppendLineIndented(sb, indentLevel + 2, "}");
			isFirst = false;
		}

		foreach (var subUnion in union.SubUnions)
		{
			var subUnionTypeName = GetNestedTypeReference(className, subUnion);
			AppendLineIndented(sb, indentLevel + 2, "try");
			AppendLineIndented(sb, indentLevel + 2, "{");
			AppendLineIndented(sb, indentLevel + 3, $"return System.Text.Json.JsonSerializer.Deserialize<{subUnionTypeName}>(root.GetRawText(), options)!;");
			AppendLineIndented(sb, indentLevel + 2, "}");
			AppendLineIndented(sb, indentLevel + 2, "catch (System.Text.Json.JsonException)");
			AppendLineIndented(sb, indentLevel + 2, "{");
			AppendLineIndented(sb, indentLevel + 3, "// Try the next sub-union family.");
			AppendLineIndented(sb, indentLevel + 2, "}");
		}

		AppendLineIndented(sb, indentLevel + 2, "throw new System.Text.Json.JsonException($\"Unknown variant type: {typeName}\");");
		AppendLineIndented(sb, indentLevel + 1, "}");
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 1, "private static string InferVariantFromProperties(System.Text.Json.JsonElement root, System.Text.Json.JsonSerializerOptions options)");
		AppendLineIndented(sb, indentLevel + 1, "{");
		AppendLineIndented(sb, indentLevel + 2, "var comparer = options.PropertyNameCaseInsensitive ? System.StringComparer.OrdinalIgnoreCase : System.StringComparer.Ordinal;");
		AppendLineIndented(sb, indentLevel + 2, "var properties = new System.Collections.Generic.HashSet<string>(comparer);");
		AppendLineIndented(sb, indentLevel + 2, "foreach (var prop in root.EnumerateObject())");
		AppendLineIndented(sb, indentLevel + 3, "properties.Add(prop.Name);");
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 2, "string? match = null;");
		foreach (var variant in union.Variants)
		{
			var parameters = variant.Parameters.ToList();
			if (parameters.Count == 0)
			{
				AppendLineIndented(sb, indentLevel + 2, "if (properties.Count == 0)");
			}
			else
			{
				var conditions = string.Join(" && ", parameters.Select(p => $"properties.Contains(ResolvePropertyName(\"{FirstCharToUpper(p.Name)}\", \"{p.Name}\", options))"));
				AppendLineIndented(sb, indentLevel + 2, $"if ({conditions})");
			}
			AppendLineIndented(sb, indentLevel + 2, "{");
			AppendLineIndented(sb, indentLevel + 3, "if (match != null) throw new System.Text.Json.JsonException(\"Ambiguous variant: multiple variants match the provided properties. Include the discriminator field to disambiguate.\");");
			AppendLineIndented(sb, indentLevel + 3, $"match = \"{variant.Name}\";");
			AppendLineIndented(sb, indentLevel + 2, "}");
		}
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 2, "if (match == null) throw new System.Text.Json.JsonException(\"Unable to infer variant type from properties. Include the discriminator field.\");");
		AppendLineIndented(sb, indentLevel + 2, "return match;");
		AppendLineIndented(sb, indentLevel + 1, "}");
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 1, $"public override void Write(System.Text.Json.Utf8JsonWriter writer, {className} value, System.Text.Json.JsonSerializerOptions options)");
		AppendLineIndented(sb, indentLevel + 1, "{");
		AppendLineIndented(sb, indentLevel + 2, "switch (value)");
		AppendLineIndented(sb, indentLevel + 2, "{");
		foreach (var variant in union.Variants)
		{
			var valueName = GetHandlerValueName(variant.Name);
			var variantTypeName = GetNestedTypeReference(className, $"{variant.Name}Variant");
			AppendLineIndented(sb, indentLevel + 3, $"case {variantTypeName} {valueName}:");
			AppendLineIndented(sb, indentLevel + 4, "writer.WriteStartObject();");
			AppendLineIndented(sb, indentLevel + 4, $"writer.WriteString(\"{discriminatorFieldName}\", \"{variant.Name}\");");
			foreach (var param in variant.Parameters)
			{
				AppendLineIndented(sb, indentLevel + 4, $"writer.WritePropertyName(ResolvePropertyName(\"{FirstCharToUpper(param.Name)}\", \"{param.Name}\", options));");
				AppendLineIndented(sb, indentLevel + 4, $"System.Text.Json.JsonSerializer.Serialize(writer, {valueName}.{FirstCharToUpper(param.Name)}, options);");
			}
			AppendLineIndented(sb, indentLevel + 4, "writer.WriteEndObject();");
			AppendLineIndented(sb, indentLevel + 4, "return;");
		}
		foreach (var subUnion in union.SubUnions)
		{
			var valueName = GetHandlerValueName(subUnion);
			var subUnionTypeName = GetNestedTypeReference(className, subUnion);
			AppendLineIndented(sb, indentLevel + 3, $"case {subUnionTypeName} {valueName}:");
			AppendLineIndented(sb, indentLevel + 4, $"System.Text.Json.JsonSerializer.Serialize<{subUnionTypeName}>(writer, {valueName}, options);");
			AppendLineIndented(sb, indentLevel + 4, "return;");
		}
		AppendLineIndented(sb, indentLevel + 3, "default:");
		AppendLineIndented(sb, indentLevel + 4, "throw new System.Text.Json.JsonException($\"Unknown variant: {value.GetType().Name}\");");
		AppendLineIndented(sb, indentLevel + 2, "}");
		AppendLineIndented(sb, indentLevel + 1, "}");
		AppendLineIndented(sb, indentLevel, "}");
	}

	private static void GenerateHierarchicalNewtonsoftJsonConverterClass(
		StringBuilder sb,
		UnionModel union,
		string discriminatorFieldName,
		int indentLevel)
	{
		var className = union.ClassName;
		var converterName = $"{className}NewtonsoftJsonConverter";

		AppendLineIndented(sb, indentLevel, $"public class {converterName} : Newtonsoft.Json.JsonConverter<{className}>");
		AppendLineIndented(sb, indentLevel, "{");
		EmitNewtonsoftConverterHelpers(sb, indentLevel + 1);
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 1, $"public override {className}? ReadJson(Newtonsoft.Json.JsonReader reader, System.Type objectType, {className}? existingValue, bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer)");
		AppendLineIndented(sb, indentLevel + 1, "{");
		AppendLineIndented(sb, indentLevel + 2, "if (reader.TokenType == Newtonsoft.Json.JsonToken.Null) return null;");
		AppendLineIndented(sb, indentLevel + 2, "var obj = Newtonsoft.Json.Linq.JObject.Load(reader);");
		AppendLineIndented(sb, indentLevel + 2, $"var typeName = (string?)obj[\"{discriminatorFieldName}\"];");
		AppendLineIndented(sb, indentLevel + 2, "if (typeName == null)");
		AppendLineIndented(sb, indentLevel + 2, "{");
		AppendLineIndented(sb, indentLevel + 3, "try");
		AppendLineIndented(sb, indentLevel + 3, "{");
		AppendLineIndented(sb, indentLevel + 4, "typeName = InferVariantFromProperties(obj, serializer);");
		AppendLineIndented(sb, indentLevel + 3, "}");
		AppendLineIndented(sb, indentLevel + 3, "catch (Newtonsoft.Json.JsonSerializationException)");
		AppendLineIndented(sb, indentLevel + 3, "{");
		AppendLineIndented(sb, indentLevel + 4, "typeName = null;");
		AppendLineIndented(sb, indentLevel + 3, "}");
		AppendLineIndented(sb, indentLevel + 2, "}");
		sb.AppendLine();

		var isFirst = true;
		foreach (var variant in union.Variants)
		{
			var keyword = isFirst ? "if" : "else if";
			AppendLineIndented(sb, indentLevel + 2, $"{keyword} (string.Equals(typeName, \"{variant.Name}\", System.StringComparison.OrdinalIgnoreCase))");
			AppendLineIndented(sb, indentLevel + 2, "{");
			if (variant.Parameters.Count > 0)
			{
				foreach (var param in variant.Parameters)
				{
					AppendLineIndented(sb, indentLevel + 3, $"var @{param.Name} = obj.GetValue(ResolvePropertyName(\"{FirstCharToUpper(param.Name)}\", \"{param.Name}\", typeof({GetNestedTypeReference(className, $"{variant.Name}Variant")}), serializer), System.StringComparison.OrdinalIgnoreCase)!.ToObject<{param.Type}>(serializer)!;");
				}
				var args = string.Join(", ", variant.Parameters.Select(p => $"@{p.Name}"));
				AppendLineIndented(sb, indentLevel + 3, $"return {className}.{variant.Name}({args});");
			}
			else
			{
				AppendLineIndented(sb, indentLevel + 3, $"return {className}.{variant.Name}();");
			}
			AppendLineIndented(sb, indentLevel + 2, "}");
			isFirst = false;
		}

		foreach (var subUnion in union.SubUnions)
		{
			var subUnionTypeName = GetNestedTypeReference(className, subUnion);
			AppendLineIndented(sb, indentLevel + 2, "try");
			AppendLineIndented(sb, indentLevel + 2, "{");
			AppendLineIndented(sb, indentLevel + 3, $"return obj.ToObject<{subUnionTypeName}>(serializer)!;");
			AppendLineIndented(sb, indentLevel + 2, "}");
			AppendLineIndented(sb, indentLevel + 2, "catch (Newtonsoft.Json.JsonSerializationException)");
			AppendLineIndented(sb, indentLevel + 2, "{");
			AppendLineIndented(sb, indentLevel + 3, "// Try the next sub-union family.");
			AppendLineIndented(sb, indentLevel + 2, "}");
		}

		AppendLineIndented(sb, indentLevel + 2, "throw new Newtonsoft.Json.JsonSerializationException($\"Unknown variant type: {typeName}\");");
		AppendLineIndented(sb, indentLevel + 1, "}");
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 1, "private static string InferVariantFromProperties(Newtonsoft.Json.Linq.JObject obj, Newtonsoft.Json.JsonSerializer serializer)");
		AppendLineIndented(sb, indentLevel + 1, "{");
		AppendLineIndented(sb, indentLevel + 2, "var properties = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);");
		AppendLineIndented(sb, indentLevel + 2, "foreach (var prop in obj.Properties())");
		AppendLineIndented(sb, indentLevel + 3, "properties.Add(prop.Name);");
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 2, "string? match = null;");
		foreach (var variant in union.Variants)
		{
			var parameters = variant.Parameters.ToList();
			var ownerType = GetNestedTypeReference(className, $"{variant.Name}Variant");
			if (parameters.Count == 0)
			{
				AppendLineIndented(sb, indentLevel + 2, "if (properties.Count == 0)");
			}
			else
			{
				var conditions = string.Join(" && ", parameters.Select(p => $"properties.Contains(ResolvePropertyName(\"{FirstCharToUpper(p.Name)}\", \"{p.Name}\", typeof({ownerType}), serializer))"));
				AppendLineIndented(sb, indentLevel + 2, $"if ({conditions})");
			}
			AppendLineIndented(sb, indentLevel + 2, "{");
			AppendLineIndented(sb, indentLevel + 3, "if (match != null) throw new Newtonsoft.Json.JsonSerializationException(\"Ambiguous variant: multiple variants match the provided properties. Include the discriminator field to disambiguate.\");");
			AppendLineIndented(sb, indentLevel + 3, $"match = \"{variant.Name}\";");
			AppendLineIndented(sb, indentLevel + 2, "}");
		}
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 2, "if (match == null) throw new Newtonsoft.Json.JsonSerializationException(\"Unable to infer variant type from properties. Include the discriminator field.\");");
		AppendLineIndented(sb, indentLevel + 2, "return match;");
		AppendLineIndented(sb, indentLevel + 1, "}");
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 1, $"public override void WriteJson(Newtonsoft.Json.JsonWriter writer, {className}? value, Newtonsoft.Json.JsonSerializer serializer)");
		AppendLineIndented(sb, indentLevel + 1, "{");
		AppendLineIndented(sb, indentLevel + 2, "if (value == null) { writer.WriteNull(); return; }");
		AppendLineIndented(sb, indentLevel + 2, "switch (value)");
		AppendLineIndented(sb, indentLevel + 2, "{");
		foreach (var variant in union.Variants)
		{
			var valueName = GetHandlerValueName(variant.Name);
			var variantTypeName = GetNestedTypeReference(className, $"{variant.Name}Variant");
			AppendLineIndented(sb, indentLevel + 3, $"case {variantTypeName} {valueName}:");
			AppendLineIndented(sb, indentLevel + 4, "writer.WriteStartObject();");
			AppendLineIndented(sb, indentLevel + 4, $"writer.WritePropertyName(\"{discriminatorFieldName}\");");
			AppendLineIndented(sb, indentLevel + 4, $"writer.WriteValue(\"{variant.Name}\");");
			foreach (var param in variant.Parameters)
			{
				AppendLineIndented(sb, indentLevel + 4, $"writer.WritePropertyName(ResolvePropertyName(\"{FirstCharToUpper(param.Name)}\", \"{param.Name}\", typeof({GetNestedTypeReference(className, $"{variant.Name}Variant")}), serializer));");
				AppendLineIndented(sb, indentLevel + 4, $"serializer.Serialize(writer, {valueName}.{FirstCharToUpper(param.Name)});");
			}
			AppendLineIndented(sb, indentLevel + 4, "writer.WriteEndObject();");
			AppendLineIndented(sb, indentLevel + 4, "return;");
		}
		foreach (var subUnion in union.SubUnions)
		{
			var valueName = GetHandlerValueName(subUnion);
			var subUnionTypeName = GetNestedTypeReference(className, subUnion);
			AppendLineIndented(sb, indentLevel + 3, $"case {subUnionTypeName} {valueName}:");
			AppendLineIndented(sb, indentLevel + 4, $"serializer.Serialize(writer, {valueName});");
			AppendLineIndented(sb, indentLevel + 4, "return;");
		}
		AppendLineIndented(sb, indentLevel + 3, "default:");
		AppendLineIndented(sb, indentLevel + 4, "throw new Newtonsoft.Json.JsonSerializationException($\"Unknown variant: {value.GetType().Name}\");");
		AppendLineIndented(sb, indentLevel + 2, "}");
		AppendLineIndented(sb, indentLevel + 1, "}");
		AppendLineIndented(sb, indentLevel, "}");
	}

	private static void GenerateJsonConverterClass(
		StringBuilder sb,
		string className,
		EquatableArray<VariantModel> variants,
		string discriminatorFieldName,
		int indentLevel)
	{
		var converterName = $"{className}JsonConverter";

		AppendLineIndented(sb, indentLevel, $"public class {converterName} : System.Text.Json.Serialization.JsonConverter<{className}>");
		AppendLineIndented(sb, indentLevel, "{");
		EmitStjConverterHelpers(sb, indentLevel + 1);
		sb.AppendLine();

		AppendLineIndented(sb, indentLevel + 1, $"public override {className}? Read(ref System.Text.Json.Utf8JsonReader reader, System.Type typeToConvert, System.Text.Json.JsonSerializerOptions options)");
		AppendLineIndented(sb, indentLevel + 1, "{");
		AppendLineIndented(sb, indentLevel + 2, "using var doc = System.Text.Json.JsonDocument.ParseValue(ref reader);");
		AppendLineIndented(sb, indentLevel + 2, "var root = doc.RootElement;");
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 2, "string? typeName = null;");
		AppendLineIndented(sb, indentLevel + 2, $"if (root.TryGetProperty(\"{discriminatorFieldName}\", out var typeElement))");
		AppendLineIndented(sb, indentLevel + 2, "{");
		AppendLineIndented(sb, indentLevel + 3, "typeName = typeElement.GetString();");
		AppendLineIndented(sb, indentLevel + 2, "}");
		AppendLineIndented(sb, indentLevel + 2, "else");
		AppendLineIndented(sb, indentLevel + 2, "{");
		AppendLineIndented(sb, indentLevel + 3, "typeName = InferVariantFromProperties(root, options);");
		AppendLineIndented(sb, indentLevel + 2, "}");
		sb.AppendLine();
		var isFirst = true;
		foreach (var variant in variants)
		{
			var variantName = variant.Name;
			var keyword = isFirst ? "if" : "else if";
			AppendLineIndented(sb, indentLevel + 2, $"{keyword} (string.Equals(typeName, \"{variantName}\", System.StringComparison.OrdinalIgnoreCase))");
			AppendLineIndented(sb, indentLevel + 2, "{");

			if (variant.Parameters.Count > 0)
			{
				foreach (var param in variant.Parameters)
				{
					var paramName = param.Name;
					var paramType = param.Type;
					var paramProp = FirstCharToUpper(param.Name);
					AppendLineIndented(sb, indentLevel + 3, $"var @{paramName} = System.Text.Json.JsonSerializer.Deserialize<{paramType}>(GetMember(root, ResolvePropertyName(\"{paramProp}\", \"{paramName}\", options), options).GetRawText(), options)!;");
				}

				var args = string.Join(", ", variant.Parameters.Select(p => $"@{p.Name}"));
				AppendLineIndented(sb, indentLevel + 3, $"return {className}.{variantName}({args});");
			}
			else
			{
				AppendLineIndented(sb, indentLevel + 3, $"return {className}.{variantName}();");
			}

			AppendLineIndented(sb, indentLevel + 2, "}");
			isFirst = false;
		}

		AppendLineIndented(sb, indentLevel + 2, "else");
		AppendLineIndented(sb, indentLevel + 2, "{");
		AppendLineIndented(sb, indentLevel + 3, "throw new System.Text.Json.JsonException($\"Unknown variant type: {typeName}\");");
		AppendLineIndented(sb, indentLevel + 2, "}");
		AppendLineIndented(sb, indentLevel + 1, "}");

		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 1, "private static string InferVariantFromProperties(System.Text.Json.JsonElement root, System.Text.Json.JsonSerializerOptions options)");
		AppendLineIndented(sb, indentLevel + 1, "{");
		AppendLineIndented(sb, indentLevel + 2, "var comparer = options.PropertyNameCaseInsensitive ? System.StringComparer.OrdinalIgnoreCase : System.StringComparer.Ordinal;");
		AppendLineIndented(sb, indentLevel + 2, "var properties = new System.Collections.Generic.HashSet<string>(comparer);");
		AppendLineIndented(sb, indentLevel + 2, "foreach (var prop in root.EnumerateObject())");
		AppendLineIndented(sb, indentLevel + 3, "properties.Add(prop.Name);");
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 2, "string? match = null;");

		foreach (var variant in variants)
		{
			var variantName = variant.Name;
			var parameters = variant.Parameters.ToList();

			if (parameters.Count == 0)
			{
				AppendLineIndented(sb, indentLevel + 2, "if (properties.Count == 0)");
			}
			else
			{
				var conditions = string.Join(" && ", parameters.Select(p => $"properties.Contains(ResolvePropertyName(\"{FirstCharToUpper(p.Name)}\", \"{p.Name}\", options))"));
				AppendLineIndented(sb, indentLevel + 2, $"if ({conditions})");
			}

			AppendLineIndented(sb, indentLevel + 2, "{");
			AppendLineIndented(sb, indentLevel + 3, "if (match != null) throw new System.Text.Json.JsonException(\"Ambiguous variant: multiple variants match the provided properties. Include the discriminator field to disambiguate.\");");
			AppendLineIndented(sb, indentLevel + 3, $"match = \"{variantName}\";");
			AppendLineIndented(sb, indentLevel + 2, "}");
		}

		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 2, "if (match == null) throw new System.Text.Json.JsonException(\"Unable to infer variant type from properties. Include the discriminator field.\");");
		AppendLineIndented(sb, indentLevel + 2, "return match;");
		AppendLineIndented(sb, indentLevel + 1, "}");

		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 1, $"public override void Write(System.Text.Json.Utf8JsonWriter writer, {className} value, System.Text.Json.JsonSerializerOptions options)");
		AppendLineIndented(sb, indentLevel + 1, "{");
		AppendLineIndented(sb, indentLevel + 2, "writer.WriteStartObject();");
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 2, "value.Switch(");

		for (int i = 0; i < variants.Count; i++)
		{
			var variant = variants[i];
			var variantName = variant.Name;
			var lambdaParam = $"variant{i}";
			var comma = i < variants.Count - 1 ? "," : "";

			AppendLineIndented(sb, indentLevel + 3, $"{lambdaParam} =>");
			AppendLineIndented(sb, indentLevel + 3, "{");
			AppendLineIndented(sb, indentLevel + 4, $"writer.WriteString(\"{discriminatorFieldName}\", \"{variantName}\");");

			foreach (var param in variant.Parameters)
			{
				var propName = FirstCharToUpper(param.Name);
				var jsonName = param.Name;
				AppendLineIndented(sb, indentLevel + 4, $"writer.WritePropertyName(ResolvePropertyName(\"{propName}\", \"{jsonName}\", options));");
				AppendLineIndented(sb, indentLevel + 4, $"System.Text.Json.JsonSerializer.Serialize(writer, {lambdaParam}.{propName}, options);");
			}

			AppendLineIndented(sb, indentLevel + 3, $"}}{comma}");
		}

		AppendLineIndented(sb, indentLevel + 2, ");");
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 2, "writer.WriteEndObject();");
		AppendLineIndented(sb, indentLevel + 1, "}");

		AppendLineIndented(sb, indentLevel, "}");
	}

	private static void GenerateNewtonsoftJsonConverterClass(
		StringBuilder sb,
		string className,
		EquatableArray<VariantModel> variants,
		string discriminatorFieldName,
		int indentLevel)
	{
		var converterName = $"{className}NewtonsoftJsonConverter";

		AppendLineIndented(sb, indentLevel, $"public class {converterName} : Newtonsoft.Json.JsonConverter<{className}>");
		AppendLineIndented(sb, indentLevel, "{");
		EmitNewtonsoftConverterHelpers(sb, indentLevel + 1);

		AppendLineIndented(sb, indentLevel + 1, $"public override {className}? ReadJson(Newtonsoft.Json.JsonReader reader, System.Type objectType, {className}? existingValue, bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer)");
		AppendLineIndented(sb, indentLevel + 1, "{");
		AppendLineIndented(sb, indentLevel + 2, "if (reader.TokenType == Newtonsoft.Json.JsonToken.Null) return null;");
		AppendLineIndented(sb, indentLevel + 2, "var obj = Newtonsoft.Json.Linq.JObject.Load(reader);");
		AppendLineIndented(sb, indentLevel + 2, $"var typeName = (string?)obj[\"{discriminatorFieldName}\"];");
		AppendLineIndented(sb, indentLevel + 2, "if (typeName == null)");
		AppendLineIndented(sb, indentLevel + 2, "{");
		AppendLineIndented(sb, indentLevel + 3, "typeName = InferVariantFromProperties(obj, serializer);");
		AppendLineIndented(sb, indentLevel + 2, "}");
		sb.AppendLine();
		var isFirstNewtonsoft = true;
		foreach (var variant in variants)
		{
			var variantName = variant.Name;
			var keyword = isFirstNewtonsoft ? "if" : "else if";
			AppendLineIndented(sb, indentLevel + 2, $"{keyword} (string.Equals(typeName, \"{variantName}\", System.StringComparison.OrdinalIgnoreCase))");
			AppendLineIndented(sb, indentLevel + 2, "{");

			if (variant.Parameters.Count > 0)
			{
				foreach (var param in variant.Parameters)
				{
					var paramName = param.Name;
					var paramType = param.Type;
					AppendLineIndented(sb, indentLevel + 3, $"var @{paramName} = obj.GetValue(ResolvePropertyName(\"{FirstCharToUpper(paramName)}\", \"{paramName}\", typeof({GetNestedTypeReference(className, $"{variantName}Variant")}), serializer), System.StringComparison.OrdinalIgnoreCase)!.ToObject<{paramType}>(serializer)!;");
				}

				var args = string.Join(", ", variant.Parameters.Select(p => $"@{p.Name}"));
				AppendLineIndented(sb, indentLevel + 3, $"return {className}.{variantName}({args});");
			}
			else
			{
				AppendLineIndented(sb, indentLevel + 3, $"return {className}.{variantName}();");
			}

			AppendLineIndented(sb, indentLevel + 2, "}");
			isFirstNewtonsoft = false;
		}

		AppendLineIndented(sb, indentLevel + 2, "else");
		AppendLineIndented(sb, indentLevel + 2, "{");
		AppendLineIndented(sb, indentLevel + 3, "throw new Newtonsoft.Json.JsonSerializationException($\"Unknown variant type: {typeName}\");");
		AppendLineIndented(sb, indentLevel + 2, "}");
		AppendLineIndented(sb, indentLevel + 1, "}");

		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 1, "private static string InferVariantFromProperties(Newtonsoft.Json.Linq.JObject obj, Newtonsoft.Json.JsonSerializer serializer)");
		AppendLineIndented(sb, indentLevel + 1, "{");
		AppendLineIndented(sb, indentLevel + 2, "var properties = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);");
		AppendLineIndented(sb, indentLevel + 2, "foreach (var prop in obj.Properties())");
		AppendLineIndented(sb, indentLevel + 3, "properties.Add(prop.Name);");
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 2, "string? match = null;");

		foreach (var variant in variants)
		{
			var variantName = variant.Name;
			var parameters = variant.Parameters.ToList();
			var ownerType = GetNestedTypeReference(className, $"{variantName}Variant");

			if (parameters.Count == 0)
			{
				AppendLineIndented(sb, indentLevel + 2, "if (properties.Count == 0)");
			}
			else
			{
				var conditions = string.Join(" && ", parameters.Select(p => $"properties.Contains(ResolvePropertyName(\"{FirstCharToUpper(p.Name)}\", \"{p.Name}\", typeof({ownerType}), serializer))"));
				AppendLineIndented(sb, indentLevel + 2, $"if ({conditions})");
			}

			AppendLineIndented(sb, indentLevel + 2, "{");
			AppendLineIndented(sb, indentLevel + 3, "if (match != null) throw new Newtonsoft.Json.JsonSerializationException(\"Ambiguous variant: multiple variants match the provided properties. Include the discriminator field to disambiguate.\");");
			AppendLineIndented(sb, indentLevel + 3, $"match = \"{variantName}\";");
			AppendLineIndented(sb, indentLevel + 2, "}");
		}

		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 2, "if (match == null) throw new Newtonsoft.Json.JsonSerializationException(\"Unable to infer variant type from properties. Include the discriminator field.\");");
		AppendLineIndented(sb, indentLevel + 2, "return match;");
		AppendLineIndented(sb, indentLevel + 1, "}");

		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 1, $"public override void WriteJson(Newtonsoft.Json.JsonWriter writer, {className}? value, Newtonsoft.Json.JsonSerializer serializer)");
		AppendLineIndented(sb, indentLevel + 1, "{");
		AppendLineIndented(sb, indentLevel + 2, "if (value == null) { writer.WriteNull(); return; }");
		AppendLineIndented(sb, indentLevel + 2, "writer.WriteStartObject();");
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 2, "value.Switch(");

		for (int i = 0; i < variants.Count; i++)
		{
			var variant = variants[i];
			var variantName = variant.Name;
			var lambdaParam = $"variant{i}";
			var comma = i < variants.Count - 1 ? "," : "";

			AppendLineIndented(sb, indentLevel + 3, $"{lambdaParam} =>");
			AppendLineIndented(sb, indentLevel + 3, "{");
			AppendLineIndented(sb, indentLevel + 4, $"writer.WritePropertyName(\"{discriminatorFieldName}\");");
			AppendLineIndented(sb, indentLevel + 4, $"writer.WriteValue(\"{variantName}\");");

			foreach (var param in variant.Parameters)
			{
				var jsonName = param.Name;
				var propName = FirstCharToUpper(param.Name);
				AppendLineIndented(sb, indentLevel + 4, $"writer.WritePropertyName(ResolvePropertyName(\"{propName}\", \"{jsonName}\", typeof({GetNestedTypeReference(className, $"{variantName}Variant")}), serializer));");
				AppendLineIndented(sb, indentLevel + 4, $"serializer.Serialize(writer, {lambdaParam}.{propName});");
			}

			AppendLineIndented(sb, indentLevel + 3, $"}}{comma}");
		}

		AppendLineIndented(sb, indentLevel + 2, ");");
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 2, "writer.WriteEndObject();");
		AppendLineIndented(sb, indentLevel + 1, "}");

		AppendLineIndented(sb, indentLevel, "}");
	}

	private static void EmitStjConverterHelpers(StringBuilder sb, int indentLevel)
	{
		AppendLineIndented(sb, indentLevel, "private static string ResolvePropertyName(string clrName, string fallback, System.Text.Json.JsonSerializerOptions options)");
		AppendLineIndented(sb, indentLevel + 1, "=> options.PropertyNamingPolicy is { } policy ? policy.ConvertName(clrName) : fallback;");
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel, "private static System.Text.Json.JsonElement GetMember(System.Text.Json.JsonElement root, string name, System.Text.Json.JsonSerializerOptions options)");
		AppendLineIndented(sb, indentLevel, "{");
		AppendLineIndented(sb, indentLevel + 1, "if (root.TryGetProperty(name, out var element)) return element;");
		AppendLineIndented(sb, indentLevel + 1, "if (options.PropertyNameCaseInsensitive)");
		AppendLineIndented(sb, indentLevel + 1, "{");
		AppendLineIndented(sb, indentLevel + 2, "foreach (var property in root.EnumerateObject())");
		AppendLineIndented(sb, indentLevel + 2, "{");
		AppendLineIndented(sb, indentLevel + 3, "if (string.Equals(property.Name, name, System.StringComparison.OrdinalIgnoreCase)) return property.Value;");
		AppendLineIndented(sb, indentLevel + 2, "}");
		AppendLineIndented(sb, indentLevel + 1, "}");
		AppendLineIndented(sb, indentLevel + 1, "throw new System.Text.Json.JsonException($\"Property '{name}' not found.\");");
		AppendLineIndented(sb, indentLevel, "}");
	}

	private static void EmitNewtonsoftConverterHelpers(StringBuilder sb, int indentLevel)
	{
		AppendLineIndented(sb, indentLevel, "private static string ResolvePropertyName(string clrName, string fallback, System.Type owner, Newtonsoft.Json.JsonSerializer serializer)");
		AppendLineIndented(sb, indentLevel, "{");
		AppendLineIndented(sb, indentLevel + 1, "if (serializer.ContractResolver.ResolveContract(owner) is Newtonsoft.Json.Serialization.JsonObjectContract objectContract)");
		AppendLineIndented(sb, indentLevel + 1, "{");
		AppendLineIndented(sb, indentLevel + 2, "foreach (var property in objectContract.Properties)");
		AppendLineIndented(sb, indentLevel + 2, "{");
		AppendLineIndented(sb, indentLevel + 3, "if (property.UnderlyingName == clrName)");
		AppendLineIndented(sb, indentLevel + 4, "return property.PropertyName != null && property.PropertyName != clrName ? property.PropertyName : fallback;");
		AppendLineIndented(sb, indentLevel + 2, "}");
		AppendLineIndented(sb, indentLevel + 1, "}");
		AppendLineIndented(sb, indentLevel + 1, "return fallback;");
		AppendLineIndented(sb, indentLevel, "}");
	}

	private static void AppendLineIndented(StringBuilder sb, int indentLevel, string text) =>
		sb.Append(Indent(indentLevel)).AppendLine(text);

	private static string Indent(int indentLevel) =>
		new(' ', indentLevel * 4);

	private static string GetTypeDeclarationPrefix(UnionModel union, bool includeAbstractModifier)
	{
		var accessModifier = string.IsNullOrWhiteSpace(union.AccessModifier)
			? string.Empty
			: union.AccessModifier + " ";
		var abstractModifier = includeAbstractModifier && union.IsAbstract ? "abstract " : string.Empty;
		return $"{accessModifier}{abstractModifier}partial {union.TypeKeyword} {union.ClassName}";
	}

	private static string GetHierarchicalVariantKeyword(UnionModel union) =>
		union.TypeKeyword == "record" ? "record" : "class";

	private static string GetHandlerName(string typeName)
	{
		var normalizedName = typeName.EndsWith("Variant", StringComparison.Ordinal)
			? typeName.Substring(0, typeName.Length - "Variant".Length)
			: typeName;
		return ToCamelCase(normalizedName);
	}

	private static string GetHandlerValueName(string typeName) =>
		GetHandlerName(typeName) + "Value";

	private static string GetNestedTypeReference(string unionClassName, string nestedTypeName) =>
		$"{unionClassName}.{nestedTypeName}";

	private static string ToCamelCase(string value) =>
		string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value.Substring(1);

	private static string FirstCharToUpper(string input) =>
		string.IsNullOrEmpty(input) ? input : char.ToUpper(input[0]) + input.Substring(1);
}

internal readonly record struct FrameworkSupport(
	bool HasNewtonsoftJson,
	bool HasSystemTextJson,
	bool HasAspNetValidation);

internal readonly record struct UnionConfig(
	bool? SuppressValidation,
	string DiscriminatorFieldName,
	bool? GenerateJsonConverter,
	bool? GenerateNewtonsoftJsonConverter);

internal readonly record struct ContainingTypeInfo(
	string Name,
	string Keyword,
	string AccessModifier,
	bool IsPartial,
	Location? Location);

internal sealed record VariantModel(string Name, EquatableArray<VariantParameter> Parameters);

internal readonly record struct VariantParameter(string Name, string Type);

internal sealed record UnionModel(
	string Namespace,
	string ClassName,
	string TypeKeyword,
	string AccessModifier,
	EquatableArray<VariantModel> Variants,
	EquatableArray<ContainingTypeInfo> ContainingTypes,
	bool IsAbstract,
	EquatableArray<string> SubUnions,
	UnionConfig Config);

internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
	where T : IEquatable<T>
{
	private readonly T[]? _array;

	public EquatableArray(T[] array)
	{
		_array = array;
	}

	public int Count => _array?.Length ?? 0;

	public T this[int index] => _array![index];

	public bool Equals(EquatableArray<T> other)
	{
		var a = _array;
		var b = other._array;

		if (a is null) return b is null || b.Length == 0;
		if (b is null) return a.Length == 0;
		if (a.Length != b.Length) return false;

		for (int i = 0; i < a.Length; i++)
		{
			if (!a[i].Equals(b[i])) return false;
		}

		return true;
	}

	public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

	public override int GetHashCode()
	{
		if (_array is null) return 0;
		unchecked
		{
			int hash = 17;
			foreach (var item in _array)
			{
				hash = hash * 31 + (item?.GetHashCode() ?? 0);
			}
			return hash;
		}
	}

	public IEnumerator<T> GetEnumerator() =>
		((IEnumerable<T>)(_array ?? Array.Empty<T>())).GetEnumerator();

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
