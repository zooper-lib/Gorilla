## 1. Model: type parameters and constraints

The converter emitters read these fields, so they exist first (D1 sequencing constraint).

- [x] 1.1 Add `TypeParameters` (`EquatableArray<string>` of names) and `TypeParameterConstraints` (`EquatableArray<string>` of pre-rendered `where` clauses) to `UnionModel`. Plain strings only — no `ITypeSymbol`, or incremental caching breaks.
- [x] 1.2 Add a `RenderConstraintClause(ITypeParameterSymbol)` helper that emits parts in language order: primary (`class` / `class?` / `struct` / `unmanaged` / `notnull` / base type) → interfaces → `new()`. Read `HasReferenceTypeConstraint`, `ReferenceTypeConstraintNullableAnnotation`, `HasValueTypeConstraint`, `HasUnmanagedTypeConstraint`, `HasNotNullConstraint`, `ConstraintTypes`, `HasConstructorConstraint`. Constraint types via `ToDisplayString()` (fully qualified) — never from declaration syntax.
- [x] 1.3 Populate both fields in the union model factory from `classSymbol.TypeParameters`.
- [x] 1.4 Extend `ContainingTypeInfo` (`DiscriminatedUnionGenerator.cs:880`) with the container's type parameter names, arity, and constraint clauses, using the same renderer. Populate in `GetContainingTypes` (line 144).
- [x] 1.5 Add a `SelfTypeReference(union)` helper returning `Disclosure<T>` (bare name when arity is 0), and a helper for the ordered container-plus-own parameter list that D3 and D4 both need.

## 2. Emitting a generic union

- [x] 2.1 `GetTypeDeclarationPrefix` (line 839) emits the type parameter list and the union's constraint clauses.
- [x] 2.2 `EmitContainingTypeOpen` (line 396) reopens a generic container with its type parameter list and constraints — `partial class Outer<TKey>`, not `partial class Outer`.
- [x] 2.3 Route the variant base type and the factory return types through `SelfTypeReference`. Leave the private constructor bare (`private Disclosure()`) — it is an identifier there, not a type.
- [x] 2.4 `GetNestedTypeReference` (line 861) emits `Disclosure<T>.VisibleVariant`.
- [x] 2.5 Verify variant classes are emitted with no type parameter list and no constraint clause of their own — they inherit both from the enclosing type.
- [x] 2.6 Tests: factory, `Match`, `Switch`, and accessors on `Disclosure<T>`; on `Result<TValue, TError>` with argument order preserved everywhere; a variant whose payload is `T` and one whose payload is `IReadOnlyList<T>`.

## 3. Match result type parameter (D4)

- [x] 3.1 `GenerateMatchMethod` (line 446) emits `public TResult Match<TResult>(` for every union, generic or not. `GenerateSwitchMethod` is unaffected.
- [x] 3.2 Suffix-uniquify (`TResult1`, `TResult2`, …) against the union's own type parameters **and** every containing type's parameters.
- [x] 3.3 Update `HierarchicalUnionTests.cs:14` — the only place in the repo pinning `"public T Match<T>("`.
- [x] 3.4 Tests: a union declaring a parameter named `TResult`; a union nested in `Outer<TResult>`. Both compile warning-clean (catches CS0693).

## 4. Source hints (D6)

- [x] 4.1 `GetSourceHint` (line 295) appends `_<arity>` to any segment with arity > 0, for both container segments and the union's own name. Underscore, not backtick.
- [x] 4.2 Test: `Foo` and `Foo<T>` in one namespace both generate; `Outer.Leaf` and `Outer<T>.Leaf` both generate.
- [x] 4.3 Confirm `SourceHintCollisionTests.cs`'s three existing assertions still pass **unmodified**. Needing to edit one means the arity-0 path changed.

## 5. Diagnostics (D8, D11)

- [x] 5.1 Diagnostic call sites (lines 251–256, 287–291) name the union in display form (`Disclosure<T>`), not `union.ClassName`. `GenerateAccessors` (line 517) and `EmitFailureMessage` (line 787) keep the bare name — unchanged.
- [x] 5.2 Add the `ZGOR005` descriptor beside `NonAbstractUnionDescriptor` (line 39), severity Warning, message naming both the declared base and the enclosing union.
- [x] 5.3 Report `ZGOR005` from the `GetDirectSubUnions` filter (lines 168–171) when `BaseType.OriginalDefinition` equals the enclosing union's definition but `BaseType` does not. Keep the existing `Equals(BaseType, classSymbol)` comparison for membership — switching to `OriginalDefinition` would include the case this diagnostic reports and emit non-compiling `Match` arms.
- [x] 5.4 Add the `ZGOR005` row to `AnalyzerReleases.Unshipped.md`. Omitting it is an RS2008 build failure.
- [x] 5.5 Tests: `ZGOR003` on a generic union names it `Disclosure<T>`; a nested union deriving from a different construction reports `ZGOR005` and is absent from the parent's `Match`, while a correctly-constructed sibling is still collected; a nested union deriving from an unrelated union reports nothing.

## 6. JSON: converters claim the hierarchy (D2)

Applies to every union, generic or not. Fixes a live defect on today's output.

- [x] 6.1 `GenerateVariantClasses` emits the **System.Text.Json** converter attribute above each variant declaration, in addition to the existing single emission before the union declaration (lines 339–347). Do **not** emit the Newtonsoft attribute on variants.
- [x] 6.2 `GenerateJsonConverterClass` (line 573) emits `public override bool CanConvert(System.Type t) => typeof(<SelfType>).IsAssignableFrom(t);` on the STJ converter.
- [x] 6.3 Tests in `JsonConverterTests.cs`, **non-generic** union: serialize through a variant's static type and through `object` — both carry the discriminator. Include a payload-free variant (`{"$type":"NotProvided"}`, not `{}`). Every existing JSON test goes through the union's declared type, so none of them cover this.

## 7. JSON: registering a generic union's converter (D1)

- [x] 7.1 Thread the union's type parameters and constraints through the existing converter emitters so the generic converter body is `DisclosureJsonConverter<T> : JsonConverter<Disclosure<T>>` with the constraints repeated. The converter is a separate generic type — it may not omit them, even though the union's own partial may.
- [x] 7.2 Emit a non-generic STJ `JsonConverterFactory` for generic unions and name it in the attribute. `CanConvert`/`CreateConverter` walk base types to the closed union (`ClosedUnion` helper) so a variant resolves to its union's arguments, then `MakeGenericType` + `Activator.CreateInstance`. No cache — STJ caches per (type, options).
- [x] 7.3 Emit a non-generic Newtonsoft shim for generic unions, delegating `ReadJson`/`WriteJson` through `JsonConverter<T>`'s public sealed non-generic overrides. Same base-walking `CanConvert`. Include the `ConcurrentDictionary` cache — Newtonsoft re-enters `ReadJson` per value.
- [x] 7.4 Tests: round-trip `Disclosure<string>` and `Disclosure<int>` under both serializers, identical JSON; the D2 shapes (variant static type, `object`) through the factory; the Newtonsoft shim registered manually via `settings.Converters.Add(...)` serializing a variant — the only path that reaches its `CanConvert`; a generic union as a DTO property still round-trips.
- [x] 7.5 Tests: constraints round-trip on the **converter** — `where T : notnull`, `where T : class`, `where T : IComparable<T>, new()`. Asserting on the union alone proves nothing; a wrong D5 ordering leaves the union compiling and only the converter failing.

## 8. Converter placement (D3)

- [x] 8.1 `GenerateSource` currently emits converters (lines 369–379) before the containing-type close loop (lines 381–385). Interleave them: close containers down to the innermost scope with no type parameters, emit the converters there, close the rest.
- [x] 8.2 When some container is generic, name the converter with the skipped containers' names as a prefix (`Outer_LeafJsonConverter`), give it the skipped containers' type parameters outermost-first followed by the union's own, and repeat their constraint clauses.
- [x] 8.3 Confirm the no-generic-container path is byte-identical to the previous release — same position, same name. `GetSourceHint` is unaffected; the hint is per union, not per converter.
- [x] 8.4 Tests: a **non-generic** union inside a generic container compiles (the CS0416 regression — fails on today's emitter); `Outer<TOuter>.Leaf<T>` compiles and round-trips under both serializers.

## 9. Native AOT (D10)

- [x] 9.1 Wrap the two emitted `MakeGenericType` sites — the STJ factory's `CreateConverter` and the Newtonsoft shim's converter resolution — in `#pragma warning disable IL3050` / `restore`, with a comment naming the reason.
- [x] 9.2 Test: the emitted reflection sites carry the pragma. Confirm no CS1691 for an unrecognised warning id.

## 10. Documentation

- [x] 10.1 README — the JSON section documents converter behaviour and does not mention D2: a union written by its runtime type (ASP.NET response, `Serialize(object)`) now keeps its discriminator.
- [x] 10.2 README — Native AOT note: a generic union's converter is constructed at runtime, so closed union types must be rooted.
- [x] 10.3 README — variance: record CS1960 and the value-type limit (CS0266 on `Disclosure<int>` → `IDisclosure<object>`), and that a user may add their own interface on their own `partial` part. Answers the question once.
- [x] 10.4 CHANGELOG — generic unions, the `TResult` rename, the D2 discriminator fix, `ZGOR005`.

## 11. Verification

- [x] 11.1 Full test suite green.
- [x] 11.2 Generated output for a generic union with constraints, a generic container, and both converters compiles with `TreatWarningsAsErrors`.
- [x] 11.3 Confirm the non-generic output diff is limited to the `TResult` rename and the added STJ variant attribute — no converter renamed, no source hint changed.
