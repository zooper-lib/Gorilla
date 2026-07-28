## Why

`Option<T>`, `Result<TValue, TError>`, `Disclosure<T>`, `Paged<T>` — the payload-carrying wrapper is the most common union shape, and today Gorilla can only generate it once per concrete payload type. Every other feature (exhaustive `Match`, accessors, JSON) is generic-safe in principle; only the emitter's string building assumes a bare type name.

Investigating this surfaced a defect that is **not** generics-specific and reproduces on today's output: a union written by its *runtime* type (an ASP.NET minimal API returning `Plain.Visible("hello")`, or any `JsonSerializer.Serialize(value)` where the static type is the variant or `object`) loses its discriminator entirely — `{"value":"hello"}` — so nothing can read the response back. A second one: a converter emitted inside a generic containing type does not compile (CS0416).

## What Changes

- A `[DiscriminatedUnion]` type may declare type parameters. `UnionModel` carries the parameter names and pre-rendered constraint clauses (fully qualified, rendered from `ITypeParameterSymbol`); every emitted self-reference gains the type argument list.
- Generic unions ship with working System.Text.Json and Newtonsoft converters in the same release — a non-generic `JsonConverterFactory` (STJ) and a non-generic delegating shim (Newtonsoft) close the open generic at runtime. No "not supported for generic unions" diagnostic is introduced.
- **BREAKING (source-visible, not binary):** `Match`'s result type parameter is renamed `T` → `TResult` in **all** generated output, generic or not. Method type parameter names are not part of the API — call sites pass arguments positionally — but the name is visible in IntelliSense and pinned by one existing test.
- The System.Text.Json converter attribute is emitted on **each generated variant class** in addition to the union, and converters override `CanConvert` to claim the whole hierarchy. Fixes the lost discriminator for every union, generic or not. Newtonsoft gets neither addition (it inherits the attribute; duplicating it would be actively harmful).
- Converters are emitted at the innermost enclosing scope that has no type parameters, absorbing skipped containers' type parameters and constraints and prefixing their names. When no container is generic — every union that compiles today — the output is byte-identical.
- Generated source hints append arity to any path segment with arity > 0 (`Ns.Foo_1.g.cs`, `Ns.Outer_1.Leaf.g.cs`), so `Foo` and `Foo<T>` no longer collide. Arity-0 hints are byte-identical to today.
- New warning **`ZGOR005`**: a nested union that derives from a *different construction* of its parent (`Rejected : Outcome<int>` nested in `Outcome<T>`) is silently excluded from `Match` today. Sub-union membership stays declared by the base clause — it is not inferred from nesting.
- Compile-time diagnostics name a union in display form (`Disclosure<T>`, `Outcome<int>`); runtime message text emitted into generated code keeps the bare name.
- The two runtime-reflection sites carry `#pragma warning disable IL3050` with a documented Native AOT constraint.
- Explicitly **not** built: a companion non-generic factory class (D9 — C# cannot infer from return type, so it lands unevenly and not at all on `Result<TValue, TError>`), and variance support (D12 — CS1960 forbids it on classes/records by construction; documented in README).

## Capabilities

### New Capabilities

- `generic-union-declaration`: type parameters and constraint clauses on `[DiscriminatedUnion]` types — capture into the model, self-references in emitted text, generic containing types, factories / `Match` / `Switch` / accessors under type parameters, arity in source hints, and the decisions to omit a companion factory class and variance support.
- `union-json-converter-registration`: how a generated converter is registered and resolved — the STJ factory and Newtonsoft shim that close an open generic, the converter attribute on variant types, the subtype-claiming `CanConvert` on both entry points, converter placement relative to generic containers, constraint repetition on converter type parameters, and the Native AOT suppression.

### Modified Capabilities

- `nested-union-generation`: `Match`'s result type parameter becomes `TResult` (spec currently states `Match<T>(...)`); source hint uniqueness gains arity; converter emission scope moves outside generic containers; sub-union collection gains the `ZGOR005` warning for a mismatched base construction; diagnostics name the union in display form.

## Impact

- `DiscriminatedUnionGenerator.cs` — `UnionModel`, `ContainingTypeInfo`, `GetSourceHint`, `GetTypeDeclarationPrefix`, `EmitContainingTypeOpen`, `GenerateVariantClasses`, `GenerateMatchMethod`, `GenerateSource` (converter emission must interleave with the containing-type close loop), `GenerateJsonConverterClass`, the diagnostic call sites, and a new descriptor for `ZGOR005`.
- `AnalyzerReleases.Unshipped.md` — the `ZGOR005` row. Omitting it is an RS2008 build failure.
- Tests — `HierarchicalUnionTests.cs:14` pins `"public T Match<T>("` and must become `TResult`. `SourceHintCollisionTests.cs`'s three existing assertions must stay byte-identical; changing one means the arity-0 path was altered. `JsonConverterTests.cs` needs cases serialising through a variant's static type and through `object`, on a **non-generic** union — every existing JSON test goes through the union's declared type and would not have caught the discriminator loss.
- `README.md` — the runtime-type / variant-attribute behaviour, the Native AOT rooting constraint, and the variance answer (CS1960 plus the value-type limit).
- No new dependencies. Existing non-generic output is unchanged except for the `TResult` rename and the added STJ variant attribute.
- Source doc with the measured evidence behind each decision (D1–D12): `generic-unions.md` at the repo root.
