# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

The next release is a major version. The OneOf dependency is removed and every union now uses one
representation: an `abstract` base type whose variants are `sealed` nested subtypes deriving from it.
This deletes the wrapper representation along with its 9-variant ceiling, its positional API, and the
third-party dependency in every shipped package. The hierarchical half of the generator — shipped
since 1.3.0, never OneOf-based — becomes the only emit path.

### Removed

- **BREAKING — the OneOf dependency.** Removed from the generator project and from the nuspec of every
  shipped package (declared in 1.0.4 through 1.5.0). Generated code no longer names it. A project that
  uses OneOf types in its own code and was compiling on Gorilla's transitive reference must add its own
  `PackageReference`.
- **BREAKING — the `OneOfBase` wrapper shape**, along with `Value`, `Index`, and the positional
  `IsT0`/`AsT0`/`TryPickT0` accessors. `Value` is meaningless once the union *is* the value; `Index`
  exposed positional identity that nothing consumed and that variant naming exists to eliminate.
- **BREAKING — `SuppressValidation` and the emitted `[ValidateNever]`**, together with the
  compilation-wide ASP.NET type probe and the config plumbing behind them. They existed solely to stop
  `ValidationVisitor` from invoking a throwing `AsT0` getter; that getter no longer exists (`AsX` is now
  a method). Verified against the framework: `ValidationVisitor` resolves metadata from the *declared*
  type and never reaches a variant's properties, so removing the attribute does not begin enforcing
  validation on variant payloads. This supersedes the `[ValidateNever]` rationale documented under
  1.0.0 below.
- **BREAKING — variant guessing when no discriminator is present** (`InferVariantFromProperties`, in
  both converters). It matched on property subsets, so a variant whose parameters were a superset of
  another's could never be read, and any foreign object containing a matching field deserialized as the
  wrong variant. If discriminator-less interop turns out to be a real requirement, exact-set matching is
  the design to reinstate — not the subset heuristic.

### Changed

- **BREAKING — `[DiscriminatedUnion]` types must be declared `abstract`.** The generator reports the new
  `ZGOR003` error rather than injecting the modifier, so the declaration never disagrees with the type it
  produces. `sealed` unions become a compile error (`CS0418`).
- **BREAKING — union constructors are emitted `private`, closing the hierarchy.** A hand-written subtype
  declared outside the union body (`public sealed record Cancelled(...) : Outcome;`) is now a compile
  error (`CS0122`) at the declaration instead of an `InvalidOperationException` at dispatch. This closes a
  hole hierarchical unions have had since 1.3.0; the affected code was already failing at runtime.
- **BREAKING — equality and `ToString` are left to the language.** No `Equals`, `GetHashCode`, or
  `ToString` is generated. A `class` union keeps reference equality (unchanged from 1.x, where
  `OneOfBase` compared wrapped variant instances); a `record` union gets value equality. `record` unions
  are newly possible — `OneOfBase` forbade them (`CS8864`) — and are now the better default.
- **BREAKING — struct unions are impossible**, not merely unsupported: variants must derive from the
  union.
- Deserialization failures report *why* a payload was unrecognized. A missing discriminator names the
  expected field; an unrecognized one names the value seen. Both name the union, and the reason survives
  the sub-union search instead of degrading to `Unknown variant type:` with a blank name.
- The generated `Variant` suffix is now real public API rather than an inference-only detail, since type
  patterns are how payloads are read.

### Added

- **`Switch` on every union.** Hierarchical unions never had one (`CS1061` before this release); without
  it, collapsing to one emit path would have removed `Switch` from every flat union.
- **Variant-named accessors for every variant *and* sub-union:** `IsX` (a `bool` property), `AsX()` (a
  method), and `TryPickX(out …)` (a method, with no remainder parameter). `AsX` is deliberately a method
  so that reflection-based property walkers — ASP.NET validation, debugger watch windows, IntelliSense
  tooltips, object mappers — never invoke a getter that throws. Note that a union declaring both a
  variant `X` and its own member `IsX`/`AsX`/`TryPickX` now fails with `CS0102`.
- **Diagnostic `ZGOR003`** (error) for a non-`abstract` union declaration, shipping with a Roslyn
  `CodeFixProvider` — the first in this package — that removes `sealed` and adds `abstract`, preserving
  every other modifier. "Fix all occurrences in solution" applies the whole migration at once.
- **Diagnostic `ZGOR004`** (error), a backstop rejecting a struct union.
- No ceiling on the number of variants. Unions of ten or more variants are covered by tests.

### Unchanged

- **The wire format.** Serialized output is byte-identical — `{"$type":"Card","number":"4111"}`,
  `{"$type":"Cash"}` — so no stored or in-flight document changes meaning. Naming-policy and
  contract-resolver handling from 1.4.0 is unaffected.

### Migration

Upgrade, run the `ZGOR003` code fix's "Fix all occurrences in solution", then handle the residue by
hand: positional accessors to variant-named ones, `Value`/`Index` removals, hand-written subtypes,
`SuppressValidation` arguments, discriminator-less payloads, and any OneOf `PackageReference` that was
previously transitive. See the migration section of the README.

## [1.5.0] — 2026-07-27

### Added

- **Variant-named `Match`/`Switch` on flat unions.** Generated unions now declare `Match`/`Switch` overloads whose parameters are named after the variants (camelCased), hiding OneOf's positional `f0`/`f1`/… versions. Call sites can use named arguments in any order (`error.Match(concurrencyConflict: …, conflict: …)`), so reordering variants in the union declaration becomes a compile error instead of a silent handler remap. Existing positional calls are unaffected. Hierarchical unions already generated a variant-named `Match`.

## [1.4.1] — 2026-06-16

### Fixed

- **Source-hint collision for same-named unions in different namespaces.** The generated file hint included the containing-type path and class name but omitted the namespace, so two `[DiscriminatedUnion]` types with the same simple name in different namespaces (e.g. `Construction.Aggregates.SectorEvolver` and `Trade.Aggregates.SectorEvolver`), or same-named nested unions under same-named containers, produced the same `.g.cs` hint. The collision caused the host to drop or duplicate the generated sources, surfacing in consuming projects as `CS8795` (missing partial implementation) or `CS0101`/`CS0111` (duplicate type/member). Source hints are now fully qualified with the namespace.

## [1.4.0] — 2026-06-16

### Changed

- Generated JSON converters now honor the caller's serializer configuration for variant property keys. `System.Text.Json` converters (flat and hierarchical) resolve keys through `JsonSerializerOptions.PropertyNamingPolicy` and honor `PropertyNameCaseInsensitive` when reading; the `Newtonsoft.Json` converter resolves keys through `serializer.ContractResolver` (e.g. `CamelCasePropertyNamesContractResolver`). Variant inference applies the same naming/case rules. The discriminator field name and value remain literal, and default-options/default-resolver output is byte-identical to previous releases.

## [1.3.0] — 2026-05-22

### Added

- Support for flat discriminated unions nested inside containing classes, interfaces, and other type scopes. The generator now preserves the containing type chain, emits nested partial scaffolding, and uses fully-qualified source hints to avoid collisions between same-named nested union types.
- Support for abstract hierarchical unions where nested sub-unions participate in outer `Match(...)` dispatch without relying on `OneOfBase` for the abstract path.
- Generated `System.Text.Json` and `Newtonsoft.Json` converters for hierarchical unions, including recursive delegation to nested sub-union converters.
- Diagnostic `ZGOR002`, which warns when a discriminated union is declared inside a containing type that is not marked `partial`.
- A generator test project covering nested flat unions, hierarchical unions, diagnostics, and JSON round-trips.

### Changed

- The README now documents nested contract-owned unions, hierarchical abstract unions, and the sample files demonstrating both patterns.
- The sample project now includes nested and hierarchical union examples in `NestedContractSamples.cs` and `ContractOutcome.cs`.

## [1.2.0] — 2026-04-29

### Fixed

- **IDE shows phantom `CS8795` errors on `[DiscriminatedUnion]` types until a file is opened.** The incremental generator pipeline was leaking non-equatable values (`ClassDeclarationSyntax`, full `Compilation`) across stages, so Roslyn's IDE host could not cache or correctly re-run the generator. Generated partial-method implementation halves intermittently went missing in IntelliSense, producing `CS8795 'must have an implementation part because it has accessibility modifiers'` even though `dotnet build` succeeded; opening any union file forced a re-run that cleared the errors across all unions until the cache desynced again.

### Changed

- Rewrote `DiscriminatedUnionGenerator` to use `ForAttributeWithMetadataName` and project to fully value-equatable models (`UnionModel`, `VariantModel`, `VariantParameter`, `UnionConfig`, `EquatableArray<T>`). Framework detection (`System.Text.Json`, `Newtonsoft.Json`, `Microsoft.AspNetCore.Mvc` validation) is now its own `CompilationProvider.Select` stage that emits a small `FrameworkSupport` record struct, so per-union output only re-runs when that union's model or a framework toggle actually changes.
- Bumped `Microsoft.CodeAnalysis.CSharp` and `Microsoft.CodeAnalysis.CSharp.Workspaces` from `4.3.0` to `4.8.0` (required for `ForAttributeWithMetadataName`).
- Hardened `[DiscriminatedUnion]` attribute argument parsing against `null` / wrongly-typed `TypedConstant` values (previously could throw and silently kill the generator).
- Wrapped per-union source generation in a guard that reports diagnostic `ZGOR001` instead of failing silently.

### Internal

- Added `IsExternalInit` polyfill so `record` and `init` setters compile on the `netstandard2.0` generator target.
- Added `AnalyzerReleases.Shipped.md` / `AnalyzerReleases.Unshipped.md` for the new `ZGOR001` diagnostic.

## [1.1.0] — 2026-04-17

### Changed

- Default discriminator field name changed from `"type"` to `"$type"` in both the generator and the `DiscriminatedUnionAttribute`.

## [1.0.5] — 2026-03-17

### Added

- Property-based variant inference for generated JSON converters — when the discriminator field (default: "type") is missing, converters infer the variant by matching JSON property names to variant parameters.
- Case-insensitive discriminator matching — discriminator values are compared using ordinal case-insensitive comparison.
- Ambiguity detection — converters throw an explicit error when more than one variant matches the provided properties; include the discriminator to disambiguate.

## [1.0.4] — 2026-03-17

### Fixed

- A bug where the Newtonsoft Json converter generation failed due to a missing argument.

## [1.0.3] — 2026-03-17

### Added

- **Automatic framework detection** — The generator now inspects the consuming project's compilation references
  at build time. `System.Text.Json` converters, `Newtonsoft.Json` converters, and `[ValidateNever]` are each
  emitted automatically when the corresponding assembly is referenced — no manual opt-in required.
  Attribute properties (`GenerateJsonConverter`, `GenerateNewtonsoftJsonConverter`, `SuppressValidation`) act
  as explicit overrides to force-enable or force-disable individual features.

- **Auto-generated `System.Text.Json` converters** — When the consuming project references `System.Text.Json`,
  the generator emits a `JsonConverter<T>` class (e.g. `SignUpErrorJsonConverter`) and applies
  `[JsonConverter(typeof(...))]` to the union class automatically. No hand-written converters needed.
  The converter discriminates on a `"type"` field (configurable) and maps each variant name to its factory
  method, serialising variant properties using camelCase field names.

- **Auto-generated `Newtonsoft.Json` converters** — When the consuming project references `Newtonsoft.Json`,
  the generator emits a `Newtonsoft.Json.JsonConverter<T>` class (e.g. `SignUpErrorNewtonsoftJsonConverter`)
  and applies `[Newtonsoft.Json.JsonConverterAttribute(typeof(...))]` to the union class. Uses
  `Newtonsoft.Json.Linq.JObject` for reading and `JsonSerializer` for nested type deserialization.
  Both converters can coexist on the same type when both libraries are referenced.

- **Auto-generated `[ValidateNever]`** — When the consuming project references
  `Microsoft.AspNetCore.Mvc.Core`, the generator emits
  `[Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNever]` on the class. This prevents the ASP.NET
  `ValidationVisitor` from walking into `OneOfBase` properties and throwing `InvalidOperationException`.

- **`GenerateJsonConverter` option on `[DiscriminatedUnion]`** — Explicit override. Set to `false` to suppress
  converter generation for a specific union (e.g. when providing a custom converter).

- **`DiscriminatorFieldName` option on `[DiscriminatedUnion]`** — Defaults to `"type"`. Overrides the JSON
  property name used to identify the active variant during serialisation and deserialisation.

- **`#nullable enable` header in generated files** — Generated `.g.cs` files now begin with `#nullable enable`
  to avoid nullable annotation warnings in consuming projects.

## [1.0.2] — Previous release

Initial public release with basic discriminated union generation via `[DiscriminatedUnion]` and `[Variant]`
attributes, extending `OneOfBase<T0, T1, ...>` from the OneOf library.
