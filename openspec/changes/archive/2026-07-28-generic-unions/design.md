## Context

`DiscriminatedUnionGenerator.cs` builds its output as strings from a `UnionModel` that holds only plain strings and `EquatableArray`s — no `ITypeSymbol` — so incremental caching works. Most of that pipeline is already generic-safe: `VariantParameter.Type` comes from `ITypeSymbol.ToDisplayString()`, so a parameter typed `T` round-trips today. What is not generic-safe is every place the emitter writes the union's *own* name, and the JSON converter's registration path.

Three defects surfaced while probing this, none of them caused by generics:

1. A union serialized by its **runtime** type (ASP.NET returning a variant, or `JsonSerializer.Serialize(object)`) loses its discriminator — STJ resolves `[JsonConverter]` on the type being written, which is `VisibleVariant`, not `Plain`.
2. A converter emitted inside a generic containing type is `error CS0416` — `typeof(LeafJsonConverter)` resolves to `OuterA<TOuter>.LeafJsonConverter` and carries a type parameter.
3. `Match<T>` inside a generic union is `warning CS0693` and silently shadows the union's `T`.

Every claim below marked *measured* was executed against Roslyn 4.8 / .NET, with the failure text and round-trip output quoted verbatim in `generic-unions.md` at the repo root. That document is the evidence log for this design; this document is the decision record.

## Goals / Non-Goals

**Goals:**

- `[DiscriminatedUnion]` types may declare type parameters and constraints, generating the same shape as today with the parameters carried through.
- Generic unions round-trip under both System.Text.Json and Newtonsoft **in the same release** — no phase where generics ship without working converters.
- Fix the runtime-type discriminator loss and the CS0416 converter placement for *all* unions, generic or not.
- Byte-identical output for every union whose containers are all non-generic, except the `TResult` rename and the added STJ variant attribute.

**Non-Goals:**

- A companion non-generic factory class (`Disclosure.Visible("x")`) — D9.
- Variance (`Disclosure<string>` → `Disclosure<object>`) — D12.
- Making generated converters AOT-clean. The reflection is irreducible; the constraint is documented — D10.
- Inferring sub-union membership from nesting — D7.

## Decisions

The decision IDs are stable and referenced from specs and tasks.

### D1 — Generic unions ship with working converters for both serializers

There is no release in which `Disclosure<T>` generates a union that STJ or Newtonsoft cannot round-trip.

- **STJ**: a non-generic `JsonConverterFactory` named in the attribute; `CreateConverter` closes `DisclosureJsonConverter<>` via `MakeGenericType`. ~12 lines. STJ caches the factory's output per (type, options).
- **Newtonsoft**: a non-generic `JsonConverter` shim; `JsonConverter<T>` exposes non-generic `ReadJson`/`WriteJson` as public sealed overrides, so the shim forwards through the base and the generic converter body stays the emitted text we already produce. ~14 lines. A `ConcurrentDictionary` cache is load-bearing here and not on STJ: Newtonsoft caches the shim instance on the contract but re-enters `ReadJson` per value.

Both were built and run. Rejected: a "converters not generated for generic unions" diagnostic (would be introduced and removed one release apart; the default path is `GenerateJsonConverter ?? HasSystemTextJson`, so the common declaration expects a converter and emitting nothing would silently serialize to `{}`); Newtonsoft-by-reflection over variant names.

**Sequencing constraint** (a property of the system, not a work order): converter emitters read the type-parameter and constraint fields of `UnionModel`, so those fields exist before any converter text references them.

### D2 — The converter attribute goes on the variant types too, and converters accept subtypes

Two additions, both required:

1. Emit the **STJ** converter attribute above each generated variant class, not only the union.
2. Override `CanConvert` so the converter claims the hierarchy: `typeof(Plain).IsAssignableFrom(t)`.

Doing only (1) throws `InvalidOperationException: The converter specified on 'Web.Plain+VisibleVariant' is not compatible with the type ...` — `JsonConverter<TBase>` refuses a derived `typeToConvert` until `CanConvert` says otherwise.

For a generic union the same claim is made twice: on the converter, and on the factory, whose `CanConvert`/`CreateConverter` walk base types to the closed union so a variant resolves to its union's type arguments. The Newtonsoft shim's `CanConvert` walks the same way — a direct `GetGenericTypeDefinition()` comparison returns false for every variant (`Disclosure<>.VisibleVariant ≠ Disclosure<>`), which only went unnoticed because Newtonsoft skips `CanConvert` when the converter arrives via `[JsonConverter]`; it is consulted on manual `settings.Converters.Add(...)`.

**Newtonsoft needs neither addition.** It honours the attribute inherited from the base. Emitting the Newtonsoft attribute on variants would be harmful — the shim would resolve `objectType = VisibleVariant` and hand back a `Disclosure<T>`.

Rejected: `[JsonDerivedType]` (STJ 7+ only, changes the wire format); scoping the fix to generic unions (a non-generic union is where this is most likely broken in someone's project right now).

### D3 — A converter is emitted at the innermost enclosing scope that has no type parameters

- No container is generic → that scope is the union's own. Position and name are **exactly today's**. Byte-identical.
- Some container is generic → the converter is emitted outside the **outermost** generic container (namespace level if that container is outermost), absorbing the type parameters of every skipped container outermost-first followed by the union's own, repeating their constraints, and prefixing its name with the skipped containers' names: `Outer_LeafJsonConverter<TOuter, T>`.

Two measured facts: `typeof(Outer<int>.Leaf<string>).GetGenericArguments()` returns `[Int32, String]` — containers first, then own, the order `MakeGenericType` expects — and `typeof(Outer<>.Leaf<>)` is legal unbound syntax, so the factory's type test needs no special casing.

Rejected: hoisting every converter to namespace level (renames the converter for every nested union that works today, breaking `settings.Converters.Add(new Outer.LeafJsonConverter())`); diagnosing the shape as unsupported (C# allows it — only the placement was wrong).

Nobody's converter name changes: the only shape whose name moves is the one that does not compile today. Residual collision risk — a union literally named `Outer_Leaf` beside the hoisted `Outer<T>.Leaf` — is noted, not defended against.

### D4 — The result parameter is named `TResult` in all generated output, generic or not

One emitted shape regardless of whether the union has type parameters. Method type parameter names are not part of the API — call sites pass type arguments positionally — so nothing recompiles differently and nothing changes at runtime.

If `TResult` is taken, suffix-uniquify (`TResult1`, `TResult2`, …). The taken-name check covers **every type parameter in scope**: a union nested in `Outer<TResult>` inherits that name and hits the same CS0693. Candidate set = containers' parameters ∪ the union's own — the list `ContainingTypeInfo` gains for D3 anyway.

Rejected: emitting `T` for non-generic and `TResult` for generic unions (two shapes of generated code differing on a property the reader is not thinking about); uniquifying against the union's own parameters only.

### D5 — Constraints are rendered from `ITypeParameterSymbol`, constraint types via `ToDisplayString()`

`where T : IEqualityComparer<T>, new()` is captured as `where T : System.Collections.Generic.IEqualityComparer<T>, new()` — fully qualified, so it resolves in a generated file whose using block is one line (`using System;`), regardless of the user's usings and regardless of the scope a D3-hoisted converter lands in. Same convention the model already uses for `VariantParameter.Type`.

Rendering order is fixed by the language and must be produced in it: primary constraint (`class` / `class?` / `struct` / `unmanaged` / `notnull` / base type), then interfaces, then `new()`. Read off `HasReferenceTypeConstraint`, `ReferenceTypeConstraintNullableAnnotation`, `HasValueTypeConstraint`, `HasUnmanagedTypeConstraint`, `HasNotNullConstraint`, `ConstraintTypes`, `HasConstructorConstraint`.

Rejected: copying clause text from the declaration syntax (unqualified names → CS0246, breaks on `using` aliases, no single source across partial declarations).

**Failure mode this creates:** a wrong rendering order leaves the *union* compiling and the *converter* not — a partial declaration inherits constraints from the part that declares them, but the converters are separate generic types and must repeat them. Constraint tests must assert on the **converter's** clause.

### D6 — Arity is appended to any path segment whose arity is greater than zero

`Ns.Foo.g.cs` for `Foo`, `Ns.Foo_1.g.cs` for `Foo<T>`, `Ns.Outer_1.Leaf.g.cs` for `Outer<T>.Leaf`. Underscore, not the CLR backtick, which is not safe in a hint name. Without it both arities produce `Ns.Foo.g.cs`, the host drops one, and the project fails CS8795 on the survivor's unimplemented members.

Every arity-0 hint is byte-identical, so no existing generated file is renamed and no project's `EmitCompilerGeneratedFiles` output churns.

Rejected: unconditional arity (`Ns.Foo_0.g.cs`) — renames every generated file in every existing project for no behavioural gain; arity on the union alone — container arity collides identically.

### D7 — Membership is declared by the base clause; the generator does not infer it from nesting

`GetDirectSubUnions` is unchanged. Measured: Roslyn returns the definition symbol when a type is constructed with its own type parameters, so `Outcome<T>` written inside `Outcome<T>` *is* `classSymbol` and the existing `Equals` comparison already works for generic unions.

The comparison is load-bearing in a way `OriginalDefinition` would destroy — see D8.

Auto-emitting the base clause was measured to be mechanically sound and is rejected on modelling grounds: nesting has two meanings and only the base clause separates them. **Sub-union** (*is a*) belongs in the parent's `Match`; **payload union** (*has a*) is nested for name scoping, like a helper enum. Inferring membership from location collapses them — a payload union silently becomes a case of its parent, and the only workaround is to stop nesting.

**Invariant:** a sub-union derives from its **immediate** enclosing union, never a further ancestor. Already holds — `GetDirectSubUnions` reads `GetTypeMembers()`, direct members only.

### D8 — `ZGOR005` reports a nested union deriving from a different construction of its parent

Severity **Warning**, consistent with `ZGOR002` (generated code that will surprise the user, not a malformed declaration).

Fires when a nested type carries `[DiscriminatedUnion]` and `BaseType.OriginalDefinition` equals the enclosing union's definition while `BaseType` does not. Only reachable by naming the enclosing union with the wrong type arguments, so it has no legitimate use. The message names both types, since they differ only in their arguments.

Without it the mistake is silent: the nested union still generates, the parent's `Match` never mentions it, and the omission surfaces at runtime through the `_ => throw new InvalidOperationException` arm. Comparing with `BaseType.OriginalDefinition` instead would turn that silent omission into generated code that does not compile — `Func<Weird, TResult>` and a `case Weird` arm inside `Outcome<T>`'s `Match` where `Weird : Outcome<int>`.

### D9 — No companion static class is emitted; call sites name the closed type

`Disclosure<string>.Visible("x")` stays. C# infers method type arguments from arguments only, never from the return type, so a companion `static class Disclosure` shortens exactly those variants whose parameters mention every type parameter and no others — half the factories short, half long, with no rule visible at the call site. It fails hardest on the motivating case: `Result.Ok<TValue, TError>(value)` cannot infer `TError`, so every call stays explicit. Secondary: a user who already has a non-generic `Disclosure` union gets CS0101 from a type they never declared.

Revisit only if real usage shows the verbosity biting.

### D10 — The reflection is suppressed at the emission site and the constraint is documented

`#pragma warning disable IL3050` / `restore` around the two `MakeGenericType` lines (the STJ factory's `CreateConverter`, the Newtonsoft shim's `Inner`), with a comment naming the reason, plus one README line: a generic union's converter is constructed at runtime, so under Native AOT the closed union types must be rooted. Verified the pragma removes both warnings and raises no CS1691.

The reflection cannot be removed — the set of closed types is not knowable at generation time, which is why `JsonConverterFactory` exists. Suppressed rather than surfaced because it lands in a `.g.cs`: a consumer with `TreatWarningsAsErrors` and one generic union gets a hard build failure at a location they cannot edit, with no action available to them.

Rejected: `[UnconditionalSuppressMessage]` — more precise, but requires `System.Diagnostics.CodeAnalysis` on the *consumer's* target framework, which a `netstandard2.0` generator cannot assume.

Distinct and *not* introduced here: `IL2026`/`IL3050` on `JsonSerializer.Serialize<TValue>` at the caller's own call sites — reflection-based STJ was never AOT-clean.

### D11 — Display form in compile-time diagnostics, bare name in runtime message text

Diagnostics name the union as written: `Disclosure<T>`, `Outcome<int>`. Message strings emitted *into* generated code keep the bare name. The split follows what is knowable where: at diagnostic time the declaration is in hand and the type arguments are what disambiguates (D6 makes `Foo` and `Foo<T>` coexist, and distinguishing `Outcome<int>` from `Outcome<T>` is the entire content of `ZGOR005`); at runtime the closed type is not knowable when the string is generated, and `{GetType().Name}` already reports the actual type.

`GenerateAccessors` and `EmitFailureMessage` are unchanged.

### D12 — No variance support; nothing is emitted for it

`error CS1960` — only interface and delegate type parameters may be variant, because classes have fields and a field is read *and* write. Gorilla's unions must be classes or records (variants derive from them; the private constructor closes the hierarchy), so the annotation is out of reach by construction.

A covariant interface emitted beside the union does compile and does widen, but was measured to be worth less than it looks: it cannot carry `Match` (`Disclosure<T>.VisibleVariant` is invariant in `T` → CS1961, so handlers get only the bare payload and a variant like `Visible(T value, string reason)` loses `reason`); it does not apply to value types (CS0266 — variance rides on reference conversions, boxing is not one); and `static string Describe<T>(Disclosure<T> d)` already covers most "works for any payload" needs.

Nothing is blocked: the union is `partial`, so a user with a genuine need declares the interface and adds `: IDisclosure<T>` on their own part. Rejected also: `Select<TTarget>(Func<T, TTarget>)` — type-safe and value-type-friendly, but must reconstruct every variant and stops being mechanical as soon as `T` appears nested (`IReadOnlyList<T>`, `Dictionary<string, List<T>>`).

## Risks / Trade-offs

- **Constraint rendering order is wrong for some combination** → the union compiles and the converter does not. Constraint tests assert on the converter's clause, not the union's, covering `notnull`, `class`, and `IComparable<T>, new()`.
- **The `TResult` rename is source-visible** → mitigated by it not being part of the API (positional type arguments) and by it being one emitted shape for all unions. One test pins the literal text (`HierarchicalUnionTests.cs:14`) and is updated deliberately.
- **The D2/D3/D6 changes touch non-generic output** → each is bounded by an explicit byte-identical claim: arity-0 hints unchanged, converter position and name unchanged when no container is generic. `SourceHintCollisionTests`' three existing assertions must stay unmodified; a change to any of them means the arity-0 path was altered.
- **Native AOT** → suppressed warning, documented rooting requirement. Residual risk is bounded and specific: a missing-instantiation failure, not a wrong result.
- **Converter name collision** after D3 hoisting (`Outer_Leaf` beside `Outer<T>.Leaf`) → accepted, not defended against.
- **`ZGOR005` false positives** → the trigger condition is only reachable by writing the wrong type arguments on the base clause; a nested union deriving from an *unrelated* union is a legitimate sub-union of that type and is not this rule's business.

## Migration Plan

No migration. All changes are inside the generator; consumers rebuild and get the new output. Two source-visible deltas, both covered above: the `TResult` rename in `Match`'s signature, and an added STJ converter attribute on variant classes. No wire-format change — `{"$type":"Visible","value":"hello"}` before and after, on every path that worked before, plus the paths that silently dropped the discriminator.

## Open Questions

None blocking. D1–D12 are decided and each rejected alternative is recorded above with the reason it was rejected. `generic-unions.md` holds the verbatim failure text and round-trip output behind each measured claim.
