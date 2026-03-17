# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Auto-generated `System.Text.Json` converters** — For every `[DiscriminatedUnion]`, the generator now emits a
  `JsonConverter<T>` class (e.g. `SignUpErrorJsonConverter`) and applies `[JsonConverter(typeof(...))]` to the
  union class automatically. No hand-written converters are needed in consumer projects.
  The converter discriminates on a `"type"` field (configurable) and maps each variant name to its factory
  method, serialising variant properties using camelCase field names.

- **`SuppressValidation` option on `[DiscriminatedUnion]`** — Setting `SuppressValidation = true` makes the
  generator emit `[Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNever]` on the class. This prevents
  the ASP.NET `ValidationVisitor` from walking into `OneOfBase` properties and throwing
  `InvalidOperationException` when a discriminated union is used inside a controller request DTO.
  Opt-in to avoid forcing a dependency on `Microsoft.AspNetCore.Mvc.Core` in non-web consumers.

- **`GenerateNewtonsoftJsonConverter` option on `[DiscriminatedUnion]`** — Defaults to `false`. When set to
  `true`, generates a `Newtonsoft.Json.JsonConverter<T>` class (e.g. `SignUpErrorNewtonsoftJsonConverter`) and
  applies `[Newtonsoft.Json.JsonConverterAttribute(typeof(...))]` to the union class. Uses
  `Newtonsoft.Json.Linq.JObject` for reading and the `JsonSerializer` for nested type deserialization.
  Requires the consuming project to reference `Newtonsoft.Json`. Can be combined with `GenerateJsonConverter`
  to emit both converters simultaneously.

- **`GenerateJsonConverter` option on `[DiscriminatedUnion]`** — Defaults to `true`. Set to `false` to suppress
  converter generation for a specific union (e.g. when providing a custom converter).

- **`DiscriminatorFieldName` option on `[DiscriminatedUnion]`** — Defaults to `"type"`. Overrides the JSON
  property name used to identify the active variant during serialisation and deserialisation.

- **`#nullable enable` header in generated files** — Generated `.g.cs` files now begin with `#nullable enable`
  to avoid nullable annotation warnings in consuming projects.

## [1.0.2] — Previous release

Initial public release with basic discriminated union generation via `[DiscriminatedUnion]` and `[Variant]`
attributes, extending `OneOfBase<T0, T1, ...>` from the OneOf library.
