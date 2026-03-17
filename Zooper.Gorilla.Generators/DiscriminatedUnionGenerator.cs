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

			var config = GetConfig(classSymbol, compilation);
			var source = GenerateSource(classSymbol, variants, config);
			context.AddSource($"{classSymbol.Name}.g.cs", SourceText.From(source, Encoding.UTF8));
		}
	}

	private string GenerateSource(
		INamedTypeSymbol classSymbol,
		List<IMethodSymbol> variants,
		UnionConfig config)
	{
		var namespaceName = classSymbol.ContainingNamespace.ToDisplayString();
		var className = classSymbol.Name;

		var sb = new StringBuilder();
		sb.AppendLine("#nullable enable");
		sb.AppendLine("using OneOf;");
		sb.AppendLine("using System;");
		sb.AppendLine();

		sb.AppendLine($"namespace {namespaceName}");
		sb.AppendLine("{");

		if (config.GenerateJsonConverter)
		{
			sb.AppendLine($"    [System.Text.Json.Serialization.JsonConverter(typeof({className}JsonConverter))]");
		}

		if (config.GenerateNewtonsoftJsonConverter)
		{
			sb.AppendLine($"    [Newtonsoft.Json.JsonConverterAttribute(typeof({className}NewtonsoftJsonConverter))]");
		}

		if (config.SuppressValidation)
		{
			sb.AppendLine("    [Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNever]");
		}

		GenerateClassHeader(sb, className, variants);
		sb.AppendLine("    {");

		GenerateConstructor(sb, className, variants);
		sb.AppendLine();

		GenerateVariantMethods(sb, className, variants);

		GenerateVariantClasses(sb, className, variants);

		sb.AppendLine("    }");

		if (config.GenerateJsonConverter)
		{
			sb.AppendLine();
			GenerateJsonConverterClass(sb, className, variants, config.DiscriminatorFieldName);
		}

		if (config.GenerateNewtonsoftJsonConverter)
		{
			sb.AppendLine();
			GenerateNewtonsoftJsonConverterClass(sb, className, variants, config.DiscriminatorFieldName);
		}

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

	private void GenerateJsonConverterClass(
		StringBuilder sb,
		string className,
		List<IMethodSymbol> variants,
		string discriminatorFieldName)
	{
		var converterName = $"{className}JsonConverter";

		sb.AppendLine($"    public class {converterName} : System.Text.Json.Serialization.JsonConverter<{className}>");
		sb.AppendLine("    {");

		// Read method
		sb.AppendLine($"        public override {className}? Read(ref System.Text.Json.Utf8JsonReader reader, System.Type typeToConvert, System.Text.Json.JsonSerializerOptions options)");
		sb.AppendLine("        {");
		sb.AppendLine("            using var doc = System.Text.Json.JsonDocument.ParseValue(ref reader);");
		sb.AppendLine("            var root = doc.RootElement;");
		sb.AppendLine();
		sb.AppendLine($"            if (!root.TryGetProperty(\"{discriminatorFieldName}\", out var typeElement))");
		sb.AppendLine($"                throw new System.Text.Json.JsonException(\"Missing discriminator field '{discriminatorFieldName}'\");");
		sb.AppendLine();
		sb.AppendLine("            var typeName = typeElement.GetString();");
		sb.AppendLine();
		sb.AppendLine("            switch (typeName)");
		sb.AppendLine("            {");

		foreach (var variant in variants)
		{
			var variantName = variant.Name;
			sb.AppendLine($"                case \"{variantName}\":");
			sb.AppendLine("                {");

			if (variant.Parameters.Any())
			{
				foreach (var param in variant.Parameters)
				{
					var paramName = param.Name;
					var paramType = param.Type.ToDisplayString();
					sb.AppendLine($"                    var @{paramName} = System.Text.Json.JsonSerializer.Deserialize<{paramType}>(root.GetProperty(\"{paramName}\").GetRawText(), options)!;");
				}

				var args = string.Join(", ", variant.Parameters.Select(p => $"@{p.Name}"));
				sb.AppendLine($"                    return {className}.{variantName}({args});");
			}
			else
			{
				sb.AppendLine($"                    return {className}.{variantName}();");
			}

			sb.AppendLine("                }");
		}

		sb.AppendLine("                default:");
		sb.AppendLine("                    throw new System.Text.Json.JsonException($\"Unknown variant type: {typeName}\");");
		sb.AppendLine("            }");
		sb.AppendLine("        }");

		// Write method
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

	private void GenerateNewtonsoftJsonConverterClass(
		StringBuilder sb,
		string className,
		List<IMethodSymbol> variants,
		string discriminatorFieldName)
	{
		var converterName = $"{className}NewtonsoftJsonConverter";

		sb.AppendLine($"    public class {converterName} : Newtonsoft.Json.JsonConverter<{className}>");
		sb.AppendLine("    {");

		// ReadJson
		sb.AppendLine($"        public override {className}? ReadJson(Newtonsoft.Json.JsonReader reader, System.Type objectType, {className}? existingValue, bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer)");
		sb.AppendLine("        {");
		sb.AppendLine("            if (reader.TokenType == Newtonsoft.Json.JsonToken.Null) return null;");
		sb.AppendLine("            var obj = Newtonsoft.Json.Linq.JObject.Load(reader);");
		sb.AppendLine($"            var typeName = obj[\"{discriminatorFieldName}\"]?.Value<string>();");
		sb.AppendLine();
		sb.AppendLine("            switch (typeName)");
		sb.AppendLine("            {");

		foreach (var variant in variants)
		{
			var variantName = variant.Name;
			sb.AppendLine($"                case \"{variantName}\":");
			sb.AppendLine("                {");

			if (variant.Parameters.Any())
			{
				foreach (var param in variant.Parameters)
				{
					var paramName = param.Name;
					var paramType = param.Type.ToDisplayString();
					sb.AppendLine($"                    var @{paramName} = obj[\"{paramName}\"]!.ToObject<{paramType}>(serializer)!;");
				}

				var args = string.Join(", ", variant.Parameters.Select(p => $"@{p.Name}"));
				sb.AppendLine($"                    return {className}.{variantName}({args});");
			}
			else
			{
				sb.AppendLine($"                    return {className}.{variantName}();");
			}

			sb.AppendLine("                }");
		}

		sb.AppendLine("                default:");
		sb.AppendLine($"                    throw new Newtonsoft.Json.JsonSerializationException($\"Unknown variant type: {{typeName}}\");");
		sb.AppendLine("            }");
		sb.AppendLine("        }");

		// WriteJson
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

	private static UnionConfig GetConfig(INamedTypeSymbol classSymbol, Compilation compilation)
	{
		// Auto-detect available frameworks from the compilation references
		var hasNewtonsoftJson = compilation.GetTypeByMetadataName("Newtonsoft.Json.JsonConverter") != null;
		var hasSystemTextJson = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonConverter") != null;
		var hasAspNetValidation = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNeverAttribute") != null;

		var config = new UnionConfig
		{
			GenerateJsonConverter = hasSystemTextJson,
			GenerateNewtonsoftJsonConverter = hasNewtonsoftJson,
			SuppressValidation = hasAspNetValidation,
		};

		var attr = classSymbol.GetAttributes()
			.FirstOrDefault(a => a.AttributeClass?.Name == "DiscriminatedUnionAttribute");

		if (attr != null)
		{
			foreach (var arg in attr.NamedArguments)
			{
				switch (arg.Key)
				{
					case "SuppressValidation":
						config.SuppressValidation = (bool)arg.Value.Value!;
						break;
					case "GenerateJsonConverter":
						config.GenerateJsonConverter = (bool)arg.Value.Value!;
						break;
					case "DiscriminatorFieldName":
						config.DiscriminatorFieldName = (string)arg.Value.Value!;
						break;
					case "GenerateNewtonsoftJsonConverter":
						config.GenerateNewtonsoftJsonConverter = (bool)arg.Value.Value!;
						break;
				}
			}
		}

		return config;
	}

	private class UnionConfig
	{
		public bool SuppressValidation { get; set; }
		public string DiscriminatorFieldName { get; set; } = "type";
		public bool GenerateJsonConverter { get; set; } = true;
		public bool GenerateNewtonsoftJsonConverter { get; set; } = true;

	}

	// Helper methods
	private string FirstCharToLower(string input) =>
		string.IsNullOrEmpty(input) ? input : char.ToLower(input[0]) + input.Substring(1);

	private string FirstCharToUpper(string input) =>
		string.IsNullOrEmpty(input) ? input : char.ToUpper(input[0]) + input.Substring(1);
}