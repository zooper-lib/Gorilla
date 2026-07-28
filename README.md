# Gorilla

<img src="icon.png" alt="drawing" width="256"/>

A .NET source generator for creating discriminated unions in C#. Inspired by the flutter package [freezed](https://pub.dev/packages/freezed) and the F# discriminated union pattern.

## 🚀 Overview

Gorilla helps you create type-safe, exhaustive discriminated unions in C#. It automatically generates factory methods, exhaustive dispatch, variant-named accessors, and JSON converters to make working with union types intuitive and safe.

A union is an `abstract` base type; each variant is a `sealed` nested type deriving from it. That means a variant value *is* a union value — type patterns, `switch`, and assignment all work naturally — with no wrapper type, no third-party dependency, and no ceiling on the number of variants.

Gorilla supports:

- unions declared as `abstract partial record` or `abstract partial class`
- unions nested inside classes, interfaces, and other containing types
- sub-unions that participate in the outer union's `Match` / `Switch` dispatch
- generated JSON converters for every union

## 📦 Installation

```bash
# Install both packages
dotnet add package Zooper.Gorilla.Attributes
dotnet add package Zooper.Gorilla.Generators
```

## 🔧 Usage

### 1. Define your union type

Add the `[DiscriminatedUnion]` attribute to an **abstract partial** type and declare one `[Variant]` factory per case:

```csharp
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial record SignInError
{
	[Variant]
	public static partial SignInError ServiceUnavailable();

	[Variant]
	public static partial SignInError InvalidCredentials();

	[Variant]
	public static partial SignInError InternalError();
}
```

The declaration **must** be `abstract` — the generated variants derive from it. A `sealed` or plain
declaration reports `ZGOR003`, which ships with a code fix ("Make discriminated union abstract",
and "Fix all occurrences in solution" for a whole codebase at once).

### 2. Use your union type

```csharp
return SignInError.ServiceUnavailable();
```

### `record` or `class`?

Prefer `record`. Gorilla generates no `Equals`, `GetHashCode`, or `ToString` — equality follows the
keyword you chose, exactly as it does for every other type in C#:

| | `abstract partial record` | `abstract partial class` |
|---|---|---|
| `Card("4111") == Card("4111")` | `true` | `false` (reference equality) |
| hash codes agree | yes | no |
| `ToString()` | `CardVariant { Number = 4111 }` | `CardVariant` |

Choose `class` when you specifically want reference identity.

## 🎯 Working with a union value

Three ways in, with different guarantees:

**`Match` / `Switch` — the exhaustive door.** One required handler per variant and per sub-union.
Adding a variant is a compile error at every call site, naming the case you missed. Use these when
you must handle every case.

```csharp
var description = payload.Match(
	created => "created",
	standard => $"standard:{standard.Category}");

payload.Switch(
	created: _ => Console.WriteLine("created"),
	standard: standard => Console.WriteLine(standard.Category));
```

Handler parameters are named after the variants, so they can be passed as named arguments in any
order. Reordering variants in the declaration then becomes a compile error rather than a silent
handler remap. Positional calls work too.

**Type patterns — for reading a payload.** Tests and binds in one step:

```csharp
if (payload is EntityState.StandardVariant standard)
{
	Console.WriteLine(standard.Category);
}
```

**`IsX` / `AsX()` / `TryPickX` — payload-free shorthand.** Generated for every variant *and* every
sub-union:

```csharp
if (payload.IsCreated) { /* ... */ }             // bool property, never throws

var standard = payload.AsStandard();             // method; throws if it is another variant
if (payload.TryPickStandard(out var value)) { }  // method; returns false instead of throwing
```

`AsX` is a **method**, not a property, so reflection-based property walkers — ASP.NET model
validation, debugger watch windows, IntelliSense tooltips, object mappers — never invoke a getter
that throws.

> **Exhaustiveness is guaranteed by `Match` and `Switch` only.** Type patterns and the accessors
> carry no such guarantee: an `if (x.IsA) … else …` keeps compiling when a variant is added. Reach
> for `Match`/`Switch` when completeness matters.

Note that accessor names derive from variant names, so a union declaring both a variant `Card` and
its own member `IsCard` fails to compile with `CS0102`.

## 🗃️ JSON Serialization

Gorilla can generate JSON converters automatically when your project references `System.Text.Json` or `Newtonsoft.Json`.

- `System.Text.Json` support emits a generated `JsonConverter<T>` and applies `[System.Text.Json.Serialization.JsonConverter(...)]`
- `Newtonsoft.Json` support emits a generated `JsonConverter<T>` and applies `[Newtonsoft.Json.JsonConverterAttribute(...)]`
- the discriminator field defaults to `$type` and is **required** when reading
- converter generation can be overridden per union with `GenerateJsonConverter`, `GenerateNewtonsoftJsonConverter`, and `DiscriminatorFieldName`
- variant property keys honor the caller's serializer configuration: `System.Text.Json` converters resolve keys through `JsonSerializerOptions.PropertyNamingPolicy` (e.g. `CamelCase`, `SnakeCaseLower`) and honor `PropertyNameCaseInsensitive` when reading; `Newtonsoft.Json` converters resolve keys through `serializer.ContractResolver` (e.g. `CamelCasePropertyNamesContractResolver`). The discriminator field name and value are configuration/data and stay literal — they are not transformed by the naming policy.

```csharp
using System.Text.Json;

var value = CreateProfileDto.Person("John", "Doe");

var json = JsonSerializer.Serialize(value);
var roundTripped = JsonSerializer.Deserialize<CreateProfileDto>(json)!;
```

A payload without the discriminator field is an error rather than a guess, and the two failure
modes are reported distinctly:

```
Missing discriminator field '$type' when deserializing 'CreateProfileDto'.
Unrecognized discriminator value 'Nonexistent' when deserializing 'CreateProfileDto'.
```

### Serializing by runtime type

A union value keeps its discriminator no matter which static type it is written through — the union,
one of its generated variants, or `object`:

```csharp
object boxed = CreateProfileDto.Person("John", "Doe");
JsonSerializer.Serialize(boxed);        // {"$type":"Person","firstName":"John", …}

var variant = (CreateProfileDto.PersonVariant)CreateProfileDto.Person("John", "Doe");
JsonSerializer.Serialize(variant);      // same output
```

This matters for any path where the runtime type reaches the serializer rather than the declared one —
an ASP.NET minimal API returning a factory call directly, a `Serialize(object)`, a logging sink. The
`System.Text.Json` converter attribute is emitted on each generated variant class as well as on the
union, and both converters claim the whole hierarchy through `CanConvert`.

### Generic unions

`[DiscriminatedUnion]` types may declare type parameters, and their converters work under both
serializers:

```csharp
[DiscriminatedUnion]
public abstract partial record Disclosure<T>
{
	[Variant]
	public static partial Disclosure<T> Visible(T value);

	[Variant]
	public static partial Disclosure<T> NotProvided();
}

var json = JsonSerializer.Serialize(Disclosure<string>.Visible("hello"));
// {"$type":"Visible","value":"hello"}
```

Call sites name the closed type — `Disclosure<string>.Visible("x")` — because C# infers method type
arguments from arguments only, never from the return type, so a non-generic companion class would
shorten some factories and not others with no rule visible at the call site.

Because an attribute argument cannot name a type parameter, a generic union's attribute names a
generated non-generic registration type (a `JsonConverterFactory` for `System.Text.Json`, a delegating
shim for `Newtonsoft.Json`) that closes the converter over the value's type arguments. The same applies
to a union nested inside a *generic* containing type, generic or not itself: its converter is emitted
outside that container, absorbs the container's type parameters, and is named with the container's name
as a prefix — `Outer<TOuter>.Leaf<T>` gets `Outer_LeafJsonConverter<TOuter, T>`. When no containing type
is generic, the converter's name and position are unchanged from earlier releases, so an existing
`settings.Converters.Add(new Outer.LeafJsonConverter())` keeps compiling.

**Native AOT:** a generic union's converter is constructed at runtime via `MakeGenericType`, because the
set of closed constructions is not knowable when the code is generated. The closed union types must
therefore be rooted. The `IL3050` warning is suppressed at the generated call sites rather than surfaced,
since it lands in a file the consumer cannot edit.

**Variance is not supported and nothing is emitted for it.** `Disclosure<string>` is not usable where
`Disclosure<object>` is expected. `CS1960` restricts variant type parameters to interfaces and delegates,
and a Gorilla union must be a class or record — variants derive from it and its private constructor
closes the hierarchy — so the annotation is out of reach by construction. A covariant interface emitted
beside the union would not help much either: it cannot carry `Match` (a variant type is invariant in `T`,
so handlers would receive only the bare payload), and it does not apply to value types at all
(`CS0266` — variance rides on reference conversions, and boxing is not one). A generic method,
`static string Describe<T>(Disclosure<T> value)`, covers most "works for any payload" needs. If you have
a genuine need, the union is `partial`: declare your own interface and add it on your own part.

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
		public abstract partial class V1 : IEntityPayload
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

## 🌲 Sub-Unions

Any union can contain nested sub-unions that also flow through the outer union's API. Nesting is
what makes a sub-union recognised — it is not a different kind of union.

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

Each level gets its own `Match(...)` and `Switch(...)`, while nested values still participate in outer matching:

```csharp
ContractOutcome outcome = ContractOutcome.Rejected.Security.Unauthorized("expired-session");

var description = outcome.Match(
	success => $"success:{success.ContractId}",
	rejected => rejected.Match(
		validation => $"validation:{validation.Field}",
		security => security.Match(
			unauthorized => $"unauthorized:{unauthorized.Reason}")));
```

The union's constructor is `private`, so the hierarchy is closed: only generated variants and
nested sub-unions can derive from a union. A hand-written subtype declared outside the union body
is a compile error (`CS0122`) rather than a runtime `InvalidOperationException` at dispatch.

See the sample in [Zooper.Gorilla.Sample/ContractOutcome.cs](Zooper.Gorilla.Sample/ContractOutcome.cs).

## ⬆️ Migrating from 1.x

Version 2.0 replaces the OneOf-based representation with the abstract-base representation described
above. The upgrade is source-breaking; the edits are mechanical.

1. **Declare every union `abstract`.** `sealed partial class` and plain `partial class` become
   `abstract partial class`. `ZGOR003` flags each one, and the shipped code fix applies the edit —
   use "Fix all occurrences in solution" to do the whole codebase at once.
2. **Replace positional accessors with variant-named ones.** `IsT0` → `IsCard`, `AsT0` → `AsCard()`
   (now a method), `TryPickT0(out var v, out var rest)` → `TryPickCard(out var v)` (no remainder
   parameter).
3. **`Value` and `Index` are gone.** `Value` is meaningless once the union *is* the value; use the
   variant type directly or `Match`. `Index` exposed positional identity that nothing consumes.
4. **Hand-written subtypes no longer compile.** `public sealed record Cancelled(...) : Outcome;`
   now fails with `CS0122`. That code was already failing at runtime with `Unknown variant` —
   declare it as a `[Variant]` or a nested sub-union instead.
5. **The discriminator field is required when deserializing.** Property-set inference is gone;
   Gorilla has always *written* the discriminator, so only hand-written or foreign JSON is affected.
   Callers previously relying on inference now get an explicit error naming the missing field
   instead of a possibly-wrong variant.
6. **`SuppressValidation` is removed.** It existed only to stop ASP.NET's validation visitor from
   invoking a throwing `AsT0` getter, which no longer exists. Delete the argument.
7. **Add your own OneOf `PackageReference` if you use OneOf types.** Gorilla no longer depends on
   OneOf, so a project that was compiling on the transitive reference needs its own.
8. **Unions can now be `record`s**, which `OneOfBase` forbade — and `record` is the better default.

## 🧪 Sample Project

The sample project includes:

- simple unions in [Zooper.Gorilla.Sample/CreateProfileDto.cs](Zooper.Gorilla.Sample/CreateProfileDto.cs), [Zooper.Gorilla.Sample/SignInError.cs](Zooper.Gorilla.Sample/SignInError.cs), and [Zooper.Gorilla.Sample/SignUpError.cs](Zooper.Gorilla.Sample/SignUpError.cs)
- nested contract-owned unions in [Zooper.Gorilla.Sample/NestedContractSamples.cs](Zooper.Gorilla.Sample/NestedContractSamples.cs)
- unions with sub-unions in [Zooper.Gorilla.Sample/ContractOutcome.cs](Zooper.Gorilla.Sample/ContractOutcome.cs)

## 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🙏 Acknowledgements

- [OneOf](https://github.com/mcintyre321/OneOf) - The foundation this library was built on through 1.x
- [F# Discriminated Unions](https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/discriminated-unions) - The inspiration for the pattern

Made with ❤️ by the Zooper team
