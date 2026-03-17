# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
