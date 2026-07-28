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

	/// <summary>Reported when a <c>[DiscriminatedUnion]</c> type is not declared <c>abstract</c>.</summary>
	public const string NonAbstractUnionDiagnosticId = "ZGOR003";

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

	private static readonly DiagnosticDescriptor NonAbstractUnionDescriptor = new(
		id: NonAbstractUnionDiagnosticId,
		title: "Discriminated union must be abstract",
		messageFormat: "Discriminated union '{0}' must be declared abstract. Its variants are generated as nested types deriving from it.",
		category: "Zooper.Gorilla",
		defaultSeverity: DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	private static readonly DiagnosticDescriptor StructUnionDescriptor = new(
		id: "ZGOR004",
		title: "Discriminated union cannot be a struct",
		messageFormat: "Discriminated union '{0}' cannot be a struct. Variants are generated as types deriving from the union, and a struct cannot be inherited from.",
		category: "Zooper.Gorilla",
		defaultSeverity: DiagnosticSeverity.Error,
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
				HasSystemTextJson: compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonConverter") is not null));

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
			SubUnions: subUnions,
			DeclarationError: GetDeclarationError(classSymbol),
			Location: classSymbol.Locations.FirstOrDefault(),
			Config: config);
	}

	private static UnionDeclarationError GetDeclarationError(INamedTypeSymbol classSymbol)
	{
		// A union is an abstract base type whose variants derive from it, so a struct can
		// never be one and a non-abstract declaration disagrees with the type we emit.
		if (classSymbol.TypeKind == TypeKind.Struct)
		{
			return UnionDeclarationError.IsStruct;
		}

		return classSymbol.IsAbstract ? UnionDeclarationError.None : UnionDeclarationError.NotAbstract;
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
		string discriminatorFieldName = "$type";
		bool? generateJsonConverter = null;
		bool? generateNewtonsoftJsonConverter = null;

		if (attribute is not null)
		{
			foreach (var arg in attribute.NamedArguments)
			{
				switch (arg.Key)
				{
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
			DiscriminatorFieldName: discriminatorFieldName,
			GenerateJsonConverter: generateJsonConverter,
			GenerateNewtonsoftJsonConverter: generateNewtonsoftJsonConverter);
	}

	private static void Execute(SourceProductionContext context, UnionModel union, FrameworkSupport frameworkSupport)
	{
		try
		{
			if (union.DeclarationError != UnionDeclarationError.None)
			{
				context.ReportDiagnostic(Diagnostic.Create(
					union.DeclarationError == UnionDeclarationError.IsStruct
						? StructUnionDescriptor
						: NonAbstractUnionDescriptor,
					union.Location ?? Location.None,
					union.ClassName));
				return;
			}

			ReportContainingTypeDiagnostics(context, union);

			var generateJsonConverter = union.Config.GenerateJsonConverter ?? frameworkSupport.HasSystemTextJson;
			var generateNewtonsoftJsonConverter = union.Config.GenerateNewtonsoftJsonConverter ?? frameworkSupport.HasNewtonsoftJson;

			var source = GenerateSource(union, generateJsonConverter, generateNewtonsoftJsonConverter);
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
		// The hint name must be fully qualified so that same-named unions in different
		// namespaces (or same-named nested unions under same-named containers) do not
		// collide on the same generated file name. A hint collision causes the host to
		// drop or duplicate generated sources, surfacing as CS8795/CS0101/CS0111 in
		// consuming projects.
		var segments = new List<string>();

		if (!string.IsNullOrWhiteSpace(union.Namespace))
		{
			segments.Add(union.Namespace);
		}

		segments.AddRange(union.ContainingTypes.Select(static type => type.Name));
		segments.Add(union.ClassName);

		return $"{string.Join(".", segments)}.g.cs";
	}

	private static string GenerateSource(
		UnionModel union,
		bool generateJsonConverter,
		bool generateNewtonsoftJsonConverter)
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

		AppendLineIndented(sb, indentLevel, GetTypeDeclarationPrefix(union));
		AppendLineIndented(sb, indentLevel, "{");
		GenerateConstructor(sb, union.ClassName, indentLevel + 1);
		sb.AppendLine();
		GenerateVariantMethods(sb, union, indentLevel + 1);

		// A union declaring neither a [Variant] nor a sub-union has nothing to dispatch over, and
		// Match/Switch with an empty parameter list would not compile.
		if (GetSubtypes(union).Count > 0)
		{
			sb.AppendLine();
			GenerateMatchMethod(sb, union, indentLevel + 1);
			sb.AppendLine();
			GenerateSwitchMethod(sb, union, indentLevel + 1);
		}

		GenerateAccessors(sb, union, indentLevel + 1);
		GenerateVariantClasses(sb, union, indentLevel + 1);
		AppendLineIndented(sb, indentLevel, "}");

		if (generateJsonConverter)
		{
			sb.AppendLine();
			GenerateJsonConverterClass(sb, union, union.Config.DiscriminatorFieldName, indentLevel);
		}

		if (generateNewtonsoftJsonConverter)
		{
			sb.AppendLine();
			GenerateNewtonsoftJsonConverterClass(sb, union, union.Config.DiscriminatorFieldName, indentLevel);
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

	/// <summary>
	/// The union constructor is private, so nothing outside the union body can derive from it.
	/// Generated variants and user-declared sub-unions are nested and can still reach it.
	/// </summary>
	private static void GenerateConstructor(
		StringBuilder sb,
		string className,
		int indentLevel) =>
		AppendLineIndented(sb, indentLevel, $"private {className}() {{ }}");

	private static void GenerateVariantMethods(
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

	/// <summary>
	/// Every subtype a value can be: one generated variant type per <c>[Variant]</c> factory,
	/// plus every nested sub-union. This list is the source of Match/Switch exhaustiveness, and
	/// the accessors derive from it so they stay correct when a sub-union is added later.
	/// </summary>
	private static List<SubtypeModel> GetSubtypes(UnionModel union) =>
		union.Variants
			.Select(static variant => new SubtypeModel($"{variant.Name}Variant", variant.Name))
			.Concat(union.SubUnions.Select(static subUnion => new SubtypeModel(subUnion, subUnion)))
			.ToList();

	private static void GenerateMatchMethod(
		StringBuilder sb,
		UnionModel union,
		int indentLevel)
	{
		var subtypes = GetSubtypes(union);

		AppendLineIndented(sb, indentLevel, "public T Match<T>(");
		for (var i = 0; i < subtypes.Count; i++)
		{
			var suffix = i < subtypes.Count - 1 ? "," : ") =>";
			AppendLineIndented(sb, indentLevel + 1, $"Func<{subtypes[i].TypeName}, T> {subtypes[i].HandlerName}{suffix}");
		}
		AppendLineIndented(sb, indentLevel + 1, "this switch");
		AppendLineIndented(sb, indentLevel + 1, "{");

		foreach (var subtype in subtypes)
		{
			AppendLineIndented(sb, indentLevel + 2, $"{subtype.TypeName} {subtype.ValueName} => {subtype.HandlerName}({subtype.ValueName}),");
		}

		AppendLineIndented(sb, indentLevel + 2, "_ => throw new InvalidOperationException($\"Unknown variant: {GetType().Name}\")");
		AppendLineIndented(sb, indentLevel + 1, "};");
	}

	private static void GenerateSwitchMethod(
		StringBuilder sb,
		UnionModel union,
		int indentLevel)
	{
		var subtypes = GetSubtypes(union);

		AppendLineIndented(sb, indentLevel, "public void Switch(");
		for (var i = 0; i < subtypes.Count; i++)
		{
			var suffix = i < subtypes.Count - 1 ? "," : ")";
			AppendLineIndented(sb, indentLevel + 1, $"Action<{subtypes[i].TypeName}> {subtypes[i].HandlerName}{suffix}");
		}
		AppendLineIndented(sb, indentLevel, "{");
		AppendLineIndented(sb, indentLevel + 1, "switch (this)");
		AppendLineIndented(sb, indentLevel + 1, "{");

		foreach (var subtype in subtypes)
		{
			AppendLineIndented(sb, indentLevel + 2, $"case {subtype.TypeName} {subtype.ValueName}:");
			AppendLineIndented(sb, indentLevel + 3, $"{subtype.HandlerName}({subtype.ValueName});");
			AppendLineIndented(sb, indentLevel + 3, "return;");
		}

		AppendLineIndented(sb, indentLevel + 2, "default:");
		AppendLineIndented(sb, indentLevel + 3, "throw new InvalidOperationException($\"Unknown variant: {GetType().Name}\");");
		AppendLineIndented(sb, indentLevel + 1, "}");
		AppendLineIndented(sb, indentLevel, "}");
	}

	/// <summary>
	/// <c>IsX</c> is a property (cheap, cannot throw); <c>AsX</c> is a method so reflection-based
	/// property walkers — ASP.NET validation, debugger watch windows, mappers — never invoke a
	/// getter that throws for every subtype but the live one.
	/// </summary>
	private static void GenerateAccessors(
		StringBuilder sb,
		UnionModel union,
		int indentLevel)
	{
		foreach (var subtype in GetSubtypes(union))
		{
			sb.AppendLine();
			AppendLineIndented(sb, indentLevel, $"public bool Is{subtype.Name} => this is {subtype.TypeName};");
			sb.AppendLine();
			AppendLineIndented(sb, indentLevel, $"public {subtype.TypeName} As{subtype.Name}() =>");
			AppendLineIndented(sb, indentLevel + 1, $"this as {subtype.TypeName} ?? throw new InvalidOperationException($\"Cannot access this {union.ClassName} as '{subtype.TypeName}' because it is '{{GetType().Name}}'.\");");
			sb.AppendLine();
			AppendLineIndented(sb, indentLevel, $"public bool TryPick{subtype.Name}(out {subtype.TypeName} value)");
			AppendLineIndented(sb, indentLevel, "{");
			AppendLineIndented(sb, indentLevel + 1, $"value = (this as {subtype.TypeName})!;");
			AppendLineIndented(sb, indentLevel + 1, "return value is not null;");
			AppendLineIndented(sb, indentLevel, "}");
		}
	}

	private static void GenerateVariantClasses(
		StringBuilder sb,
		UnionModel union,
		int indentLevel)
	{
		var variantKeyword = GetVariantKeyword(union);

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

	private static void GenerateJsonConverterClass(
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

		if (union.Variants.Count > 0)
		{
			sb.AppendLine();
		}

		EmitFailureMessage(sb, className, discriminatorFieldName, indentLevel + 2);
		sb.AppendLine();

		foreach (var subUnion in union.SubUnions)
		{
			var subUnionTypeName = GetNestedTypeReference(className, subUnion);
			AppendLineIndented(sb, indentLevel + 2, "try");
			AppendLineIndented(sb, indentLevel + 2, "{");
			AppendLineIndented(sb, indentLevel + 3, $"return System.Text.Json.JsonSerializer.Deserialize<{subUnionTypeName}>(root.GetRawText(), options)!;");
			AppendLineIndented(sb, indentLevel + 2, "}");
			AppendLineIndented(sb, indentLevel + 2, "catch (System.Text.Json.JsonException)");
			AppendLineIndented(sb, indentLevel + 2, "{");
			AppendLineIndented(sb, indentLevel + 3, "// Try the next sub-union family; the recorded reason is what gets reported if none accepts.");
			AppendLineIndented(sb, indentLevel + 2, "}");
		}

		AppendLineIndented(sb, indentLevel + 2, "throw new System.Text.Json.JsonException(failureMessage);");
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

	private static void GenerateNewtonsoftJsonConverterClass(
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

		if (union.Variants.Count > 0)
		{
			sb.AppendLine();
		}

		EmitFailureMessage(sb, className, discriminatorFieldName, indentLevel + 2);
		sb.AppendLine();

		foreach (var subUnion in union.SubUnions)
		{
			var subUnionTypeName = GetNestedTypeReference(className, subUnion);
			AppendLineIndented(sb, indentLevel + 2, "try");
			AppendLineIndented(sb, indentLevel + 2, "{");
			AppendLineIndented(sb, indentLevel + 3, $"return obj.ToObject<{subUnionTypeName}>(serializer)!;");
			AppendLineIndented(sb, indentLevel + 2, "}");
			AppendLineIndented(sb, indentLevel + 2, "catch (Newtonsoft.Json.JsonSerializationException)");
			AppendLineIndented(sb, indentLevel + 2, "{");
			AppendLineIndented(sb, indentLevel + 3, "// Try the next sub-union family; the recorded reason is what gets reported if none accepts.");
			AppendLineIndented(sb, indentLevel + 2, "}");
		}

		AppendLineIndented(sb, indentLevel + 2, "throw new Newtonsoft.Json.JsonSerializationException(failureMessage);");
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

	/// <summary>
	/// Records why this union's own variants did not match, before the sub-union search runs.
	/// A sub-union may still accept the payload, so the reason cannot be thrown here — but if
	/// none does, this is what gets reported instead of a message with a blank variant name.
	/// </summary>
	private static void EmitFailureMessage(
		StringBuilder sb,
		string className,
		string discriminatorFieldName,
		int indentLevel)
	{
		AppendLineIndented(sb, indentLevel, "var failureMessage = typeName is null");
		AppendLineIndented(sb, indentLevel + 1, $"? \"Missing discriminator field '{discriminatorFieldName}' when deserializing '{className}'.\"");
		AppendLineIndented(sb, indentLevel + 1, $": \"Unrecognized discriminator value '\" + typeName + \"' when deserializing '{className}'.\";");
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

	private static string GetTypeDeclarationPrefix(UnionModel union)
	{
		var accessModifier = string.IsNullOrWhiteSpace(union.AccessModifier)
			? string.Empty
			: union.AccessModifier + " ";
		return $"{accessModifier}abstract partial {union.TypeKeyword} {union.ClassName}";
	}

	private static string GetVariantKeyword(UnionModel union) =>
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
	bool HasSystemTextJson);

internal readonly record struct UnionConfig(
	string DiscriminatorFieldName,
	bool? GenerateJsonConverter,
	bool? GenerateNewtonsoftJsonConverter);

internal readonly record struct ContainingTypeInfo(
	string Name,
	string Keyword,
	string AccessModifier,
	bool IsPartial,
	Location? Location);

internal enum UnionDeclarationError
{
	None,
	NotAbstract,
	IsStruct
}

/// <summary>One subtype a union value can be: a generated variant, or a nested sub-union.</summary>
internal sealed record SubtypeModel(string TypeName, string Name)
{
	public string HandlerName { get; } = ToCamelCase(Name);

	public string ValueName { get; } = ToCamelCase(Name) + "Value";

	private static string ToCamelCase(string value) =>
		string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value.Substring(1);
}

internal sealed record VariantModel(string Name, EquatableArray<VariantParameter> Parameters);

internal readonly record struct VariantParameter(string Name, string Type);

internal sealed record UnionModel(
	string Namespace,
	string ClassName,
	string TypeKeyword,
	string AccessModifier,
	EquatableArray<VariantModel> Variants,
	EquatableArray<ContainingTypeInfo> ContainingTypes,
	EquatableArray<string> SubUnions,
	UnionDeclarationError DeclarationError,
	Location? Location,
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
