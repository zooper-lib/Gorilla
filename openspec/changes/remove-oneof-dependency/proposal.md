## Why

Gorilla's flat unions derive from `OneOfBase<T0…Tn>`, which caps a union at 9 variants with no workaround, forces a third-party dependency into every consumer's graph and into generated code, and leaks a positional API (`IsT0`/`AsT0`/`TryPickT0`) that our variant-named `Match`/`Switch` can only *hide*, not override — so the reorder-safety that feature exists to provide evaporates for anyone holding the value as its OneOf base type. Meanwhile the hierarchical path (abstract base, sealed variant subclasses, type-switch dispatch) has shipped since 1.3.0 without OneOf, is covered by behavioral tests, and has none of those limits. This change deletes the OneOf path and makes the hierarchical representation the only one, collapsing two divergent codepaths into one.

Now, because this is a major version: the migration edit (`sealed`/plain → `abstract` on every union declaration) is source-breaking regardless, so spending that budget once — on one representation rather than two — is the cheapest moment to do it.

## What Changes

- **BREAKING** — Flat unions adopt the hierarchical representation: a union is an `abstract` base type whose variants are sealed nested subtypes. The `OneOfBase` wrapper shape is gone, along with `Value`, `Index`, and positional `IsT0`/`AsT0`/`TryPickT0`. Type patterns (`is`, `switch`, property patterns) now work on every union.
- **BREAKING** — `[DiscriminatedUnion]` types MUST be declared `abstract`. The generator rejects non-`abstract` declarations with a new diagnostic rather than injecting the modifier. `sealed` unions become a compile error (`CS0418`). 20 of 31 declarations in this repo must change.
- **BREAKING** — The OneOf package reference is dropped from the generator and from every shipped nuspec. Consumers using OneOf types in their own code must add their own `PackageReference`.
- **BREAKING** — `SuppressValidation` and the `[ValidateNever]` emission are removed, along with the ASP.NET type probe and config plumbing. They existed solely to stop `ValidationVisitor` from invoking a throwing `AsT0` getter; that getter no longer exists.
- **BREAKING** — Variant guessing (`InferVariantFromProperties`) is deleted from both converters. The discriminator field is required; a payload without it is an error instead of being subset-matched to a possibly-wrong variant.
- **BREAKING** — Union constructors are emitted `private`, closing the hierarchy. Hand-written subtypes (`public sealed record Cancelled(...) : Outcome;`) become a compile error at the declaration instead of an `InvalidOperationException` at dispatch. This closes a hole hierarchical unions have had since 1.3.0.
- Variant-named accessors are generated for every variant *and* sub-union: `IsX` (property), `AsX()` (method), `TryPickX(out …)` (method). `AsX` is a method specifically so property-walking reflection — ASP.NET validation, debugger watch windows, IntelliSense tooltips, mappers — never invokes a throwing getter.
- `Switch` is emitted for every union. Hierarchical unions never had one (`CS1061` today); without this, Decision 1 would delete `Switch` from all 20 flat unions.
- Deserialization failures report *why* a payload was unrecognized. Missing discriminator and unrecognized discriminator are distinct errors, and the reason survives the sub-union search instead of degrading to `Unknown variant type:` with a blank name.
- Equality and `ToString` are left to the language: no generated `Equals`/`GetHashCode`/`ToString`. `class` unions get reference equality (unchanged from today), `record` unions get value equality. `record` unions are newly possible — `OneOfBase` forbade them (`CS8864`).
- Generated variant types keep the `Variant` suffix, now real public API rather than an inference-only detail.
- A Roslyn `CodeFixProvider` ships with the declaration diagnostic, giving "Fix all occurrences in solution" for the mechanical `sealed`/plain → `abstract` migration.
- `TypeKind.Struct` becomes a rejection rather than an emission. Struct unions are permanently impossible under this representation, not merely unsupported.

## Capabilities

### New Capabilities

- `union-representation`: The single emit path — unions as `abstract` base types with sealed nested variant subtypes; the `abstract` declaration requirement; the `Variant` naming rule; `private` constructor / closed hierarchy; equality and `ToString` delegated to the language keyword; struct rejection.
- `union-api-surface`: The generated public members — variant-named `Match` and `Switch` over variants *and* sub-unions, `IsX` property, `AsX()` method, `TryPickX(out …)` method, and the exhaustiveness boundary (guaranteed for `Match`/`Switch`, not for patterns or accessors).
- `union-json-discriminator`: Discriminator-required deserialization — removal of property-set inference, and the failure-reporting rules that distinguish a missing discriminator from an unrecognized one across the sub-union search.
- `union-declaration-diagnostic`: The diagnostic raised for a non-`abstract` `[DiscriminatedUnion]` type and its accompanying `CodeFixProvider`.

### Modified Capabilities

- `nested-union-generation`: Requirements are written against the flat/hierarchical split that this change removes. "using the existing `OneOfBase`-based switch for flat sealed-class unions" becomes false; scenarios phrased as "sealed class" unions must become `abstract`; hierarchical unions gain `Switch` and informative deserialization failures they do not have today.
- `json-converter-options-awareness`: Two requirements become false. "Variant inference respects naming policy and case sensitivity" describes `InferVariantFromProperties`, which is deleted. "No new attribute knobs" asserts the surface "SHALL remain `SuppressValidation`, `DiscriminatorFieldName`, `GenerateJsonConverter`, `GenerateNewtonsoftJsonConverter`" — `SuppressValidation` is removed.

## Impact

**Dependency.** OneOf 3.0.263 removed from the generator project and from the nuspec of every shipped package (declared in 1.0.4 through 1.5.0). Generated code no longer names it.

**Generator** (`Zooper.Gorilla.Generators/DiscriminatedUnionGenerator.cs`): `GenerateFlatSource` and everything reached only from it are deleted, including the second JSON converter pair. `UnionModel.IsAbstract` and the branch at `:229` are removed; `GetTypeDeclarationPrefix` always emits `abstract`. `GetHierarchicalVariantKeyword` and `SubUnions` handling become the behavior for all unions. `GetTypeKeyword`'s `TypeKind.Struct` branches become rejections. `InferVariantFromProperties` and its `ResolvePropertyName` call sites in the guessing path are deleted from both the STJ and Newtonsoft converters, with the ambiguity diagnostics only that path could raise.

**Analyzer package**: a new diagnostic joins `ZGOR001`/`ZGOR002` and must be registered in `AnalyzerReleases.Unshipped.md`. A `CodeFixProvider` is added — the first in this package — making `Microsoft.CodeAnalysis.CSharp.Workspaces` (currently `PrivateAssets="all"`) part of what the analyzer needs at runtime. Packaging is unaffected: `Zooper.Gorilla.Generators.csproj` already packs to `analyzers/dotnet/cs`.

**ASP.NET integration**: eight code sites and a compilation-wide type probe for `[ValidateNever]`/`SuppressValidation` are deleted. Verified against the framework: `ValidationVisitor` resolves metadata from the *declared* type and never reaches a variant's properties, so removing the attribute does not begin enforcing validation on variant payloads.

**Repo sources**: 20 union declarations change — 17 `sealed partial class` → `abstract partial class` (`Zooper.Gorilla.Sample/NestedContractSamples.cs:25`; `Zooper.Gorilla.Generators.Tests/TestSources.cs` lines 9, 46, 86, 114, 144, 173, 183, 197, 237, 344, 359, 390, 518, 528, 547, 560) and 3 plain `partial class` → `abstract partial class` (`Zooper.Gorilla.Sample/SignInError.cs`, `SignUpError.cs`, `CreateProfileDto.cs`). 11 `abstract partial record` declarations already comply.

**Docs**: `README.md` teaches two declarations that no longer compile (flat example at `:37`, nested at `:95`), presents `abstract` as what enables sub-unions (no longer distinguishing), and teaches `class` for flat unions with `record` only for hierarchical ones — an emphasis this change inverts. `CHANGELOG.md:103` documents `[ValidateNever]`'s rationale.

**Wire format**: unchanged. Both converter designs produce byte-identical payloads (`{"$type":"Card","number":"4111"}`), so no stored or in-flight document changes meaning.

**Not addressed**: generic unions remain unsupported (our gap, not OneOf's — neither fixed nor worsened). An exhaustiveness analyzer over type patterns is explicitly deferred; it addresses a pre-existing gap, though this change's closed hierarchy makes it tractable. Attributes on `[Variant]` parameters remain silently dropped (`DiscriminatedUnionGenerator.cs:543`, `:646`).
