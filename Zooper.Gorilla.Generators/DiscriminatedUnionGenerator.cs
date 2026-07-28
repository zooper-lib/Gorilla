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

	/// <summary>
	/// Reported when a nested <c>[DiscriminatedUnion]</c> names a different construction of its enclosing
	/// union as its base. Warning rather than error, like ZGOR002: the declaration is legal, but the
	/// generated code will surprise the user — the nested union never appears in the parent's Match, and
	/// the omission only surfaces at runtime through the fallback throw.
	/// </summary>
	private static readonly DiagnosticDescriptor MismatchedSubUnionBaseDescriptor = new(
		id: "ZGOR005",
		title: "Nested union derives from a different construction of its enclosing union",
		messageFormat: "Nested discriminated union '{0}' derives from '{1}', which is a different construction of the enclosing union '{2}'. It is excluded from '{2}'s Match; derive from '{2}' instead.",
		category: "Zooper.Gorilla",
		defaultSeverity: DiagnosticSeverity.Warning,
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
		var subUnions = GetDirectSubUnions(classSymbol, out var mismatchedSubUnions);

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
			TypeParameters: GetTypeParameterNames(classSymbol),
			TypeParameterConstraints: GetConstraintClauses(classSymbol),
			Variants: new EquatableArray<VariantModel>(variantBuilder.ToArray()),
			ContainingTypes: containingTypes,
			SubUnions: subUnions,
			MismatchedSubUnions: mismatchedSubUnions,
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
				TypeParameters: GetTypeParameterNames(containingType),
				TypeParameterConstraints: GetConstraintClauses(containingType),
				Location: containingType.Locations.FirstOrDefault()));
			containingType = containingType.ContainingType;
		}

		types.Reverse();
		return new EquatableArray<ContainingTypeInfo>(types.ToArray());
	}

	private static EquatableArray<string> GetTypeParameterNames(INamedTypeSymbol typeSymbol) =>
		new(typeSymbol.TypeParameters.Select(static parameter => parameter.Name).ToArray());

	private static EquatableArray<string> GetConstraintClauses(INamedTypeSymbol typeSymbol) =>
		new(typeSymbol.TypeParameters
			.Select(RenderConstraintClause)
			.Where(static clause => clause is not null)
			.Select(static clause => clause!)
			.ToArray());

	/// <summary>
	/// Renders a <c>where</c> clause in the order the language requires — primary constraint, then
	/// interfaces, then <c>new()</c> — with constraint types fully qualified via
	/// <see cref="ISymbol.ToDisplayString()"/> so the clause resolves in a generated file whose using
	/// block is one line, and in whatever scope a hoisted converter lands in. Reading the declaration
	/// syntax instead would emit unqualified names (CS0246) and break on <c>using</c> aliases.
	/// </summary>
	private static string? RenderConstraintClause(ITypeParameterSymbol typeParameter)
	{
		var parts = new List<string>();

		if (typeParameter.HasReferenceTypeConstraint)
		{
			parts.Add(typeParameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated
				? "class?"
				: "class");
		}
		else if (typeParameter.HasUnmanagedTypeConstraint)
		{
			// 'unmanaged' also sets HasValueTypeConstraint, so it must be tested first.
			parts.Add("unmanaged");
		}
		else if (typeParameter.HasValueTypeConstraint)
		{
			parts.Add("struct");
		}
		else if (typeParameter.HasNotNullConstraint)
		{
			parts.Add("notnull");
		}

		// A base-type constraint arrives through ConstraintTypes, and Roslyn preserves declaration
		// order, which the language already forces to put the base type first.
		parts.AddRange(typeParameter.ConstraintTypes.Select(static type => type.ToDisplayString()));

		if (typeParameter.HasConstructorConstraint)
		{
			parts.Add("new()");
		}

		return parts.Count == 0
			? null
			: $"where {typeParameter.Name} : {string.Join(", ", parts)}";
	}

	/// <summary>
	/// Sub-union membership is declared by the base clause, never inferred from nesting: nesting also
	/// means "payload type scoped inside its owner", and only the base clause separates the two.
	/// </summary>
	private static EquatableArray<string> GetDirectSubUnions(
		INamedTypeSymbol classSymbol,
		out EquatableArray<MismatchedSubUnion> mismatched)
	{
		var subUnionNames = new List<string>();
		var mismatches = new List<MismatchedSubUnion>();

		foreach (var nestedType in classSymbol.GetTypeMembers())
		{
			var isUnion = nestedType.GetAttributes().Any(static attribute =>
				attribute.AttributeClass?.ToDisplayString() == DiscriminatedUnionAttributeFullName);

			if (!isUnion)
			{
				continue;
			}

			// A type constructed with its own enclosing type parameters is the definition symbol, so
			// this comparison already holds for generic unions. Comparing original definitions instead
			// would also accept a different construction and emit Match arms that do not compile.
			if (SymbolEqualityComparer.Default.Equals(nestedType.BaseType, classSymbol))
			{
				subUnionNames.Add(nestedType.Name);
				continue;
			}

			if (nestedType.BaseType is not null &&
				SymbolEqualityComparer.Default.Equals(nestedType.BaseType.OriginalDefinition, classSymbol.OriginalDefinition))
			{
				mismatches.Add(new MismatchedSubUnion(
					Name: nestedType.Name,
					DeclaredBase: nestedType.BaseType.ToDisplayString(),
					Location: nestedType.Locations.FirstOrDefault()));
			}
		}

		mismatched = new EquatableArray<MismatchedSubUnion>(mismatches.ToArray());
		return new EquatableArray<string>(subUnionNames.ToArray());
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
			// Compile-time diagnostics name the union as the user wrote it, so a generic and a non-generic
			// union of the same name — which now coexist — are distinguishable.
			var displayName = SelfTypeReference(union);

			if (union.DeclarationError != UnionDeclarationError.None)
			{
				context.ReportDiagnostic(Diagnostic.Create(
					union.DeclarationError == UnionDeclarationError.IsStruct
						? StructUnionDescriptor
						: NonAbstractUnionDescriptor,
					union.Location ?? Location.None,
					displayName));
				return;
			}

			ReportContainingTypeDiagnostics(context, union, displayName);
			ReportMismatchedSubUnionDiagnostics(context, union, displayName);

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

	private static void ReportContainingTypeDiagnostics(SourceProductionContext context, UnionModel union, string displayName)
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
				displayName));
		}
	}

	private static void ReportMismatchedSubUnionDiagnostics(SourceProductionContext context, UnionModel union, string displayName)
	{
		foreach (var mismatched in union.MismatchedSubUnions)
		{
			context.ReportDiagnostic(Diagnostic.Create(
				MismatchedSubUnionBaseDescriptor,
				mismatched.Location ?? Location.None,
				mismatched.Name,
				mismatched.DeclaredBase,
				displayName));
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

		segments.AddRange(union.ContainingTypes.Select(static type => AppendArity(type.Name, type.Arity)));
		segments.Add(AppendArity(union.ClassName, union.TypeParameters.Count));

		return $"{string.Join(".", segments)}.g.cs";
	}

	/// <summary>
	/// Types differing only in arity would otherwise share a hint. An underscore rather than the CLR's
	/// backtick, which is not safe in a hint name; arity zero carries no suffix, so no hint that a
	/// previous release emitted is renamed.
	/// </summary>
	private static string AppendArity(string name, int arity) =>
		arity == 0 ? name : $"{name}_{arity}";

	private static string GenerateSource(
		UnionModel union,
		bool generateJsonConverter,
		bool generateNewtonsoftJsonConverter)
	{
		var sb = new StringBuilder();
		sb.AppendLine("#nullable enable");
		sb.AppendLine("using System;");
		sb.AppendLine();

		var placement = GetConverterPlacement(union);

		// System.Text.Json resolves [JsonConverter] on the type being written, which for a value flowing
		// through 'object' or a variant's static type is the variant, not the union. Without the attribute
		// on the variant too, such a value is written by default logic and loses its discriminator.
		var stjVariantAttribute = generateJsonConverter
			? $"[System.Text.Json.Serialization.JsonConverter(typeof({placement.StjAttributeTarget}))]"
			: null;

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
			AppendLineIndented(sb, indentLevel, stjVariantAttribute!);
		}

		// Newtonsoft honours the attribute inherited from the base type, so it is emitted here only.
		// Duplicating it on variants would make the shim resolve objectType as the variant and hand
		// back a union instance where a variant instance is expected.
		if (generateNewtonsoftJsonConverter)
		{
			AppendLineIndented(sb, indentLevel, $"[Newtonsoft.Json.JsonConverterAttribute(typeof({placement.NewtonsoftAttributeTarget}))]");
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
		GenerateVariantClasses(sb, union, stjVariantAttribute, indentLevel + 1);
		AppendLineIndented(sb, indentLevel, "}");

		// Close down to the converter's scope, emit there, then close the rest.
		for (var i = union.ContainingTypes.Count - 1; i >= placement.ScopeDepth; i--)
		{
			indentLevel--;
			EmitContainingTypeClose(sb, indentLevel);
		}

		if (generateJsonConverter)
		{
			sb.AppendLine();
			GenerateJsonConverterClass(sb, union, placement, union.Config.DiscriminatorFieldName, indentLevel);

			if (placement.NeedsRuntimeConstruction)
			{
				sb.AppendLine();
				GenerateStjConverterFactory(sb, placement, indentLevel);
			}
		}

		if (generateNewtonsoftJsonConverter)
		{
			sb.AppendLine();
			GenerateNewtonsoftJsonConverterClass(sb, union, placement, union.Config.DiscriminatorFieldName, indentLevel);

			if (placement.NeedsRuntimeConstruction)
			{
				sb.AppendLine();
				GenerateNewtonsoftConverterShim(sb, placement, indentLevel);
			}
		}

		for (var i = placement.ScopeDepth - 1; i >= 0; i--)
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
		AppendLineIndented(
			sb,
			indentLevel,
			$"{accessModifier}partial {type.Keyword} {type.Name}"
				+ RenderTypeParameterList(type.TypeParameters)
				+ RenderConstraintClauses(type.TypeParameterConstraints));
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
		var selfType = SelfTypeReference(union);

		foreach (var variant in union.Variants)
		{
			var parameters = string.Join(", ", variant.Parameters.Select(p => $"{p.Type} {p.Name}"));
			var args = string.Join(", ", variant.Parameters.Select(p => p.Name));
			var newVariantInstance = string.IsNullOrEmpty(args)
				? $"new {variant.Name}Variant()"
				: $"new {variant.Name}Variant({args})";

			AppendLineIndented(sb, indentLevel, $"public static partial {selfType} {variant.Name}({parameters}) => {newVariantInstance};");
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
		var resultType = GetResultTypeParameterName(union);

		AppendLineIndented(sb, indentLevel, $"public {resultType} Match<{resultType}>(");
		for (var i = 0; i < subtypes.Count; i++)
		{
			var suffix = i < subtypes.Count - 1 ? "," : ") =>";
			AppendLineIndented(sb, indentLevel + 1, $"Func<{subtypes[i].TypeName}, {resultType}> {subtypes[i].HandlerName}{suffix}");
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

	/// <summary>
	/// One emitted shape for every union: the result parameter is <c>TResult</c>, suffix-uniquified when
	/// that name is already in scope. The taken set is every type parameter in scope, not just the
	/// union's own — a union nested in <c>Outer&lt;TResult&gt;</c> inherits the name and hits the same CS0693.
	/// </summary>
	private static string GetResultTypeParameterName(UnionModel union)
	{
		var taken = new HashSet<string>(GetTypeParametersInScope(union), StringComparer.Ordinal);

		if (!taken.Contains("TResult"))
		{
			return "TResult";
		}

		for (var suffix = 1; ; suffix++)
		{
			var candidate = "TResult" + suffix;
			if (!taken.Contains(candidate))
			{
				return candidate;
			}
		}
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

	/// <summary>
	/// A variant is a nested type, so it inherits the enclosing union's type parameters and constraints
	/// and declares neither of its own.
	/// </summary>
	private static void GenerateVariantClasses(
		StringBuilder sb,
		UnionModel union,
		string? stjConverterAttribute,
		int indentLevel)
	{
		var variantKeyword = GetVariantKeyword(union);
		var selfType = SelfTypeReference(union);

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

			if (stjConverterAttribute is not null)
			{
				AppendLineIndented(sb, indentLevel, stjConverterAttribute);
			}

			AppendLineIndented(sb, indentLevel, $"public sealed {variantKeyword} {variantTypeName} : {selfType}");
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
		ConverterPlacement placement,
		string discriminatorFieldName,
		int indentLevel)
	{
		var className = placement.UnionReference;

		// A converter is a separate generic type from the union, so it repeats the constraint clauses
		// even though the union's own partial declaration could inherit them from the user's part.
		AppendLineIndented(
			sb,
			indentLevel,
			$"public class {placement.StjConverterName}{RenderTypeParameterList(placement.TypeParameters)}"
				+ $" : System.Text.Json.Serialization.JsonConverter<{className}>"
				+ RenderConstraintClauses(placement.Constraints));
		AppendLineIndented(sb, indentLevel, "{");
		EmitStjConverterHelpers(sb, indentLevel + 1);
		sb.AppendLine();
		// JsonConverter<TBase> refuses a derived typeToConvert until CanConvert claims it, and the
		// attribute on the variant types hands it exactly those derived types.
		AppendLineIndented(sb, indentLevel + 1, $"public override bool CanConvert(System.Type typeToConvert) => typeof({className}).IsAssignableFrom(typeToConvert);");
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

		EmitFailureMessage(sb, union.ClassName, discriminatorFieldName, indentLevel + 2);
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
		ConverterPlacement placement,
		string discriminatorFieldName,
		int indentLevel)
	{
		var className = placement.UnionReference;

		AppendLineIndented(
			sb,
			indentLevel,
			$"public class {placement.NewtonsoftConverterName}{RenderTypeParameterList(placement.TypeParameters)}"
				+ $" : Newtonsoft.Json.JsonConverter<{className}>"
				+ RenderConstraintClauses(placement.Constraints));
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

		EmitFailureMessage(sb, union.ClassName, discriminatorFieldName, indentLevel + 2);
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
	/// <c>JsonConverterAttribute</c> instantiates the named type, and an open generic type has no
	/// instance, so a converter carrying type parameters is registered through this non-generic factory.
	/// No cache: System.Text.Json caches the factory's output per (type, options).
	/// </summary>
	private static void GenerateStjConverterFactory(
		StringBuilder sb,
		ConverterPlacement placement,
		int indentLevel)
	{
		AppendLineIndented(sb, indentLevel, $"public class {placement.StjFactoryName} : System.Text.Json.Serialization.JsonConverterFactory");
		AppendLineIndented(sb, indentLevel, "{");
		EmitClosedUnionHelper(sb, placement, indentLevel + 1);
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 1, "public override bool CanConvert(System.Type typeToConvert) => ClosedUnion(typeToConvert) != null;");
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 1, "public override System.Text.Json.Serialization.JsonConverter? CreateConverter(System.Type typeToConvert, System.Text.Json.JsonSerializerOptions options)");
		AppendLineIndented(sb, indentLevel + 1, "{");
		AppendLineIndented(sb, indentLevel + 2, "var closedUnion = ClosedUnion(typeToConvert);");
		AppendLineIndented(sb, indentLevel + 2, "if (closedUnion == null) return null;");
		EmitMakeGenericConverterType(sb, placement.StjConverterName, placement.TypeParameters.Count, indentLevel + 2);
		AppendLineIndented(sb, indentLevel + 2, "return (System.Text.Json.Serialization.JsonConverter?)System.Activator.CreateInstance(converterType);");
		AppendLineIndented(sb, indentLevel + 1, "}");
		AppendLineIndented(sb, indentLevel, "}");
	}

	/// <summary>
	/// <c>JsonConverter&lt;T&gt;</c> exposes non-generic <c>ReadJson</c>/<c>WriteJson</c> as public sealed
	/// overrides, so this shim forwards through the base and the read/write logic stays in the one
	/// emitted generic body. The cache is load-bearing here and not on the System.Text.Json side:
	/// Newtonsoft caches the shim instance on the contract but re-enters <c>ReadJson</c> per value.
	/// </summary>
	private static void GenerateNewtonsoftConverterShim(
		StringBuilder sb,
		ConverterPlacement placement,
		int indentLevel)
	{
		AppendLineIndented(sb, indentLevel, $"public class {placement.NewtonsoftShimName} : Newtonsoft.Json.JsonConverter");
		AppendLineIndented(sb, indentLevel, "{");
		AppendLineIndented(sb, indentLevel + 1, "private static readonly System.Collections.Concurrent.ConcurrentDictionary<System.Type, Newtonsoft.Json.JsonConverter> Converters = new System.Collections.Concurrent.ConcurrentDictionary<System.Type, Newtonsoft.Json.JsonConverter>();");
		sb.AppendLine();
		EmitClosedUnionHelper(sb, placement, indentLevel + 1);
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 1, "private static Newtonsoft.Json.JsonConverter Resolve(System.Type candidate)");
		AppendLineIndented(sb, indentLevel + 1, "{");
		AppendLineIndented(sb, indentLevel + 2, "var closedUnion = ClosedUnion(candidate)");
		AppendLineIndented(sb, indentLevel + 3, $"?? throw new Newtonsoft.Json.JsonSerializationException(\"Type '\" + candidate + \"' is not a '{placement.BaseName}'.\");");
		AppendLineIndented(sb, indentLevel + 2, "return Converters.GetOrAdd(closedUnion, resolved =>");
		AppendLineIndented(sb, indentLevel + 2, "{");
		EmitMakeGenericConverterType(sb, placement.NewtonsoftConverterName, placement.TypeParameters.Count, indentLevel + 3, "resolved");
		AppendLineIndented(sb, indentLevel + 3, "return (Newtonsoft.Json.JsonConverter)System.Activator.CreateInstance(converterType)!;");
		AppendLineIndented(sb, indentLevel + 2, "});");
		AppendLineIndented(sb, indentLevel + 1, "}");
		sb.AppendLine();
		// Newtonsoft skips CanConvert when the converter arrives via [JsonConverter], but consults it on
		// a manual settings.Converters.Add(...). A generic-type-definition comparison would decline every
		// variant and drop the value to default property serialization with no discriminator.
		AppendLineIndented(sb, indentLevel + 1, "public override bool CanConvert(System.Type objectType) => ClosedUnion(objectType) != null;");
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 1, "public override object? ReadJson(Newtonsoft.Json.JsonReader reader, System.Type objectType, object? existingValue, Newtonsoft.Json.JsonSerializer serializer)");
		AppendLineIndented(sb, indentLevel + 2, "=> Resolve(objectType).ReadJson(reader, objectType, existingValue, serializer);");
		sb.AppendLine();
		AppendLineIndented(sb, indentLevel + 1, "public override void WriteJson(Newtonsoft.Json.JsonWriter writer, object? value, Newtonsoft.Json.JsonSerializer serializer)");
		AppendLineIndented(sb, indentLevel + 1, "{");
		AppendLineIndented(sb, indentLevel + 2, "if (value == null) { writer.WriteNull(); return; }");
		AppendLineIndented(sb, indentLevel + 2, "Resolve(value.GetType()).WriteJson(writer, value, serializer);");
		AppendLineIndented(sb, indentLevel + 1, "}");
		AppendLineIndented(sb, indentLevel, "}");
	}

	/// <summary>
	/// A variant is nested in the union, so it is itself a generic type but its definition is not the
	/// union's; walking base types is what turns any candidate into the construction that carries the
	/// union's type arguments. <c>GetGenericArguments()</c> on that construction reports containing
	/// types' arguments first, which is the order the hoisted converter declares its parameters in.
	/// </summary>
	private static void EmitClosedUnionHelper(
		StringBuilder sb,
		ConverterPlacement placement,
		int indentLevel)
	{
		AppendLineIndented(sb, indentLevel, "private static System.Type? ClosedUnion(System.Type? candidate)");
		AppendLineIndented(sb, indentLevel, "{");
		AppendLineIndented(sb, indentLevel + 1, "for (var type = candidate; type != null; type = type.BaseType)");
		AppendLineIndented(sb, indentLevel + 1, "{");
		AppendLineIndented(sb, indentLevel + 2, $"if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof({placement.UnboundUnionReference})) return type;");
		AppendLineIndented(sb, indentLevel + 1, "}");
		AppendLineIndented(sb, indentLevel + 1, "return null;");
		AppendLineIndented(sb, indentLevel, "}");
	}

	private static void EmitMakeGenericConverterType(
		StringBuilder sb,
		string converterName,
		int arity,
		int indentLevel,
		string closedUnionVariable = "closedUnion")
	{
		// The set of closed constructions is not knowable at generation time, which is why a factory
		// exists at all, so the reflection cannot be removed. Surfacing IL3050 from a generated file
		// would hard-fail any consumer building with TreatWarningsAsErrors at a location they cannot edit.
		AppendLineIndented(sb, indentLevel, "#pragma warning disable IL3050 // Native AOT: the closed union types must be rooted; see README.");
		AppendLineIndented(sb, indentLevel, $"var converterType = typeof({converterName}{RenderUnboundArgumentList(arity)}).MakeGenericType({closedUnionVariable}.GetGenericArguments());");
		AppendLineIndented(sb, indentLevel, "#pragma warning restore IL3050");
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
		return $"{accessModifier}abstract partial {union.TypeKeyword} {union.ClassName}"
			+ RenderTypeParameterList(union.TypeParameters)
			+ RenderConstraintClauses(union.TypeParameterConstraints);
	}

	private static string RenderTypeParameterList(IEnumerable<string> typeParameters)
	{
		var names = typeParameters.ToList();
		return names.Count == 0 ? string.Empty : "<" + string.Join(", ", names) + ">";
	}

	private static string RenderConstraintClauses(IEnumerable<string> clauses)
	{
		var rendered = clauses.ToList();
		return rendered.Count == 0 ? string.Empty : " " + string.Join(" ", rendered);
	}

	/// <summary>
	/// The union as a <em>type</em>: <c>Disclosure&lt;T&gt;</c>, bare when arity is zero. The one place
	/// the name is an identifier rather than a type — the generated private constructor — stays bare.
	/// </summary>
	private static string SelfTypeReference(UnionModel union) =>
		UnionReferenceFrom(union, union.ContainingTypes.Count);

	/// <summary>
	/// The union named from a scope with <paramref name="scopeDepth"/> containing types still open, so a
	/// converter hoisted out of a generic container can still name it: <c>Outer&lt;TOuter&gt;.Leaf&lt;T&gt;</c>.
	/// </summary>
	private static string UnionReferenceFrom(UnionModel union, int scopeDepth)
	{
		var sb = new StringBuilder();

		for (var i = scopeDepth; i < union.ContainingTypes.Count; i++)
		{
			sb.Append(union.ContainingTypes[i].Name)
				.Append(RenderTypeParameterList(union.ContainingTypes[i].TypeParameters))
				.Append('.');
		}

		return sb.Append(union.ClassName).Append(RenderTypeParameterList(union.TypeParameters)).ToString();
	}

	/// <summary>The same path written unbound, for <c>typeof(Outer&lt;&gt;.Leaf&lt;&gt;)</c>.</summary>
	private static string UnboundUnionReferenceFrom(UnionModel union, int scopeDepth)
	{
		var sb = new StringBuilder();

		for (var i = scopeDepth; i < union.ContainingTypes.Count; i++)
		{
			sb.Append(union.ContainingTypes[i].Name)
				.Append(RenderUnboundArgumentList(union.ContainingTypes[i].Arity))
				.Append('.');
		}

		return sb.Append(union.ClassName).Append(RenderUnboundArgumentList(union.TypeParameters.Count)).ToString();
	}

	private static string RenderUnboundArgumentList(int arity) =>
		arity == 0 ? string.Empty : "<" + new string(',', arity - 1) + ">";

	/// <summary>
	/// Every type parameter in scope at the union's declaration, containers outermost first followed by
	/// the union's own — the order the runtime reports for a closed nested generic type, and the set the
	/// Match result parameter must avoid colliding with.
	/// </summary>
	private static List<string> GetTypeParametersInScope(UnionModel union)
	{
		var names = new List<string>();

		foreach (var containingType in union.ContainingTypes)
		{
			names.AddRange(containingType.TypeParameters);
		}

		names.AddRange(union.TypeParameters);
		return names;
	}

	/// <summary>
	/// An attribute argument cannot name a type parameter (CS0416), so a converter may not live inside a
	/// generic containing type. It is emitted at the innermost enclosing scope with no type parameters,
	/// absorbing the parameters of every container it skipped — outermost first, then the union's own,
	/// which is the order <c>MakeGenericType</c> expects — and taking those containers' names as a prefix.
	/// When no container is generic this is the union's own scope, and both position and name are
	/// exactly what previous releases emitted.
	/// </summary>
	private static ConverterPlacement GetConverterPlacement(UnionModel union)
	{
		var containingTypes = union.ContainingTypes;
		var scopeDepth = containingTypes.Count;

		for (var i = 0; i < containingTypes.Count; i++)
		{
			if (containingTypes[i].Arity > 0)
			{
				scopeDepth = i;
				break;
			}
		}

		var namePrefix = new StringBuilder();
		var typeParameters = new List<string>();
		var constraints = new List<string>();

		for (var i = scopeDepth; i < containingTypes.Count; i++)
		{
			namePrefix.Append(containingTypes[i].Name).Append('_');
			typeParameters.AddRange(containingTypes[i].TypeParameters);
			constraints.AddRange(containingTypes[i].TypeParameterConstraints);
		}

		typeParameters.AddRange(union.TypeParameters);
		constraints.AddRange(union.TypeParameterConstraints);

		return new ConverterPlacement(
			ScopeDepth: scopeDepth,
			BaseName: namePrefix.Append(union.ClassName).ToString(),
			UnionReference: UnionReferenceFrom(union, scopeDepth),
			UnboundUnionReference: UnboundUnionReferenceFrom(union, scopeDepth),
			TypeParameters: typeParameters,
			Constraints: constraints);
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
	EquatableArray<string> TypeParameters,
	EquatableArray<string> TypeParameterConstraints,
	Location? Location)
{
	public int Arity => TypeParameters.Count;
}

/// <summary>
/// A nested <c>[DiscriminatedUnion]</c> whose base clause names a different construction of the
/// enclosing union (<c>Rejected : Outcome&lt;int&gt;</c> inside <c>Outcome&lt;T&gt;</c>). It is not
/// a subtype of this construction, so it cannot appear in the parent's Match — see ZGOR005.
/// </summary>
internal readonly record struct MismatchedSubUnion(
	string Name,
	string DeclaredBase,
	Location? Location);

/// <summary>Where a union's converters are emitted, what they are named there, and the type parameters they carry.</summary>
internal sealed record ConverterPlacement(
	int ScopeDepth,
	string BaseName,
	string UnionReference,
	string UnboundUnionReference,
	List<string> TypeParameters,
	List<string> Constraints)
{
	/// <summary>
	/// A generic converter cannot be named in an attribute argument, so registration goes through a
	/// non-generic factory (System.Text.Json) or shim (Newtonsoft) that closes it at runtime.
	/// </summary>
	public bool NeedsRuntimeConstruction => TypeParameters.Count > 0;

	public string StjConverterName => BaseName + "JsonConverter";

	public string StjFactoryName => BaseName + "JsonConverterFactory";

	public string NewtonsoftConverterName => BaseName + "NewtonsoftJsonConverter";

	public string NewtonsoftShimName => BaseName + "NewtonsoftJsonConverterShim";

	public string StjAttributeTarget => NeedsRuntimeConstruction ? StjFactoryName : StjConverterName;

	public string NewtonsoftAttributeTarget => NeedsRuntimeConstruction ? NewtonsoftShimName : NewtonsoftConverterName;
}

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
	EquatableArray<string> TypeParameters,
	EquatableArray<string> TypeParameterConstraints,
	EquatableArray<VariantModel> Variants,
	EquatableArray<ContainingTypeInfo> ContainingTypes,
	EquatableArray<string> SubUnions,
	EquatableArray<MismatchedSubUnion> MismatchedSubUnions,
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
