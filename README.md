# Gorilla

<img src="icon.png" alt="drawing" width="256"/>

A .NET source generator for creating discriminated unions in C# with OneOf. Inspired by the flutter package [freezed](https://pub.dev/packages/freezed) and the F# discriminated union pattern.

## 🚀 Overview

Gorilla helps you create type-safe, exhaustive discriminated unions in C#. It automatically generates matching methods, type checks, and conversions to make working with union types intuitive and safe.
Gorilla uses [OneOf](https://github.com/mcintyre321/OneOf) under the hood to implement the discriminated union pattern but adds a layer of compile-time convenience and safety through source generation.

Gorilla supports:

- top-level flat unions
- unions nested inside classes, interfaces, and other containing types
- abstract hierarchical unions where nested sub-unions participate in outer `Match(...)` dispatch
- generated JSON converters for both flat and hierarchical unions

## 📦 Installation

```bash
# Install both packages
dotnet add package Zooper.Gorilla.Attributes
dotnet add package Zooper.Gorilla.Generators
```

## 🔧 Usage

### 1. Define your union type

Add the `[DiscriminatedUnion]` attribute to a partial type and declare one `[Variant]` factory per case:

```csharp
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public partial class SignInError
{
	[Variant]
	public static partial SignInError ServiceUnavailable();

	[Variant]
	public static partial SignInError InvalidCredentials();

	[Variant]
	public static partial SignInError InternalError();
}
```

### 2. Use your union type

```csharp
return SignInError.ServiceUnavailable();
```

## 🗃️ JSON Serialization

Gorilla can generate JSON converters automatically when your project references `System.Text.Json` or `Newtonsoft.Json`.

- `System.Text.Json` support emits a generated `JsonConverter<T>` and applies `[System.Text.Json.Serialization.JsonConverter(...)]`
- `Newtonsoft.Json` support emits a generated `JsonConverter<T>` and applies `[Newtonsoft.Json.JsonConverterAttribute(...)]`
- the discriminator field defaults to `$type`
- converter generation can be overridden per union with `GenerateJsonConverter`, `GenerateNewtonsoftJsonConverter`, and `DiscriminatorFieldName`

```csharp
using System.Text.Json;

var value = CreateProfileDto.Person("John", "Doe");

var json = JsonSerializer.Serialize(value);
var roundTripped = JsonSerializer.Deserialize<CreateProfileDto>(json)!;
```

JSON support works for both flat unions and hierarchical unions.

## 🧩 Nested Unions

Nested unions can stay attached to the owning contract or payload type instead of being flattened into top-level declarations.

```csharp
using Zooper.Gorilla.Attributes;

public partial interface IEntityCreatedContract
{
	public sealed record ContractVersion1(
		EntityId EntityId,
		EntityName Name,
		IEntityPayload Info,
		EntityOrder Order) : IEntityCreatedContract;

	public partial interface IEntityPayload
	{
		[DiscriminatedUnion]
		public sealed partial class V1 : IEntityPayload
		{
			[Variant]
			public static partial V1 Created();

			[Variant]
			public static partial V1 Standard(string category, bool isVisible);
		}
	}
}
```

That generated union can still be matched normally:

```csharp
var payload = IEntityCreatedContract.IEntityPayload.V1.Standard("alpha", true);

var description = payload.Match(
	created => "created",
	standard => $"standard:{standard.Category}:{standard.IsVisible}");
```

See the full sample in [Zooper.Gorilla.Sample/NestedContractSamples.cs](Zooper.Gorilla.Sample/NestedContractSamples.cs).

## 🌲 Hierarchical Unions

Abstract unions can contain nested sub-unions that also flow through the outer union API.

```csharp
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial record ContractOutcome
{
	[Variant]
	public static partial ContractOutcome Success(string contractId);

	[DiscriminatedUnion]
	public abstract partial record Rejected : ContractOutcome
	{
		[Variant]
		public static partial Rejected Validation(string field);

		[DiscriminatedUnion]
		public abstract partial record Security : Rejected
		{
			[Variant]
			public static partial Security Unauthorized(string reason);
		}
	}
}
```

Each level gets its own `Match(...)`, while nested values still participate in outer matching:

```csharp
ContractOutcome outcome = ContractOutcome.Rejected.Security.Unauthorized("expired-session");

var description = outcome.Match(
	success => $"success:{success.ContractId}",
	rejected => rejected.Match(
		validation => $"validation:{validation.Field}",
		security => security.Match(
			unauthorized => $"unauthorized:{unauthorized.Reason}")));
```

See the sample in [Zooper.Gorilla.Sample/ContractOutcome.cs](Zooper.Gorilla.Sample/ContractOutcome.cs).

## 🧪 Sample Project

The sample project includes:

- flat unions in [Zooper.Gorilla.Sample/CreateProfileDto.cs](Zooper.Gorilla.Sample/CreateProfileDto.cs), [Zooper.Gorilla.Sample/SignInError.cs](Zooper.Gorilla.Sample/SignInError.cs), and [Zooper.Gorilla.Sample/SignUpError.cs](Zooper.Gorilla.Sample/SignUpError.cs)
- nested contract-owned unions in [Zooper.Gorilla.Sample/NestedContractSamples.cs](Zooper.Gorilla.Sample/NestedContractSamples.cs)
- hierarchical abstract unions in [Zooper.Gorilla.Sample/ContractOutcome.cs](Zooper.Gorilla.Sample/ContractOutcome.cs)

## 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🙏 Acknowledgements

- [OneOf](https://github.com/mcintyre321/OneOf) - The foundation for this library
- [F# Discriminated Unions](https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/discriminated-unions) - The inspiration for the pattern

Made with ❤️ by the Zooper team
