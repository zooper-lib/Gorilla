## Why

Gorilla's generated JSON converters hardcode variant property-name keys as string literals, so they ignore the caller's serializer configuration. A caller who sets `JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase` still gets union variant properties in PascalCase — inconsistent with the rest of their model — and camelCase input fails to deserialize. The fix is to delegate property naming to the caller's options/settings, which already own naming, case sensitivity, and null handling.

## What Changes

- Generated System.Text.Json converters (flat **and** hierarchical) resolve variant property names through `JsonSerializerOptions.PropertyNamingPolicy` instead of emitting literals.
- STJ read path honors `JsonSerializerOptions.PropertyNameCaseInsensitive` via a hand-rolled member lookup (STJ `GetProperty` is always case-sensitive).
- Variant inference (`InferVariantFromProperties`) compares the property set under the same naming policy and case-insensitivity rules; `options` is threaded into the method.
- Generated Newtonsoft converter resolves variant property names through `serializer.ContractResolver.ResolveContract(type)` (e.g. `CamelCasePropertyNamesContractResolver`), with case-insensitive lookup where settings imply it.
- Discriminator **field name** (configured via `DiscriminatorFieldName`) and discriminator **value** (the variant's C# name) stay literal — not subjected to the naming policy. They are data/config, not model property names.
- No new attribute knobs. `DiscriminatedUnionAttribute` surface unchanged (`SuppressValidation`, `DiscriminatorFieldName`, `GenerateJsonConverter`, `GenerateNewtonsoftJsonConverter`).
- Default options / default resolver output stays byte-identical to today (no regression).

## Capabilities

### New Capabilities
- `json-converter-options-awareness`: Generated discriminated-union JSON converters (System.Text.Json flat + hierarchical, and Newtonsoft) must honor the caller's serializer configuration — property naming policy, case-insensitive property matching — for variant property keys, while keeping discriminator field name and value literal.

### Modified Capabilities
<!-- None — openspec/specs/ is empty; this is the first captured capability. -->

## Impact

- `Zooper.Gorilla.Generators/DiscriminatedUnionGenerator.cs` — converter emission (`GenerateJsonConverterClass`, `GenerateHierarchicalJsonConverterClass`, `GenerateNewtonsoftJsonConverterClass`) and `InferVariantFromProperties` emitters.
- `Zooper.Gorilla.Generators.Tests/` — new round-trip / inference / case-insensitivity / golden-output tests for both serializers.
- `README.md` — JSON Serialization section: note converters honor `JsonSerializerOptions` naming + case sensitivity.
- `CHANGELOG.md` — entry.
- No public API or attribute changes; behavior change is in generated converter code only.
