# Generic unions

## The idea

Let a `[DiscriminatedUnion]` type declare type parameters, so a union can be written once and reused
for any payload type:

```csharp
[DiscriminatedUnion]
public abstract partial record Disclosure<T>
{
	[Variant] public static partial Disclosure<T> Visible(T value);
	[Variant] public static partial Disclosure<T> NotProvided();
	[Variant] public static partial Disclosure<T> Redacted();
}
```

Note the declaration is `abstract partial record`, not `sealed partial class` — that requirement is
unchanged (`ZGOR003`), because the variants are still generated as nested types deriving from it.

The generated shape is the shape we already emit, with the type parameters carried along:

```csharp
public abstract partial record Disclosure<T>
{
	private Disclosure() { }

	public static partial Disclosure<T> Visible(T value) => new VisibleVariant(value);
	public static partial Disclosure<T> NotProvided() => new NotProvidedVariant();
	public static partial Disclosure<T> Redacted() => new RedactedVariant();

	public TResult Match<TResult>(
		Func<VisibleVariant, TResult> visible,
		Func<NotProvidedVariant, TResult> notProvided,
		Func<RedactedVariant, TResult> redacted) => …

	public sealed record VisibleVariant : Disclosure<T>
	{
		internal VisibleVariant(T value) { this.Value = value; }
		public T Value { get; }
	}
	…
}
```

Nested types inherit the enclosing type's parameters, so the variant classes need no type parameter
list and no constraint clauses of their own. From outside, a variant is `Disclosure<string>.VisibleVariant`.

## Why

`Option<T>`, `Result<TValue, TError>`, `Disclosure<T>`, `Paged<T>` — the payload-carrying wrapper is the
single most common union shape, and today it can only be written by hand, once per concrete payload
type. Every other Gorilla feature (exhaustive `Match`, variant accessors, JSON) is already generic-safe
in principle; only the emitter's string building assumes a bare type name.

## What actually has to change

Most of the generator is unaffected. `VariantParameter.Type` already comes from
`ITypeSymbol.ToDisplayString()`, so a parameter typed `T` already round-trips correctly. The work is
in five places.

**1. Model.** `UnionModel` gains `TypeParameters` (an `EquatableArray<string>` of names) and, once JSON
lands, `TypeParameterConstraints` (the `where …` clauses as pre-rendered strings). Both must be plain
strings — no `ITypeSymbol` in the model, or incremental caching breaks.

**2. Self-references in emitted text.** Every place that writes the union's name as a *type* needs the
type argument list appended: `GetTypeDeclarationPrefix`, the variant base type, the factory return
types (`Disclosure<T>`), and `GetNestedTypeReference` (`Disclosure<T>.VisibleVariant`, not
`Disclosure.VisibleVariant`). Places that write the name as an *identifier* — the private constructor,
diagnostic and exception message text — stay bare. One helper (`SelfTypeReference(union)`) and its
absence at the constructor is the whole change.

**3. `Match<T>` shadows the union's `T`.** Verified on .NET 10: `TResult Match<T>(…)` inside `Box<T>`
compiles with **CS0693** (a warning, not an error), and the method's `T` silently wins inside the
signature. Rename the result parameter to `TResult`, and if the union already declares `TResult`,
suffix-uniquify (`TResult1`, `TResult2`, …).

**4. Generic containing types.** `ContainingTypeInfo` emits `partial class Outer` — for a union nested
in `Outer<TKey>` that reopens the wrong type and does not compile. `ContainingTypeInfo` needs the
container's type parameter names too.

**5. Sub-union detection.** `GetDirectSubUnions` compares `nestedType.BaseType` to `classSymbol` with
`SymbolEqualityComparer`. Inside `Disclosure<T>` the nested type's base is the *constructed* type
`Disclosure<T>`, which is not the same symbol as the definition. Compare `BaseType.OriginalDefinition`
instead.

Also: `GetSourceHint` must include arity, or `Foo` and `Foo<T>` in one namespace collide on `Foo.g.cs`
and the host drops one. Backticks are not safe in hint names — use `Foo_1.g.cs`.

## JSON is the one genuinely hard part

The converter body itself is fine generic — `JsonSerializer.Deserialize<T>(…)` works with an open `T`.
The problem is *registration*.

**System.Text.Json.** `[JsonConverter(typeof(DisclosureJsonConverter<>))]` does **not** work; verified
on .NET 10 it throws at first serialization:

```
System.ArgumentException: Cannot create an instance of DisclosureJsonConverter`1[T]
because Type.ContainsGenericParameters is true.
```

The fix is a non-generic `JsonConverterFactory` alongside the generic converter — verified working:

```csharp
[System.Text.Json.Serialization.JsonConverter(typeof(DisclosureJsonConverterFactory))]
public abstract partial record Disclosure<T> { … }

public sealed class DisclosureJsonConverterFactory : System.Text.Json.Serialization.JsonConverterFactory
{
	public override bool CanConvert(System.Type t) =>
		t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Disclosure<>);

	public override System.Text.Json.Serialization.JsonConverter CreateConverter(
		System.Type t, System.Text.Json.JsonSerializerOptions options) =>
		(System.Text.Json.Serialization.JsonConverter)System.Activator.CreateInstance(
			typeof(DisclosureJsonConverter<>).MakeGenericType(t.GetGenericArguments()))!;
}

public sealed class DisclosureJsonConverter<T> : JsonConverter<Disclosure<T>> { /* today's body */ }
```

That is ~12 extra emitted lines, and it is where the captured constraint clauses are needed: the
converter is a *separate* generic type, so `where T : notnull` must be repeated on
`DisclosureJsonConverter<T>` and its factory. The union's own generated partial declaration can still
omit them — a partial declaration may omit constraint clauses entirely and take them from the part
that declares them.

**Newtonsoft.** No factory concept. `JsonConverterAttribute` instantiates the named type directly, so
the same open-generic problem applies with no clean escape: the workaround is a non-generic converter
that overrides `CanConvert(Type)` and reflects — `MakeGenericMethod` on the factory method to build a
variant, `serializer.Deserialize(reader, closedParamType)` for payloads. Real work, all of it
reflection, for a legacy serializer.

## Proposed scope

**Phase 1 — generics, no serialization.** Items 1–5 above. For a generic union, emit no JSON converter
and no converter attribute. If the user explicitly set `GenerateJsonConverter = true` or
`GenerateNewtonsoftJsonConverter = true` on a generic union, report a new warning (`ZGOR005`,
"JSON converters are not generated for generic unions") so the omission is never silent. Constraints
need not be captured at all in this phase.

**Phase 2 — System.Text.Json.** Constraint capture, generic converter, factory. Drop `ZGOR005` to
Newtonsoft-only.

**Phase 3 — Newtonsoft, if anyone asks.** The reflective converter. Not speculatively.

Splitting this way means phase 1 is a mechanical change to string building with no new runtime
concepts, and the serializer work — the only part with real design risk — is isolated behind it.

## Tests

- factory, `Match`, `Switch`, and accessors on a single-parameter union (`Disclosure<T>`)
- two parameters (`Result<TValue, TError>`) — argument order preserved everywhere
- a variant whose payload is the type parameter, and one that is `IReadOnlyList<T>`
- constraints round-trip: `where T : notnull`, `where T : class`, `where T : IComparable<T>, new()`
- a generic union nested in a generic containing type
- a sub-union nested in a generic union participates in `Match`
- `Foo` and `Foo<T>` in one namespace both generate (hint collision)
- no `Match<T>` shadowing warning: compile the generated output warning-clean
- phase 2: serialize/deserialize `Disclosure<string>` and `Disclosure<int>` round-trip

## Open questions

**Type inference at call sites.** `Disclosure<string>.Visible("x")` cannot be shortened —
static members on a generic type never infer their type arguments. A companion non-generic class
(`public static class Disclosure { public static Disclosure<T> Visible<T>(T value) => …; }`) would
allow `Disclosure.Visible("x")`, but it claims a type name the user may already be using and doubles
the emitted surface. Recommend leaving it out of the first cut and revisiting if the verbosity bites.

**Variance.** `Disclosure<out T>` is not expressible — variance requires an interface, and the union
is a class. Out of scope; worth one line in the README so it isn't asked twice.
