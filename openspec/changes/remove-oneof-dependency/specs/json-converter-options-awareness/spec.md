## MODIFIED Requirements

### Requirement: STJ converters resolve variant property names via naming policy

Generated System.Text.Json converters SHALL resolve variant property names through the caller's `JsonSerializerOptions.PropertyNamingPolicy` for both writing and reading, instead of emitting hardcoded string literals. When no naming policy is configured, the property name SHALL be the variant parameter's C# name unchanged.

#### Scenario: Serialize with camelCase naming policy
- **WHEN** a value is serialized with `new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }`
- **THEN** the emitted variant property keys are in camelCase

#### Scenario: Round-trip with camelCase naming policy (union without sub-unions)
- **WHEN** a union value with a multi-parameter variant is serialized then deserialized using a camelCase naming policy
- **THEN** the deserialized value equals the original

#### Scenario: Round-trip with camelCase naming policy (union with sub-unions)
- **WHEN** a value of a union declaring nested sub-unions is serialized then deserialized using a camelCase naming policy
- **THEN** the deserialized value equals the original, including outer-then-inner dispatch

#### Scenario: Default options output unchanged
- **WHEN** a value is serialized with default `JsonSerializerOptions` (no naming policy)
- **THEN** the output is byte-identical to the pre-change generated output

### Requirement: Newtonsoft converter resolves names via contract resolver

The generated Newtonsoft converter SHALL resolve variant property names through `serializer.ContractResolver.ResolveContract(type)` of the variant's value type, so contract resolvers such as `CamelCasePropertyNamesContractResolver` apply to variant property keys for both writing and reading. Reads SHALL honor case-insensitive lookup where the contract/settings imply it.

#### Scenario: Round-trip with CamelCasePropertyNamesContractResolver (union without sub-unions)
- **WHEN** a union value is serialized then deserialized with a `JsonSerializer` using `CamelCasePropertyNamesContractResolver`
- **THEN** the variant property keys are camelCase and the deserialized value equals the original

#### Scenario: Round-trip with CamelCasePropertyNamesContractResolver (union with sub-unions)
- **WHEN** a value of a union declaring nested sub-unions is serialized then deserialized with a `CamelCasePropertyNamesContractResolver`
- **THEN** the deserialized value equals the original

#### Scenario: Default resolver output unchanged
- **WHEN** a value is serialized with a default `JsonSerializer` (no custom resolver)
- **THEN** the output is byte-identical to the pre-change generated output

### Requirement: No new attribute knobs

This change SHALL NOT add property-naming or discriminator-value options to `DiscriminatedUnionAttribute`. The attribute surface SHALL remain `DiscriminatorFieldName`, `GenerateJsonConverter`, `GenerateNewtonsoftJsonConverter`.

#### Scenario: Attribute surface unchanged
- **WHEN** the generator is built after this change
- **THEN** `DiscriminatedUnionAttribute` exposes only those three members, with no naming-related members and no `SuppressValidation`

## REMOVED Requirements

### Requirement: Variant inference respects naming policy and case sensitivity

**Reason**: `InferVariantFromProperties` is deleted. Property-set inference used subset matching, which cannot read a variant whose parameters are a superset of another's, silently misidentifies foreign objects containing a matching field, and carries no information at all when two variants share a field set. It only ever ran on JSON that Gorilla did not write, and no consumer of that kind is known. With the heuristic gone there is no inference path for naming policy or case sensitivity to apply to.

**Migration**: Include the discriminator field in payloads handed to a generated converter. Gorilla has always written it, so payloads produced by Gorilla are unaffected; only hand-written or foreign JSON needs updating. Callers previously relying on inference now receive an explicit error naming the missing discriminator field instead of a possibly-wrong variant — see the `union-json-discriminator` capability. If discriminator-less interop later proves to be a real requirement, exact-set matching is the design to reinstate, not the subset heuristic.
