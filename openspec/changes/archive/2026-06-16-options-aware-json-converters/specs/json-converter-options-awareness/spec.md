## ADDED Requirements

### Requirement: STJ converters resolve variant property names via naming policy

Generated System.Text.Json converters (both flat and hierarchical) SHALL resolve variant property names through the caller's `JsonSerializerOptions.PropertyNamingPolicy` for both writing and reading, instead of emitting hardcoded string literals. When no naming policy is configured, the property name SHALL be the variant parameter's C# name unchanged.

#### Scenario: Serialize with camelCase naming policy
- **WHEN** a value is serialized with `new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }`
- **THEN** the emitted variant property keys are in camelCase

#### Scenario: Round-trip with camelCase naming policy (flat union)
- **WHEN** a flat union value with a multi-parameter variant is serialized then deserialized using a camelCase naming policy
- **THEN** the deserialized value equals the original

#### Scenario: Round-trip with camelCase naming policy (hierarchical union)
- **WHEN** a hierarchical (nested sub-union) value is serialized then deserialized using a camelCase naming policy
- **THEN** the deserialized value equals the original, including outer-then-inner dispatch

#### Scenario: Default options output unchanged
- **WHEN** a value is serialized with default `JsonSerializerOptions` (no naming policy)
- **THEN** the output is byte-identical to the pre-change generated output

### Requirement: STJ read honors case-insensitive property matching

Generated System.Text.Json converters SHALL honor `JsonSerializerOptions.PropertyNameCaseInsensitive` when reading variant properties. Because STJ `JsonElement.GetProperty` is always case-sensitive, the converter SHALL perform a case-insensitive member lookup when the option is enabled.

#### Scenario: Deserialize mixed-case keys when case-insensitive
- **WHEN** JSON whose property keys differ only in case from the expected names is deserialized with `PropertyNameCaseInsensitive = true`
- **THEN** deserialization succeeds and produces the correct value

#### Scenario: Missing property throws
- **WHEN** a required variant property is absent from the JSON
- **THEN** the converter throws a `JsonException` naming the missing property

### Requirement: Variant inference respects naming policy and case sensitivity

Generated converters' `InferVariantFromProperties` logic (used when no discriminator field is present) SHALL compare the candidate property set under the same naming policy and case-insensitivity rules as the active serializer configuration. The `options` SHALL be threaded into the inference method.

#### Scenario: Inference under camelCase naming policy
- **WHEN** a union is deserialized without a discriminator field, using a camelCase naming policy
- **THEN** the correct variant is inferred from the camelCase property keys

#### Scenario: Inference under case-insensitive matching
- **WHEN** a union is deserialized without a discriminator field and `PropertyNameCaseInsensitive = true`
- **THEN** the correct variant is inferred from mixed-case property keys

### Requirement: Newtonsoft converter resolves names via contract resolver

The generated Newtonsoft converter SHALL resolve variant property names through `serializer.ContractResolver.ResolveContract(type)` of the variant's value type, so contract resolvers such as `CamelCasePropertyNamesContractResolver` apply to variant property keys for both writing and reading. Reads SHALL honor case-insensitive lookup where the contract/settings imply it; inference SHALL resolve names through the resolver before comparing, with the `serializer` threaded into the inference method.

#### Scenario: Round-trip with CamelCasePropertyNamesContractResolver (flat)
- **WHEN** a flat union value is serialized then deserialized with a `JsonSerializer` using `CamelCasePropertyNamesContractResolver`
- **THEN** the variant property keys are camelCase and the deserialized value equals the original

#### Scenario: Round-trip with CamelCasePropertyNamesContractResolver (hierarchical)
- **WHEN** a hierarchical union value is serialized then deserialized with a `CamelCasePropertyNamesContractResolver`
- **THEN** the deserialized value equals the original

#### Scenario: Newtonsoft inference under camelCase
- **WHEN** a union is deserialized without a discriminator field using a camelCase contract resolver
- **THEN** the correct variant is inferred from the camelCase property keys

#### Scenario: Default resolver output unchanged
- **WHEN** a value is serialized with a default `JsonSerializer` (no custom resolver)
- **THEN** the output is byte-identical to the pre-change generated output

### Requirement: Discriminator field name and value bypass naming policy

The discriminator field name (configured via `DiscriminatorFieldName`) and the discriminator value (the variant's C# name) SHALL NOT be subjected to the naming policy or contract resolver in either System.Text.Json or Newtonsoft converters. They are configuration/data, not model property names.

#### Scenario: Discriminator field unaffected by naming policy
- **WHEN** a value is serialized with a camelCase naming policy / contract resolver
- **THEN** the discriminator field key remains the configured value (e.g. `$type`) and the discriminator value remains the variant's C# name

### Requirement: No new attribute knobs

This change SHALL NOT add property-naming or discriminator-value options to `DiscriminatedUnionAttribute`. The attribute surface SHALL remain `SuppressValidation`, `DiscriminatorFieldName`, `GenerateJsonConverter`, `GenerateNewtonsoftJsonConverter`.

#### Scenario: Attribute surface unchanged
- **WHEN** the generator is built after this change
- **THEN** `DiscriminatedUnionAttribute` exposes only the existing four members and no naming-related members
