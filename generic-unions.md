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

**1. Model.** `UnionModel` gains `TypeParameters` (an `EquatableArray<string>` of names) and
`TypeParameterConstraints` (the `where …` clauses as pre-rendered strings). Both must be plain
strings — no `ITypeSymbol` in the model, or incremental caching breaks. Constraints are not optional;
D1 requires them. How the strings are produced is D5.

**2. Self-references in emitted text.** Every place that writes the union's name as a *type* needs the
type argument list appended: `GetTypeDeclarationPrefix`, the variant base type, the factory return
types (`Disclosure<T>`), and `GetNestedTypeReference` (`Disclosure<T>.VisibleVariant`, not
`Disclosure.VisibleVariant`). One helper — `SelfTypeReference(union)` — covers all of them. The
private constructor is the one place the name is an identifier and must stay bare: `private Disclosure()`.
Message text is not covered by either rule; see D12.

**3. `Match<T>` shadows the union's `T`.** See D4.

**4. Generic containing types.** `ContainingTypeInfo` emits `partial class Outer` — for a union nested
in `Outer<TKey>` that reopens the wrong type and does not compile. `ContainingTypeInfo` needs the
container's type parameter names too.

**5. Sub-union detection.** No change required — see D7. The premise this item was written on is false.

Also: `GetSourceHint` must include arity — see D6.

## JSON: registration, not the converter body

The converter body is fine generic — `JsonSerializer.Deserialize<T>(…)` works with an open `T`. The
only obstacle is *registration*: `JsonConverterAttribute` (both serializers) instantiates the named
type via `Activator.CreateInstance`, and an open generic type has no instance.

Everything below was executed, not reasoned about. Failure text and output are quoted verbatim.

**The naive emit fails at runtime.** `[JsonConverter(typeof(DisclosureJsonConverter<>))]` compiles and
then throws on first serialization:

```
System.ArgumentException: Cannot create an instance of Probe.NaiveJsonConverter`1[T]
because Type.ContainsGenericParameters is true.
```

**System.Text.Json — non-generic `JsonConverterFactory` closes the generic.**

```csharp
[System.Text.Json.Serialization.JsonConverter(typeof(DisclosureJsonConverterFactory))]
public abstract partial record Disclosure<T> { … }

public sealed class DisclosureJsonConverterFactory : System.Text.Json.Serialization.JsonConverterFactory
{
	public override bool CanConvert(System.Type t) => ClosedUnion(t) is not null;

	public override System.Text.Json.Serialization.JsonConverter CreateConverter(
		System.Type t, System.Text.Json.JsonSerializerOptions options) =>
		(System.Text.Json.Serialization.JsonConverter)System.Activator.CreateInstance(
			typeof(DisclosureJsonConverter<>).MakeGenericType(ClosedUnion(t)!.GetGenericArguments()))!;

	// Walks to the closed union, so a variant type resolves to its union's arguments. See D2.
	private static System.Type? ClosedUnion(System.Type? t)
	{
		for (; t is not null; t = t.BaseType)
			if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Disclosure<>)) return t;
		return null;
	}
}

public class DisclosureJsonConverter<T> : JsonConverter<Disclosure<T>> where T : notnull
{
	public override bool CanConvert(System.Type t) => typeof(Disclosure<T>).IsAssignableFrom(t); // D2
	/* today's body */
}
```

Observed round-trips: `{"$type":"Visible","value":"hello"}` for `Disclosure<string>`,
`{"$type":"Visible","value":42}` for `Disclosure<int>`, `{"$type":"NotProvided"}` for the
payload-free variant. STJ caches the factory's output per (type, options), so no cache of our own.

**Newtonsoft — a non-generic shim that delegates to the same generic converter.** No reflection over
variant names or payload types is required. `JsonConverter<T>` exposes the non-generic
`ReadJson`/`WriteJson` as public sealed overrides, so the shim forwards through the base:

```csharp
public sealed class DisclosureNewtonsoftJsonConverter : Newtonsoft.Json.JsonConverter
{
	private static readonly ConcurrentDictionary<System.Type, Newtonsoft.Json.JsonConverter> Cache = new();

	public override bool CanConvert(System.Type t) =>
		t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Disclosure<>);

	public override object? ReadJson(JsonReader reader, System.Type objectType, object? existingValue, JsonSerializer serializer) =>
		Inner(objectType).ReadJson(reader, objectType, existingValue, serializer);

	public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
	{
		if (value is null) { writer.WriteNull(); return; }
		Inner(value.GetType()).WriteJson(writer, value, serializer);
	}

	private static Newtonsoft.Json.JsonConverter Inner(System.Type t) =>
		Cache.GetOrAdd(t, static key => (Newtonsoft.Json.JsonConverter)System.Activator.CreateInstance(
			typeof(DisclosureNewtonsoftJsonConverter<>).MakeGenericType(key.GetGenericArguments()))!);
}
```

Three measured facts make it work:

1. A type nested in a generic type is itself generic and carries the enclosing arguments.
   `typeof(Disclosure<string>.VisibleVariant).GetGenericArguments()` returns `[String]`. So the same
   helper closes the read path (declared type) and the write path (runtime variant type).
2. Delegation goes through the non-generic base, so the generic converter body is the emitted text we
   already produce with `<T>` threaded in — one implementation, not two.
3. The cache is load-bearing on Newtonsoft and not on STJ. Newtonsoft caches the *shim instance* on
   the contract but re-enters `ReadJson` per value; without the cache every deserialized value pays a
   `MakeGenericType` + `Activator.CreateInstance`.

Observed round-trips: identical JSON to STJ for `Disclosure<string>` and `Disclosure<int>`.

**The shim's `CanConvert` walks base types, matching the STJ factory's `ClosedUnion`.** Tested directly
against the type it would misjudge: `Disclosure<string>.VisibleVariant`'s generic type definition is
`Disclosure<>.VisibleVariant`, not `Disclosure<>`, so a direct `GetGenericTypeDefinition()` comparison
returns false for every variant. The measured write path still succeeded only because Newtonsoft skips
`CanConvert` entirely when the converter arrives via `[JsonConverter]` on the type. It is consulted when
a user registers manually — `settings.Converters.Add(new DisclosureNewtonsoftJsonConverter())` — and
there the direct comparison silently declines every variant, dropping the union back to default
property serialisation with no discriminator. Same failure D2 fixes on the System.Text.Json side, so
both entry points use the same base-walking test.

**Constraints must be captured.** The converters are *separate* generic types, so `where T : notnull`
has to be repeated on `DisclosureJsonConverter<T>`, `DisclosureNewtonsoftJsonConverter<T>`, and on any
hoisted converter's container parameters. The union's own generated partial may still omit them —
confirmed by compiling a generated part with no `where` clause against a user part that declares one.

### D1 — Generic unions ship with working converters for both serializers.

**Decided.** There is no release in which `Disclosure<T>` generates a union that System.Text.Json or
Newtonsoft cannot round-trip.

Rationale is mechanical, not aspirational: both mechanisms above were built and run, and each costs
one small non-generic class (~12 lines STJ, ~14 Newtonsoft) plus threading `<T>` through the existing
converter emitter. There is no design risk left to isolate behind a phase boundary, so a phase
boundary buys nothing and costs a diagnostic that would be introduced and removed one release apart.

The default resolution path is `GenerateJsonConverter ?? HasSystemTextJson`
(`DiscriminatedUnionGenerator.cs:262`), so the common declaration leaves the flag `null` and expects a
converter. Emitting nothing there would be silent — an abstract record with no public properties
serializes to `{}` rather than throwing.

**Rules out:**
- A "JSON converters are not generated for generic unions" diagnostic. None is added. (`ZGOR005` is
  later spent on D8.)
- Any ordering that ships generics ahead of the converters.
- Treating constraint capture as optional. It is required by the converters.
- Newtonsoft-by-reflection over variant names and payload types.

Sequencing constraint (a property of the system, not a work breakdown): the converter emitters read
the type-parameter and constraint fields of `UnionModel`, so those fields exist before any converter
text can reference them.

## The discriminator is lost when a union is written by its runtime type

Found while checking generic unions against a real ASP.NET Core app. It is **not** a generics
problem — it reproduces on today's non-generic output, unchanged.

A minimal API returning a union directly:

```csharp
app.MapGet("/plain",   () => Plain.Visible("hello"));               // non-generic union, today's emitter
app.MapGet("/generic", () => Disclosure<string>.Visible("hello"));  // generic union
```

Observed responses **before** the fix:

```
plain          {"value":"hello"}      <- no discriminator
generic        {"value":"hello"}      <- no discriminator
generic-none   {}                     <- payload-free variant, indistinguishable from anything
```

ASP.NET serialises the response by the value's **runtime** type, which is `VisibleVariant`, not
`Plain`. System.Text.Json resolves `[JsonConverter]` on the type being written and does not pick up
the attribute the union declares, so the converter never runs and the raw properties are written.
The payload survives; the discriminator does not, so nothing can read the response back.

`Results.Json<Plain>(…)` does not avoid it — the same output. Only a union reached through a
*declared* member type behaves correctly, which is why a union sitting inside a DTO always worked:

```
dto            {"name":"Ada","email":{"$type":"Visible","value":"ada@x.com"},"age":{"$type":"NotProvided"}}
```

### D2 — The converter attribute goes on the variant types too, and converters accept subtypes.

**Decided.** Two additions to the emitted output, together:

1. Emit the System.Text.Json converter attribute on **each generated variant class**, not only on the
   union.
2. Override `CanConvert` on the converter so it claims the whole hierarchy:

```csharp
public override bool CanConvert(System.Type t) => typeof(Plain).IsAssignableFrom(t);
```

Both are required. Doing only (1) throws at the first write:

```
System.InvalidOperationException: The converter specified on 'Web.Plain+VisibleVariant'
is not compatible with the type 'Web.Plain+VisibleVariant'.
```

`JsonConverter<TBase>` refuses a derived `typeToConvert` until `CanConvert` says otherwise. For the
generic union the same claim is made in two places: `DisclosureJsonConverter<T>.CanConvert` as above,
and the factory's `CanConvert`/`CreateConverter`, which walk base types to the closed union so a
variant type resolves to its union's type arguments (the `ClosedUnion` helper shown earlier).

Verified after the change — generic and non-generic emit identical JSON on every route:

```
plain          {"$type":"Visible","value":"hello"}
generic        {"$type":"Visible","value":"hello"}
generic-int    {"$type":"Visible","value":42}
generic-none   {"$type":"NotProvided"}
plain-typed    {"$type":"Visible","value":"hello"}
generic-typed  {"$type":"Visible","value":"hello"}
dto            {"name":"Ada","email":{"$type":"Visible","value":"ada@x.com"},"age":{"$type":"NotProvided"}}
POST generic   {"$type":"Visible","value":"hi"}     (request body parsed by the converter)
POST plain     {"$type":"NotProvided"}
```

**Newtonsoft needs neither addition.** It honours the attribute inherited from the base type, so
writing a value whose static type is the variant already produced `{"$type":"Visible","value":"hello"}`.
Emitting the Newtonsoft attribute on variants would be actively harmful: the shim would resolve
`objectType = VisibleVariant` and hand back a `Disclosure<T>` where the variant type is expected. Only
the System.Text.Json attribute is duplicated onto variants.

**Rules out:**
- Fixing this with `[JsonDerivedType]`. That is STJ 7+ only and would change the wire format.
- Treating it as an ASP.NET-specific concern. Any `JsonSerializer.Serialize(value)` where the static
  type is not the union hits it.
- Scoping it to generic unions. The emitter change applies to every union; a non-generic union is
  where it is most likely to be sitting broken in someone's project right now.

**Affected files:**
- `DiscriminatedUnionGenerator.cs` — `GenerateVariantClasses` must emit the STJ converter attribute
  above each variant declaration; `GenerateJsonConverterClass` must emit the `CanConvert` override.
  The attribute currently appears once, at lines 339–347, before the union declaration only.
- `JsonConverterTests.cs` — needs a case that serialises through the variant's static type, and one
  that serialises a union returned as `object`. Both pass today for the wrong reason: every existing
  test goes through the union's declared type.
- `README.md` — the JSON section documents converter behaviour and does not mention this.

## Converter placement

A converter emitted inside a generic containing type cannot be named in an attribute:

```
Probe3.cs(10,55): error CS0416: 'Probe3.OuterA<TOuter>.LeafJsonConverter':
    an attribute argument cannot use type parameters
```

`OuterA<TOuter>` is generic; `Leaf` is an ordinary non-generic union. `GenerateSource` emits the
converter at the union's own indent level (`DiscriminatedUnionGenerator.cs:369-379`), i.e. inside every
containing type, so `typeof(LeafJsonConverter)` resolves to `OuterA<TOuter>.LeafJsonConverter` and
carries a type parameter. This is broken on today's output, before generics exist. `ZGOR002` does not
mention it, because the containing type *is* partial — the placement is what's wrong.

### D3 — A converter is emitted at the innermost enclosing scope that has no type parameters.

**Decided.** One rule, applied to every union:

- No containing type is generic → that scope is the union's own, so the position and the converter's
  name are **exactly what is emitted today**. Existing output is byte-identical.
- Some containing type is generic → the converter is emitted outside the *outermost* generic
  container (at namespace level if that container is outermost). It absorbs the type parameters of
  every container it skipped, outermost first, followed by the union's own, and repeats their
  constraint clauses. Its name is prefixed with the skipped containers' names.

For `Outer<TOuter>.Leaf<T>`, verified round-tripping under both serializers:

```csharp
public partial class Outer<TOuter> where TOuter : notnull
{
	[JsonConverter(typeof(Outer_LeafJsonConverterFactory))]
	public abstract partial record Leaf<T> where T : notnull { … }
}

public class Outer_LeafJsonConverter<TOuter, T> : JsonConverter<Outer<TOuter>.Leaf<T>>
	where TOuter : notnull
	where T : notnull { … }
```

Two runtime facts this depends on, both measured:
`typeof(Outer<int>.Leaf<string>).GetGenericArguments()` returns `[Int32, String]` — containers first,
then own, which is the order `MakeGenericType` expects — and `typeof(Outer<>.Leaf<>)` is legal unbound
syntax (`Probe4.Outer\`1+Leaf\`1`), so the factory's type test needs no special casing.

**Rules out:**
- Hoisting every converter to namespace level. It would rename the converter type for every nested
  union that works today and break `settings.Converters.Add(new Outer.LeafJsonConverter())`.
- Diagnosing the shape as unsupported. C# allows it; only the emitted placement was ever wrong.
- Any converter name change for a union whose containers are all non-generic.

Nobody's converter name changes: the only shape whose name moves is the one that does not compile
today, so it has no users. Residual collision risk is a union literally named `Outer_Leaf` sitting in
the same scope as the hoisted `Outer<T>.Leaf` — noted, not defended against.

**Affected files:**
- `DiscriminatedUnionGenerator.cs` — `GenerateSource` currently emits converters (lines 369–379)
  *before* the containing-type close loop (lines 381–385). Those two must interleave: close containers
  down to the target scope, emit the converters there, close the rest.
- `ContainingTypeInfo` (line 880) — needs the container's type parameter names and constraint clauses,
  both as plain strings. It is already the record that must gain type parameters for
  `EmitContainingTypeOpen` (line 401) to reopen `partial class Outer<TKey>` correctly; D3 adds the
  constraints to the same record.
- `GetSourceHint` is unaffected — the hint is per union, not per converter.

## Match's result type parameter

Line 453 emits `public T Match<T>(`. Inside a generic union that name already belongs to the union:

```
Probe5.cs(13,24): warning CS0693: Type parameter 'T' has the same name as the type
    parameter from outer type 'Box<T>'
```

A warning, not an error, so it compiles and the method's `T` silently wins throughout the signature —
`Func<OfVariant, T>` reads as the result type, not the payload type. It behaves correctly and
documents itself wrongly, and any consumer building with `TreatWarningsAsErrors` fails outright.

### D4 — The result parameter is named `TResult` in all generated output, generic or not.

**Decided.** One emitted shape regardless of whether the union has type parameters.

Method type parameter names are not part of the API: call sites pass type arguments positionally
(`x.Match<string>(…)`), so no consumer recompiles differently and nothing changes at runtime. The name
is visible only in IntelliSense and in the generated file.

If `TResult` is already taken, suffix-uniquify (`TResult1`, `TResult2`, …) — verified to compile
warning-clean.

The taken-name check covers **every type parameter in scope**, not only the union's own. A union
nested in `Outer<TResult>` inherits that name and `Match<TResult>` inside it shadows the container's
parameter with the same CS0693. The candidate set is the containers' parameters ∪ the union's own —
the list `ContainingTypeInfo` already has to carry for D3.

**Rules out:**
- Emitting `T` for non-generic unions and `TResult` for generic ones. Two shapes of generated code,
  differing on a property of the declaration the reader is not thinking about.
- Uniquifying against the union's own type parameters only.

**Affected files:**
- `DiscriminatedUnionGenerator.cs` — `GenerateMatchMethod` (line 446). `GenerateSwitchMethod` is
  unaffected; `Switch` has no type parameter.
- `HierarchicalUnionTests.cs:14` — asserts the literal `"public T Match<T>("`. Becomes
  `"public TResult Match<TResult>("`. Only place in the repo that pins the text.
- `README.md` — shows call sites only, never the signature. No change.

## Capturing constraint clauses

The generated file's using block is one line (line 322): `using System;`. A user file has its own:

```csharp
using System.Collections.Generic;
namespace App;

[DiscriminatedUnion]
public abstract partial record Cache<T> where T : IEqualityComparer<T>, new() { … }
```

### D5 — Constraints are rendered from `ITypeParameterSymbol`, constraint types via `ToDisplayString()`.

**Decided.** The captured string for the example above is
`where T : System.Collections.Generic.IEqualityComparer<T>, new()` — fully qualified, so it resolves
in the generated file regardless of the user's usings, and regardless of the scope a D3-hoisted
converter lands in.

This is how the model already treats types: `VariantParameter.Type` is `parameter.Type.ToDisplayString()`
(line 111). Constraint types are captured by the same rule, so there is one convention in the model,
not two.

Rendering order is fixed by the language and must be produced in it: primary constraint first
(`class` / `class?` / `struct` / `unmanaged` / `notnull` / base type), then interfaces, then `new()`
last. Read off `HasReferenceTypeConstraint`, `ReferenceTypeConstraintNullableAnnotation`,
`HasValueTypeConstraint`, `HasUnmanagedTypeConstraint`, `HasNotNullConstraint`, `ConstraintTypes`,
`HasConstructorConstraint`.

Constraints that reference type parameters (`where T : IComparable<T>`) survive because no type
parameter is ever renamed: D4 renames only `Match`'s own result parameter, and a D3-hoisted converter
keeps the container parameter names it absorbed.

**Rules out:**
- Copying the clause text from the declaration syntax. It emits unqualified names into a file with
  only `using System;` (CS0246), breaks on `using` aliases, and has no single source when the union is
  declared across several partial declarations.
- Omitting constraints anywhere they are required. The union's own generated partial may still omit
  them — a partial declaration inherits them from the part that declares them, confirmed by compiling
  exactly that — but the converters may not.

**Failure mode this creates:** a wrong rendering order leaves the *union* compiling and the
*converter* not. The constraint tests already listed are what catch it, and they must assert on the
converter's clause, not only the union's.

## Generated file names

`GetSourceHint` (line 295) builds the hint from namespace + container path + class name, with no arity:

```csharp
segments.AddRange(union.ContainingTypes.Select(static type => type.Name));
segments.Add(union.ClassName);
return $"{string.Join(".", segments)}.g.cs";
```

`Foo` and `Foo<T>` in one namespace both produce `Ns.Foo.g.cs`. The host drops one and the project
fails with CS8795 on the surviving partial's unimplemented members — the failure `SourceHintCollisionTests`
exists to prevent. Containers are named the same way, so `Outer.Leaf` and `Outer<T>.Leaf` collide too.

### D6 — Arity is appended to any path segment whose arity is greater than zero.

**Decided.** `Ns.Foo.g.cs` for `Foo`, `Ns.Foo_1.g.cs` for `Foo<T>`, `Ns.Outer_1.Leaf.g.cs` for
`Outer<T>.Leaf`. Underscore, not the CLR's backtick, which is not safe in a hint name.

Every arity-0 hint is byte-identical to today, so no existing generated file is renamed and no
project's `EmitCompilerGeneratedFiles` output churns.

**Rules out:**
- Appending arity unconditionally (`Ns.Foo_0.g.cs`). Uniform and marginally simpler to emit, but
  renames every generated file in every existing project for no behavioural gain.
- Arity on the union alone. Container arity collides identically.

**Affected files:**
- `DiscriminatedUnionGenerator.cs` — `GetSourceHint` (line 295); `ContainingTypeInfo` must expose
  arity, which follows from the type parameter names it already gains for D3.
- `SourceHintCollisionTests.cs` — the three existing assertions
  (`…SectorEvolver.g.cs`, `…Sector.Evolver.g.cs`, `Assert.Equal(2, …)`) are unaffected and must stay
  unmodified; a change to any of them means the arity-0 path was altered.

## Sub-union membership

`GetDirectSubUnions` (line 164) collects nested types carrying `[DiscriminatedUnion]` whose base type
is the union. Measured against Roslyn 4.8, the existing comparison already works for generic unions:

```
== generic union, nested sub-union ==
  classSymbol            : App.Outcome<T>
  nested.BaseType        : App.Outcome<T>
  Equals(BaseType, cls)  : True
```

Roslyn returns the definition symbol when a type is constructed with its own type parameters, so
`Outcome<T>` written inside `Outcome<T>` *is* `classSymbol`. Same result for
`Outer<TOuter>.Leaf<T>.Deep`. The claim that these are different symbols is false.

The comparison is also load-bearing in a way `OriginalDefinition` would destroy. A nested type may
derive from a *different construction* of the enclosing union — legal C#, and the shape a user
produces by typing `<int>` where they meant `<T>`:

```
  weird.BaseType                 : App.Outcome<int>
  Equals(BaseType, cls)          : False   <- today, correctly excluded
  Equals(BaseType.OrigDef, cls)  : True    <- would be wrongly included
```

Classified as a sub-union, `Weird` would put `Func<Weird, TResult>` and a `case Weird` arm inside
`Outcome<T>`'s `Match`, where `Weird` derives from `Outcome<int>` and not from `Outcome<T>`. The
generated code stops compiling for every generic union that has one.

### D7 — Membership is declared by the base clause. The generator does not infer it from nesting.

**Decided.** A nested union is part of its parent when, and only when, it says so:
`public abstract partial record Rejected : ContractOutcome`. `GetDirectSubUnions` is unchanged.

The alternative — emit the base clause automatically for any `[DiscriminatedUnion]` nested inside a
`[DiscriminatedUnion]`, making the clause optional — is mechanically sound. It was measured: a
generated part may supply a base clause the user's part omits, both parts may repeat the same base
clause with no error, and a disagreement raises CS0263 on the user's own line. It is rejected on
modelling grounds, not feasibility.

Nesting has two distinct meanings, and only the base clause tells them apart:

- **Sub-union** — *is a*. `Rejected` is a `ContractOutcome`. Belongs in the parent's `Match`. Exists so
  a caller can dispatch at the granularity it cares about, and so adding a case breaks only the
  callers that were looking at that level.
- **Payload union** — *has a*. `SharedPersonalDetails` is data inside an `AthleteProfileView`, not a
  kind of one. Nested for the same reason a helper enum is nested: to scope the name to its owner.

Inferring membership from location collapses the two: a payload union nested for scoping silently
becomes a case of its parent, and values of it start type-checking wherever the parent is expected.
That capability is not recoverable by the user — the only workaround is to stop nesting.

**Invariant:** a sub-union derives from its **immediate** enclosing union, never a further ancestor.
`Security : Rejected`, not `Security : ContractOutcome`. This already holds — `GetDirectSubUnions`
reads `classSymbol.GetTypeMembers()`, which is direct members only, never transitive — and it is what
permits several branches at one layer, each with its own children.

### D8 — `ZGOR005` reports a nested union that derives from a different construction of its parent.

**Decided.** Severity **Warning**, consistent with `ZGOR002`, which likewise reports generated code
that will surprise the user rather than a malformed declaration.

Fires when a nested type carries `[DiscriminatedUnion]` and
`BaseType.OriginalDefinition` equals the enclosing union's definition while `BaseType` does not equal
it. That condition is only reachable by naming the enclosing union with the wrong type arguments, so
it has no legitimate use.

Message names both types, because the two differ only in their arguments:
*"'Rejected' derives from 'Outcome&lt;int&gt;' but is nested in 'Outcome&lt;T&gt;'; it will not
participate in Match."*

Without it, the mistake is silent: the nested union still generates, its parent's `Match` simply never
mentions it, and the omission surfaces at runtime through the `_ => throw new InvalidOperationException`
arm.

**Rules out:**
- Comparing with `BaseType.OriginalDefinition`. It misclassifies exactly the case this diagnostic
  reports, and turns a silent omission into generated code that does not compile.
- Inferring membership from nesting (see D7).
- Diagnosing a nested union that derives from some *unrelated* union. That is a legitimate sub-union of
  the other type and is not this rule's business.

**Affected files:**
- `DiscriminatedUnionGenerator.cs` — new descriptor beside `NonAbstractUnionDescriptor` (line 39); the
  detection sits in the `GetDirectSubUnions` filter (lines 168–171), which currently discards the
  non-matching case with no record of why.
- `AnalyzerReleases.Unshipped.md` — add the `ZGOR005` row. Omitting it is an RS2008 build failure, not
  a documentation lapse.
- Tests — a nested union with a different construction reports `ZGOR005` and is absent from the
  parent's `Match`; the correctly-constructed sibling is still collected.

## Call-site verbosity

### D9 — No companion static class is emitted. Call sites name the closed type.

**Decided.** `Disclosure<string>.Visible("x")` stays as it is.

A companion non-generic class would shorten some call sites:

```csharp
public static class Disclosure
{
	public static Disclosure<T> Visible<T>(T value) => Disclosure<T>.Visible(value);
	public static Disclosure<T> NotProvided<T>() => Disclosure<T>.NotProvided();
}
```

```csharp
Disclosure.Visible("x")              // T inferred from the argument
Disclosure.NotProvided<string>()     // nothing to infer from — no shorter than Disclosure<string>.NotProvided()
```

C# infers method type arguments from arguments only, never from the return type. The shorthand
therefore reaches exactly those variants whose parameters mention every type parameter and no others,
giving a union whose factories are half short and half long with no rule visible at the call site.

It fails hardest on the case that motivates generics in the first place. `Result<TValue, TError>` —
named in *Why* above as the shape users most want — gets nothing: `Result.Ok<TValue, TError>(value)`
cannot infer `TError` from the argument list, so every call stays fully explicit.

Name collision is secondary but real: `Disclosure` and `Disclosure<T>` coexist by arity, and D6 keeps
their generated files apart, but a user who already has a non-generic `Disclosure` union receives
CS0101 from a type they never declared.

**Rules out:**
- Emitting the companion unconditionally. Doubles the emitted factory surface for a benefit that lands
  unevenly across variants and not at all on multi-parameter unions.
- Emitting it only for unions where every variant mentions every type parameter. That buys a uniform
  API at the price of a shorthand whose availability depends on a property of the whole union that is
  invisible where it is used.

Revisit only if real usage shows the verbosity biting.

## Native AOT and trimming

Building the verified converters with `<PublishAot>true</PublishAot>` produces one new warning class,
twice — once per serializer, both on lines D1 introduces:

```
Program.cs(118,17): warning IL3050: Using member 'System.Type.MakeGenericType(params Type[])'
    which has 'RequiresDynamicCodeAttribute' can break functionality when AOT compiling.
    The native code for this instantiation might not be available at runtime.
```

Line 118 is the System.Text.Json factory's `CreateConverter`; line 190 is `Inner` in the Newtonsoft
shim. Today's non-generic converter contains no reflection and emits neither.

Distinct from this, and *not* introduced here: `IL2026`/`IL3050` on `JsonSerializer.Serialize<TValue>`.
Those fire at the caller's own call sites and fire identically for a non-generic union — reflection-based
System.Text.Json was never AOT-clean.

### D10 — The reflection is suppressed at the emission site and the constraint is documented.

**Decided.** Emit `#pragma warning disable IL3050` / `restore` around the two reflection lines, with a
comment naming the reason, plus one README line: a generic union's converter is constructed at
runtime, so under Native AOT the closed union types must be rooted.

Verified: the pragma removes both warnings and raises no `CS1691` for an unrecognised warning id.

The reflection itself cannot be removed. The set of closed types (`Disclosure<string>`,
`Disclosure<int>`, …) is not knowable when the source is generated — which is the reason
`JsonConverterFactory` exists at all. The residual runtime risk is bounded and specific: under AOT
`DisclosureJsonConverter<string>` may not be rooted, giving a missing-instantiation failure rather
than a wrong result.

The warning is suppressed rather than surfaced because it lands in a `.g.cs` file. A consumer with
`TreatWarningsAsErrors` and one generic union gets a hard build failure at a location they cannot
edit, and no action available to them resolves it.

**Rules out:**
- Leaving the warnings in place. It reports a constraint at a location the user has no power over.
- `[UnconditionalSuppressMessage("AOT", "IL3050")]` on the emitted member. More precise, but the
  attribute requires `System.Diagnostics.CodeAnalysis` on the *consumer's* target framework, which a
  `netstandard2.0` generator cannot assume.
- Enumerating closed constructions at generation time. They are not knowable.

**Affected files:**
- `DiscriminatedUnionGenerator.cs` — the factory and shim emitters.
- `README.md` — the Native AOT note.

## How a union names itself in messages

D6 makes this consequential: `Foo` and `Foo<T>` may now coexist in one namespace and both generate.

Compile-time diagnostics pass `union.ClassName` (line 256), so a generic union reports as
`Discriminated union 'Disclosure' must be declared abstract` — naming neither type when both exist.
`ZGOR005` is worse off, since distinguishing `Outcome<int>` from `Outcome<T>` is the entire content of
the diagnostic.

Runtime text is the opposite case. Line 517 emits into the generated file:

```csharp
throw new InvalidOperationException($"Cannot access this Disclosure as 'VisibleVariant' because it is '{GetType().Name}'.");
```

Writing `Disclosure<T>` there prints a literal `T`, which is false — the object is a `Disclosure<string>`.

### D11 — Display form in compile-time diagnostics; bare name in runtime message text.

**Decided.** Diagnostics name the union as the user wrote it: `Disclosure<T>`, `Outcome<int>`. Message
strings emitted into generated code keep the bare name.

The split follows what is knowable where. At diagnostic time the declaration is in hand and the type
arguments are exactly what disambiguates. At runtime the closed type is not knowable when the string is
generated, and `{GetType().Name}` already reports the actual type of the value.

**Rules out:**
- Bare names everywhere, as originally stated in item 2 above. `ZGOR003` stays ambiguous whenever both
  arities exist, and `ZGOR005` cannot express its own subject.
- Display form everywhere. Runtime messages would assert a type argument that is not the one in play.

**Affected files:**
- `DiscriminatedUnionGenerator.cs` — the diagnostic call sites (lines 251–256, 287–291) and the new
  `ZGOR005` from D8. `GenerateAccessors` (line 517) and `EmitFailureMessage` (line 787) are unchanged.

## Variance

Variance is the rule for when `Foo<Derived>` may be used where `Foo<Base>` is expected. `IEnumerable<out T>`
permits `IEnumerable<string>` → `IEnumerable<object>` because T only ever comes *out*; `List<T>` forbids
it because `Add(T)` would let an `int` into a `List<string>`.

The annotation is unavailable on a union, and not as a matter of preference:

```
error CS1960: Invalid variance modifier.
    Only interface and delegate type parameters can be specified as variant.
```

The restriction to interfaces and delegates exists because classes have fields, and a field is read
*and* write. A covariant class field would let `Box<object> o = box_of_string; o.Value = 42;` put an
`int` where a `string` is required. Gorilla's unions must be classes or records — variants derive from
them, and the private constructor is what closes the hierarchy — so the annotation is out of reach by
construction.

### D12 — No variance support. Nothing is emitted for it.

**Decided.** `Disclosure<string>` does not convert to `Disclosure<object>`, and the generator emits no
interface, no map, and no conversion to make it.

A covariant interface emitted beside the union does compile and does widen —
`IDisclosure<object> w = Disclosure<string>.Visible("hello")` — but it was measured to be worth less
than it looks:

- It cannot carry the union's `Match`. `Disclosure<T>.VisibleVariant` is invariant in `T`, so
  `TResult Match<TResult>(Func<Disclosure<T>.VisibleVariant, TResult>)` on a covariant interface is
  `error CS1961`. Handlers could receive only the bare payload, so a variant such as
  `Visible(T value, string reason)` loses `reason`, and the union ends up with two `Match` methods of
  differing capability.
- It does not apply to value types. `IDisclosure<object> b = Disclosure<int>.Visible(42)` is
  `error CS0266` — variance rides on reference conversions, and boxing is not one.
- Most "works for any payload" needs are already met without it. `static string Describe<T>(Disclosure<T> d)`
  compiles today and infers `T` at the call site. Variance earns its keep only when mixed payloads must
  share one collection or field.

Nothing is blocked by this. The union is `partial`, so a user with a genuine need declares the
interface and adds `: IDisclosure<T>` on their own part; partial declarations combine interface lists.

**Rules out:**
- Emitting a covariant interface for unions whose variants have at most one parameter, that parameter
  being exactly `T`. Same defect as D9's rejected option — availability governed by a property of the
  whole union that is invisible at the point of use.
- Emitting `Select<TTarget>(Func<T, TTarget>)`. Type-safe and value-type-friendly, but it must
  reconstruct every variant and stops being mechanical as soon as `T` appears nested
  (`IReadOnlyList<T>`, `Dictionary<string, List<T>>`).

**Affected files:**
- `README.md` — record CS1960 and the value-type limit, so the question is answered once.

## Tests

Generics:

- factory, `Match`, `Switch`, and accessors on a single-parameter union (`Disclosure<T>`)
- two parameters (`Result<TValue, TError>`) — argument order preserved everywhere
- a variant whose payload is the type parameter, and one that is `IReadOnlyList<T>`
- constraints round-trip on the **converter**, not only the union: `where T : notnull`,
  `where T : class`, `where T : IComparable<T>, new()`. A wrong rendering order under D5 leaves the
  union compiling and the converter not, so asserting on the union alone proves nothing.
- `Foo` and `Foo<T>` in one namespace both generate (D6); the three existing hint assertions in
  `SourceHintCollisionTests` stay byte-identical
- generated output compiles warning-clean — catches a `Match<T>` regression against D4 (CS0693)

Nesting:

- a generic union nested in a generic containing type compiles and round-trips both serializers.
  This is the CS0416 regression from D3 and fails on today's emitter with a *non-generic* union too.
- a sub-union nested in a generic union participates in `Match`
- a nested union deriving from a different construction reports `ZGOR005` and is absent from the
  parent's `Match`; a correctly-constructed sibling is still collected (D8)
- several sub-unions at one layer, each with their own children — the immediate-parent invariant

JSON:

- serialize/deserialize `Disclosure<string>` and `Disclosure<int>`, both serializers
- D2 regression, **non-generic** union: serialize through the variant's static type and through
  `object`; both must carry the discriminator. Every existing JSON test goes through the union's
  declared type, so none of them would have caught this.
- D2 regression, generic union: the same two shapes through the factory
- Newtonsoft converter registered manually via `settings.Converters.Add(…)` rather than the attribute,
  serialising a variant — exercises the base-walking `CanConvert` that the attribute path never reaches
- a generic union as a property of an enclosing DTO round-trips — the path that already worked, kept
  working

Diagnostics and output hygiene:

- `ZGOR003` on a generic union names it `Disclosure<T>`, not `Disclosure` (D11)
- the emitted reflection sites carry the `IL3050` pragma (D10)
