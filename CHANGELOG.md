# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
