# union-json-discriminator

## Purpose

Defines how a generated converter decides which variant a payload holds: from the discriminator field alone, never by guessing from which property names are present. This covers the two distinct failure modes a malformed payload produces, the requirement that the recorded reason survives the sub-union search rather than degrading to a blank message, and the guarantee that the wire format is unchanged.

## Requirements

### Requirement: The discriminator field is required for deserialization

Generated converters (System.Text.Json and Newtonsoft) SHALL resolve a variant only from the discriminator field. They SHALL NOT infer a variant from which property names are present in the payload.

#### Scenario: Payload without a discriminator is rejected

- **WHEN** `{"number":"4111"}` is deserialized as a union whose `Card` variant has a `number` parameter
- **THEN** deserialization fails with an exception reporting the missing discriminator field, rather than producing a `CardVariant`

#### Scenario: Superset payloads are no longer ambiguous

- **WHEN** `{"$type":"CardWithCvv","number":"4111","cvv":"123"}` is deserialized on a union declaring both `Card(number)` and `CardWithCvv(number, cvv)`
- **THEN** the result is a `CardWithCvvVariant` with both properties populated

#### Scenario: Foreign objects are not silently misidentified

- **WHEN** `{"number":"4111","junk":true}` is deserialized as a union
- **THEN** deserialization fails rather than returning a `CardVariant`

#### Scenario: Variants with identical shapes round-trip

- **WHEN** a union declares two variants with identical parameter sets and a value of the second is serialized and deserialized
- **THEN** the deserialized value is of the second variant

#### Scenario: Field-less variants round-trip

- **WHEN** a union declaring several field-less variants has one of them serialized and deserialized
- **THEN** the deserialized value is that same variant

### Requirement: A missing discriminator and an unrecognized discriminator are distinct errors

Generated converters SHALL distinguish the two failure modes. A payload with no discriminator field SHALL report that the field is absent, naming the expected field. A payload whose discriminator value matches no known subtype SHALL report the value it saw and the union it was reading.

#### Scenario: Missing discriminator names the field

- **WHEN** `{"bogus":1}` is deserialized as a union using the default discriminator field
- **THEN** the exception message states that the discriminator field `$type` is missing and names the union type

#### Scenario: Unrecognized discriminator names the value

- **WHEN** `{"$type":"Nonexistent"}` is deserialized as a union
- **THEN** the exception message contains `Nonexistent` and names the union type

#### Scenario: Configured discriminator field name is used in the message

- **WHEN** a union sets `DiscriminatorFieldName = "kind"` and a payload omits it
- **THEN** the exception message names `kind`, not `$type`

#### Scenario: Both converters report the same distinction

- **WHEN** the same two malformed payloads are deserialized through the System.Text.Json converter and the Newtonsoft converter
- **THEN** both report a missing discriminator and an unrecognized discriminator as distinct, informative errors

### Requirement: The failure reason survives the sub-union search

When a union's own variants do not match, the converter SHALL hand the payload to each nested sub-union in turn. If every sub-union also fails, the converter SHALL throw the reason recorded before the search rather than a generic message with an empty variant name.

#### Scenario: Nested union reports the real reason

- **WHEN** `{"bogus":1}` is deserialized as a union that declares nested sub-unions
- **THEN** the exception reports the missing discriminator field and does not produce a message with a blank variant name

#### Scenario: Unrecognized discriminator survives the search

- **WHEN** `{"$type":"Nonexistent"}` is deserialized as a union that declares nested sub-unions and no sub-union recognises it
- **THEN** the exception message contains `Nonexistent`

#### Scenario: A discriminator belonging to a sub-union still resolves

- **WHEN** a payload's discriminator names a variant declared by a nested sub-union
- **THEN** deserialization succeeds and produces that sub-union's variant, with no exception

### Requirement: Serialized output is unchanged

The wire format SHALL be byte-identical to the format produced before this change: a discriminator field naming the variant, followed by the variant's properties.

#### Scenario: Payload variant serializes identically

- **WHEN** a variant with a payload is serialized with default options
- **THEN** the output is `{"$type":"Card","number":"4111"}`

#### Scenario: Field-less variant serializes identically

- **WHEN** a field-less variant is serialized with default options
- **THEN** the output is `{"$type":"Cash"}`

#### Scenario: Stored documents still deserialize

- **WHEN** a document written by a previous version and containing a discriminator is deserialized after this change
- **THEN** it produces the same value it produced before
