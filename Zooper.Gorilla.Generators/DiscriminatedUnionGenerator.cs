using System;
using System.Collections;
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
	private const string DiscriminatedUnionAttributeFullName = "Zooper.Gorilla.Attributes.DiscriminatedUnionAttribute";

	private static readonly DiagnosticDescriptor GeneratorErrorDescriptor = new(
		id: "ZGOR001",
		title: "Discriminated union generator failure",
		messageFormat: "Failed to generate discriminated union for '{0}': {1}",
		category: "Zooper.Gorilla",
		defaultSeverity: DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		var unions = context.SyntaxProvider
			.ForAttributeWithMetadataName(
				DiscriminatedUnionAttributeFullName,
				predicate: static (node, _) => node is ClassDeclarationSyntax,
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
			Variants: new EquatableArray<VariantModel>(variantBuilder.ToArray()),
			Config: config);
	}

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
			var generateJsonConverter = union.Config.GenerateJsonConverter ?? frameworkSupport.HasSystemTextJson;
			var generateNewtonsoftJsonConverter = union.Config.GenerateNewtonsoftJsonConverter ?? frameworkSupport.HasNewtonsoftJson;
			var suppressValidation = union.Config.SuppressValidation ?? frameworkSupport.HasAspNetValidation;

			var source = GenerateSource(union, generateJsonConverter, generateNewtonsoftJsonConverter, suppressValidation);
			context.AddSource($"{union.ClassName}.g.cs", SourceText.From(source, Encoding.UTF8));
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

	private static string GenerateSource(
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

		sb.AppendLine($"namespace {namespaceName}");
		sb.AppendLine("{");

		if (generateJsonConverter)
		{
			sb.AppendLine($"    [System.Text.Json.Serialization.JsonConverter(typeof({className}JsonConverter))]");
		}

		if (generateNewtonsoftJsonConverter)
		{
			sb.AppendLine($"    [Newtonsoft.Json.JsonConverterAttribute(typeof({className}NewtonsoftJsonConverter))]");
		}

		if (suppressValidation)
		{
			sb.AppendLine("    [Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNever]");
		}

		GenerateClassHeader(sb, className, variants);
		sb.AppendLine("    {");

		GenerateConstructor(sb, className, variants);
		sb.AppendLine();

		GenerateVariantMethods(sb, className, variants);

		GenerateVariantClasses(sb, variants);

		sb.AppendLine("    }");

		if (generateJsonConverter)
		{
			sb.AppendLine();
			GenerateJsonConverterClass(sb, className, variants, union.Config.DiscriminatorFieldName);
		}

		if (generateNewtonsoftJsonConverter)
		{
			sb.AppendLine();
			GenerateNewtonsoftJsonConverterClass(sb, className, variants, union.Config.DiscriminatorFieldName);
		}

		sb.AppendLine("}");

		return sb.ToString();
	}

	private static void GenerateClassHeader(
		StringBuilder sb,
		string className,
		EquatableArray<VariantModel> variants)
	{
		var variantTypeNames = variants.Select(v => $"{className}.{v.Name}Variant").ToList();
		sb.AppendLine($"    public partial class {className} : OneOfBase<{string.Join(", ", variantTypeNames)}>");
	}

	private static void GenerateConstructor(
		StringBuilder sb,
		string className,
		EquatableArray<VariantModel> variants)
	{
		var variantTypeNames = variants.Select(v => $"{className}.{v.Name}Variant").ToList();
		sb.AppendLine($"        private {className}(OneOf<{string.Join(", ", variantTypeNames)}> value) : base(value) {{ }}");
	}

	private static void GenerateVariantMethods(
		StringBuilder sb,
		string className,
		EquatableArray<VariantModel> variants)
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

			sb.AppendLine($"        {methodSignature} => new {className}({newVariantInstance});");
		}
	}

	private static void GenerateVariantClasses(
		StringBuilder sb,
		EquatableArray<VariantModel> variants)
	{
		foreach (var variant in variants)
		{
			var variantName = variant.Name;
			var variantTypeName = $"{variantName}Variant";
			var parameters = variant.Parameters;
			var fields = parameters.Select(p => $"            public {p.Type} {FirstCharToUpper(p.Name)} {{ get; }}")
				.ToList();
			var ctorParameters = string.Join(", ", parameters.Select(p => $"{p.Type} {p.Name}"));
			var assignments = parameters.Select(p => $"                this.{FirstCharToUpper(p.Name)} = {p.Name};").ToList();

			sb.AppendLine();
			sb.AppendLine($"        public class {variantTypeName}");
			sb.AppendLine("        {");

			if (parameters.Count > 0)
			{
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

	private static void GenerateJsonConverterClass(
		StringBuilder sb,
		string className,
		EquatableArray<VariantModel> variants,
		string discriminatorFieldName)
	{
		var converterName = $"{className}JsonConverter";

		sb.AppendLine($"    public class {converterName} : System.Text.Json.Serialization.JsonConverter<{className}>");
		sb.AppendLine("    {");

		sb.AppendLine($"        public override {className}? Read(ref System.Text.Json.Utf8JsonReader reader, System.Type typeToConvert, System.Text.Json.JsonSerializerOptions options)");
		sb.AppendLine("        {");
		sb.AppendLine("            using var doc = System.Text.Json.JsonDocument.ParseValue(ref reader);");
		sb.AppendLine("            var root = doc.RootElement;");
		sb.AppendLine();
		sb.AppendLine($"            string? typeName = null;");
		sb.AppendLine($"            if (root.TryGetProperty(\"{discriminatorFieldName}\", out var typeElement))");
		sb.AppendLine("            {");
		sb.AppendLine("                typeName = typeElement.GetString();");
		sb.AppendLine("            }");
		sb.AppendLine("            else");
		sb.AppendLine("            {");
		sb.AppendLine("                typeName = InferVariantFromProperties(root);");
		sb.AppendLine("            }");
		sb.AppendLine();
		var isFirst = true;
		foreach (var variant in variants)
		{
			var variantName = variant.Name;
			var keyword = isFirst ? "if" : "else if";
			sb.AppendLine($"            {keyword} (string.Equals(typeName, \"{variantName}\", System.StringComparison.OrdinalIgnoreCase))");
			sb.AppendLine("            {");

			if (variant.Parameters.Count > 0)
			{
				foreach (var param in variant.Parameters)
				{
					var paramName = param.Name;
					var paramType = param.Type;
					sb.AppendLine($"                var @{paramName} = System.Text.Json.JsonSerializer.Deserialize<{paramType}>(root.GetProperty(\"{paramName}\").GetRawText(), options)!;");
				}

				var args = string.Join(", ", variant.Parameters.Select(p => $"@{p.Name}"));
				sb.AppendLine($"                return {className}.{variantName}({args});");
			}
			else
			{
				sb.AppendLine($"                return {className}.{variantName}();");
			}

			sb.AppendLine("            }");
			isFirst = false;
		}

		sb.AppendLine("            else");
		sb.AppendLine("            {");
		sb.AppendLine("                throw new System.Text.Json.JsonException($\"Unknown variant type: {typeName}\");");
		sb.AppendLine("            }");
		sb.AppendLine("        }");

		sb.AppendLine();
		sb.AppendLine("        private static string InferVariantFromProperties(System.Text.Json.JsonElement root)");
		sb.AppendLine("        {");
		sb.AppendLine("            var properties = new System.Collections.Generic.HashSet<string>();");
		sb.AppendLine("            foreach (var prop in root.EnumerateObject())");
		sb.AppendLine("                properties.Add(prop.Name);");
		sb.AppendLine();
		sb.AppendLine("            string? match = null;");

		foreach (var variant in variants)
		{
			var variantName = variant.Name;
			var paramNames = variant.Parameters.Select(p => p.Name).ToList();

			if (paramNames.Count == 0)
			{
				sb.AppendLine($"            if (properties.Count == 0)");
			}
			else
			{
				var conditions = string.Join(" && ", paramNames.Select(p => $"properties.Contains(\"{p}\")"));
				sb.AppendLine($"            if ({conditions})");
			}

			sb.AppendLine("            {");
			sb.AppendLine($"                if (match != null) throw new System.Text.Json.JsonException(\"Ambiguous variant: multiple variants match the provided properties. Include the discriminator field to disambiguate.\");");
			sb.AppendLine($"                match = \"{variantName}\";");
			sb.AppendLine("            }");
		}

		sb.AppendLine();
		sb.AppendLine($"            if (match == null) throw new System.Text.Json.JsonException(\"Unable to infer variant type from properties. Include the discriminator field.\");");
		sb.AppendLine("            return match;");
		sb.AppendLine("        }");

		sb.AppendLine();
		sb.AppendLine($"        public override void Write(System.Text.Json.Utf8JsonWriter writer, {className} value, System.Text.Json.JsonSerializerOptions options)");
		sb.AppendLine("        {");
		sb.AppendLine("            writer.WriteStartObject();");
		sb.AppendLine();
		sb.AppendLine("            value.Switch(");

		for (int i = 0; i < variants.Count; i++)
		{
			var variant = variants[i];
			var variantName = variant.Name;
			var lambdaParam = $"variant{i}";
			var comma = i < variants.Count - 1 ? "," : "";

			sb.AppendLine($"                {lambdaParam} =>");
			sb.AppendLine("                {");
			sb.AppendLine($"                    writer.WriteString(\"{discriminatorFieldName}\", \"{variantName}\");");

			foreach (var param in variant.Parameters)
			{
				var propName = FirstCharToUpper(param.Name);
				var jsonName = param.Name;
				sb.AppendLine($"                    writer.WritePropertyName(\"{jsonName}\");");
				sb.AppendLine($"                    System.Text.Json.JsonSerializer.Serialize(writer, {lambdaParam}.{propName}, options);");
			}

			sb.AppendLine($"                }}{comma}");
		}

		sb.AppendLine("            );");
		sb.AppendLine();
		sb.AppendLine("            writer.WriteEndObject();");
		sb.AppendLine("        }");

		sb.AppendLine("    }");
	}

	private static void GenerateNewtonsoftJsonConverterClass(
		StringBuilder sb,
		string className,
		EquatableArray<VariantModel> variants,
		string discriminatorFieldName)
	{
		var converterName = $"{className}NewtonsoftJsonConverter";

		sb.AppendLine($"    public class {converterName} : Newtonsoft.Json.JsonConverter<{className}>");
		sb.AppendLine("    {");

		sb.AppendLine($"        public override {className}? ReadJson(Newtonsoft.Json.JsonReader reader, System.Type objectType, {className}? existingValue, bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer)");
		sb.AppendLine("        {");
		sb.AppendLine("            if (reader.TokenType == Newtonsoft.Json.JsonToken.Null) return null;");
		sb.AppendLine("            var obj = Newtonsoft.Json.Linq.JObject.Load(reader);");
		sb.AppendLine($"            var typeName = (string?)obj[\"{discriminatorFieldName}\"];");
		sb.AppendLine($"            if (typeName == null)");
		sb.AppendLine("            {");
		sb.AppendLine("                typeName = InferVariantFromProperties(obj);");
		sb.AppendLine("            }");
		sb.AppendLine();
		var isFirstNewtonsoft = true;
		foreach (var variant in variants)
		{
			var variantName = variant.Name;
			var keyword = isFirstNewtonsoft ? "if" : "else if";
			sb.AppendLine($"            {keyword} (string.Equals(typeName, \"{variantName}\", System.StringComparison.OrdinalIgnoreCase))");
			sb.AppendLine("            {");

			if (variant.Parameters.Count > 0)
			{
				foreach (var param in variant.Parameters)
				{
					var paramName = param.Name;
					var paramType = param.Type;
					sb.AppendLine($"                var @{paramName} = obj[\"{paramName}\"]!.ToObject<{paramType}>(serializer)!;");
				}

				var args = string.Join(", ", variant.Parameters.Select(p => $"@{p.Name}"));
				sb.AppendLine($"                return {className}.{variantName}({args});");
			}
			else
			{
				sb.AppendLine($"                return {className}.{variantName}();");
			}

			sb.AppendLine("            }");
			isFirstNewtonsoft = false;
		}

		sb.AppendLine("            else");
		sb.AppendLine("            {");
		sb.AppendLine($"                throw new Newtonsoft.Json.JsonSerializationException($\"Unknown variant type: {{typeName}}\");");
		sb.AppendLine("            }");
		sb.AppendLine("        }");

		sb.AppendLine();
		sb.AppendLine("        private static string InferVariantFromProperties(Newtonsoft.Json.Linq.JObject obj)");
		sb.AppendLine("        {");
		sb.AppendLine("            var properties = new System.Collections.Generic.HashSet<string>();");
		sb.AppendLine("            foreach (var prop in obj.Properties())");
		sb.AppendLine("                properties.Add(prop.Name);");
		sb.AppendLine();
		sb.AppendLine("            string? match = null;");

		foreach (var variant in variants)
		{
			var variantName = variant.Name;
			var paramNames = variant.Parameters.Select(p => p.Name).ToList();

			if (paramNames.Count == 0)
			{
				sb.AppendLine($"            if (properties.Count == 0)");
			}
			else
			{
				var conditions = string.Join(" && ", paramNames.Select(p => $"properties.Contains(\"{p}\")"));
				sb.AppendLine($"            if ({conditions})");
			}

			sb.AppendLine("            {");
			sb.AppendLine($"                if (match != null) throw new Newtonsoft.Json.JsonSerializationException(\"Ambiguous variant: multiple variants match the provided properties. Include the discriminator field to disambiguate.\");");
			sb.AppendLine($"                match = \"{variantName}\";");
			sb.AppendLine("            }");
		}

		sb.AppendLine();
		sb.AppendLine($"            if (match == null) throw new Newtonsoft.Json.JsonSerializationException(\"Unable to infer variant type from properties. Include the discriminator field.\");");
		sb.AppendLine("            return match;");
		sb.AppendLine("        }");

		sb.AppendLine();
		sb.AppendLine($"        public override void WriteJson(Newtonsoft.Json.JsonWriter writer, {className}? value, Newtonsoft.Json.JsonSerializer serializer)");
		sb.AppendLine("        {");
		sb.AppendLine("            if (value == null) { writer.WriteNull(); return; }");
		sb.AppendLine("            writer.WriteStartObject();");
		sb.AppendLine();
		sb.AppendLine("            value.Switch(");

		for (int i = 0; i < variants.Count; i++)
		{
			var variant = variants[i];
			var variantName = variant.Name;
			var lambdaParam = $"variant{i}";
			var comma = i < variants.Count - 1 ? "," : "";

			sb.AppendLine($"                {lambdaParam} =>");
			sb.AppendLine("                {");
			sb.AppendLine($"                    writer.WritePropertyName(\"{discriminatorFieldName}\");");
			sb.AppendLine($"                    writer.WriteValue(\"{variantName}\");");

			foreach (var param in variant.Parameters)
			{
				var jsonName = param.Name;
				var propName = FirstCharToUpper(param.Name);
				sb.AppendLine($"                    writer.WritePropertyName(\"{jsonName}\");");
				sb.AppendLine($"                    serializer.Serialize(writer, {lambdaParam}.{propName});");
			}

			sb.AppendLine($"                }}{comma}");
		}

		sb.AppendLine("            );");
		sb.AppendLine();
		sb.AppendLine("            writer.WriteEndObject();");
		sb.AppendLine("        }");

		sb.AppendLine("    }");
	}

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

internal sealed record VariantModel(string Name, EquatableArray<VariantParameter> Parameters);

internal readonly record struct VariantParameter(string Name, string Type);

internal sealed record UnionModel(
	string Namespace,
	string ClassName,
	EquatableArray<VariantModel> Variants,
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
