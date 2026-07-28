## ADDED Requirements

### Requirement: The System.Text.Json converter attribute is emitted on variant types
The generator SHALL emit the System.Text.Json converter attribute above **each generated variant class** in addition to the union declaration, for every union whether or not it declares type parameters. Without it, a value written by its runtime type — which is the variant, not the union — is serialized by System.Text.Json's default logic and loses its discriminator.

The Newtonsoft converter attribute SHALL NOT be emitted on variant classes. Newtonsoft honours the attribute inherited from the base type, and duplicating it would cause the shim to resolve `objectType` as the variant and return a union instance where a variant instance is expected.

#### Scenario: Serialize a non-generic union through the variant's static type
- **WHEN** a value whose static type is `Plain.VisibleVariant` is serialized with System.Text.Json
- **THEN** the output carries the discriminator, as `{"$type":"Visible","value":"hello"}`

#### Scenario: Serialize a non-generic union as object
- **WHEN** a union value is serialized through a static type of `object`
- **THEN** the output carries the discriminator

#### Scenario: Payload-free variant keeps its discriminator
- **WHEN** a payload-free variant is serialized by its runtime type
- **THEN** the output is `{"$type":"NotProvided"}` rather than `{}`

#### Scenario: Newtonsoft attribute is absent from variants
- **WHEN** a union's generated output is inspected
- **THEN** the Newtonsoft converter attribute appears on the union declaration only, never above a variant class

### Requirement: Converters claim the whole union hierarchy
A generated converter SHALL override `CanConvert` so that it accepts any type assignable to the union (`typeof(Plain).IsAssignableFrom(t)`). `JsonConverter<TBase>` otherwise refuses a derived `typeToConvert`, and emitting the attribute on variants without this override throws at the first write.

For a generic union the same claim SHALL be made in both places: on the converter itself, and on the registration entry point.

#### Scenario: Converter accepts a derived variant type
- **WHEN** System.Text.Json asks a generated converter whether it can convert a generated variant type
- **THEN** it answers true, and serialization proceeds through the converter

#### Scenario: Attribute without CanConvert is not shipped
- **WHEN** a value whose static type is a variant is serialized
- **THEN** no `InvalidOperationException` reporting an incompatible converter is thrown

### Requirement: A generic union registers its System.Text.Json converter through a non-generic factory
Because `JsonConverterAttribute` instantiates the named type via `Activator.CreateInstance`, and an open generic type has no instance, the generator SHALL name a non-generic `JsonConverterFactory` in the attribute for a union that declares type parameters. The factory SHALL close the generic converter over the union's type arguments via `MakeGenericType`.

The factory's `CanConvert` and `CreateConverter` SHALL walk the candidate type's base types to the closed union, so that a variant type resolves to its union's type arguments. A direct generic-type-definition comparison is insufficient: a type nested in a generic type is itself generic, and `Disclosure<>.VisibleVariant` is not `Disclosure<>`.

#### Scenario: Round-trip a closed generic union
- **WHEN** `Disclosure<string>.Visible("hello")` is serialized and deserialized with System.Text.Json
- **THEN** the JSON is `{"$type":"Visible","value":"hello"}` and the deserialized value equals the original

#### Scenario: Round-trip a different closed construction
- **WHEN** `Disclosure<int>.Visible(42)` is serialized
- **THEN** the JSON is `{"$type":"Visible","value":42}` and it deserializes back to an equal value

#### Scenario: Serialize a generic union through the variant's static type
- **WHEN** a value whose static type is `Disclosure<string>.VisibleVariant` is serialized
- **THEN** the factory resolves the closed union from the variant's base types and the output carries the discriminator

#### Scenario: Naive open-generic registration is not emitted
- **WHEN** a generic union's generated attribute is inspected
- **THEN** it names the non-generic factory, not an open generic converter, so no `ArgumentException` about `ContainsGenericParameters` can be thrown at first serialization

### Requirement: A generic union registers its Newtonsoft converter through a non-generic delegating shim
The generator SHALL emit a non-generic `Newtonsoft.Json.JsonConverter` shim for a union that declares type parameters. The shim SHALL delegate `ReadJson` and `WriteJson` through the non-generic overrides of the same generic converter body the generator already emits, closing it over the type arguments of the read path's declared type and the write path's runtime type. No reflection over variant names or payload types SHALL be used.

The shim SHALL cache the closed converter instance per type. Newtonsoft caches the shim instance on the contract but re-enters `ReadJson` per value, so without the cache every deserialized value pays a `MakeGenericType` and an `Activator.CreateInstance`.

The shim's `CanConvert` SHALL walk base types by the same rule as the System.Text.Json factory. Newtonsoft skips `CanConvert` when the converter arrives via the attribute, but consults it when the user registers the converter manually, where a direct generic-type-definition comparison declines every variant and silently drops the union to default property serialization with no discriminator.

#### Scenario: Round-trip a closed generic union under Newtonsoft
- **WHEN** `Disclosure<string>.Visible("hello")` and `Disclosure<int>.Visible(42)` are serialized and deserialized with Newtonsoft
- **THEN** the JSON is identical to the System.Text.Json output and both deserialize back to equal values

#### Scenario: Manual registration serializing a variant
- **WHEN** the shim is registered via `settings.Converters.Add(...)` rather than the attribute, and a value whose static type is a variant is serialized
- **THEN** `CanConvert` accepts it and the output carries the discriminator

#### Scenario: One converter implementation, not two
- **WHEN** the generated output for a generic union is inspected
- **THEN** the shim forwards to the same generic converter body used for the attribute path, with no second implementation of the read/write logic

### Requirement: A converter is emitted at the innermost enclosing scope that has no type parameters
The generator SHALL emit a union's converters at the innermost enclosing scope that declares no type parameters.

When no containing type is generic, that scope is the union's own, and the converter's position and name SHALL be exactly what is emitted today. When some containing type is generic, the converter SHALL be emitted outside the **outermost** generic container — at namespace level if that container is outermost — SHALL absorb the type parameters of every container it skipped, outermost first followed by the union's own, SHALL repeat those containers' constraint clauses, and SHALL be named with the skipped containers' names as a prefix.

An attribute argument cannot use a type parameter, so a converter emitted inside a generic container does not compile (`CS0416`). This applies to a non-generic union nested in a generic container as much as to a generic one.

#### Scenario: Non-generic union in a generic container
- **WHEN** a non-generic union `Leaf` is declared inside `OuterA<TOuter>`
- **THEN** the converter is emitted outside `OuterA<TOuter>`, the attribute argument names it without type parameters, and the file compiles with no `CS0416`

#### Scenario: Generic union in a generic container
- **WHEN** `Outer<TOuter>.Leaf<T>` is declared with constraints on both parameters
- **THEN** the converter is emitted as `Outer_LeafJsonConverter<TOuter, T>` with both constraint clauses repeated, and values round-trip under both serializers

#### Scenario: Type argument order matches MakeGenericType
- **WHEN** a hoisted converter is closed over the arguments of `Outer<int>.Leaf<string>`
- **THEN** the containers' arguments precede the union's own, matching the order the runtime reports for the closed type

#### Scenario: No container is generic — output is unchanged
- **WHEN** a union whose containing types are all non-generic is generated
- **THEN** the converter's emitted position and name are byte-identical to the previous release, so existing manual registrations such as `settings.Converters.Add(new Outer.LeafJsonConverter())` keep compiling

### Requirement: Converters repeat the constraint clauses of the type parameters they declare
Because a generated converter is a **separate** generic type from the union, it SHALL repeat the constraint clauses of every type parameter it declares — the union's own and any absorbed from skipped containers. The union's own generated partial declaration may omit them, since a partial declaration inherits constraints from the part that declares them, but a converter may not.

#### Scenario: notnull constraint on the converter
- **WHEN** a union declares `where T : notnull`
- **THEN** the generated converter declares the same clause and compiles

#### Scenario: class constraint on the converter
- **WHEN** a union declares `where T : class`
- **THEN** the generated converter declares the same clause and compiles

#### Scenario: Compound constraint on the converter
- **WHEN** a union declares `where T : IComparable<T>, new()`
- **THEN** the generated converter declares the fully-qualified clause in language-legal order and compiles

#### Scenario: Constraint assertions target the converter
- **WHEN** constraint round-trip coverage is written
- **THEN** it asserts on the converter's clause, because a wrong rendering order leaves the union compiling and only the converter failing

### Requirement: Runtime reflection sites carry a Native AOT suppression
The generator SHALL wrap the emitted `MakeGenericType` call sites — the System.Text.Json factory's `CreateConverter` and the Newtonsoft shim's converter resolution — in `#pragma warning disable IL3050` / `restore`, with a comment naming the reason.

The set of closed constructions is not knowable at generation time, so the reflection cannot be removed. The warning is suppressed rather than surfaced because it lands in a generated file: a consumer with `TreatWarningsAsErrors` and one generic union would otherwise get a hard build failure at a location they cannot edit, with no action available to them.

#### Scenario: AOT build of a project with a generic union
- **WHEN** a project containing a generic union is built with `PublishAot` enabled
- **THEN** no `IL3050` warning originates from the generated file, and no `CS1691` is raised for an unrecognised warning id

#### Scenario: Pragma is present at both sites
- **WHEN** the generated output for a generic union is inspected
- **THEN** both reflection sites are enclosed by the disable/restore pair

#### Scenario: Constraint is documented
- **WHEN** a user consults the documentation for Native AOT
- **THEN** it states that a generic union's converter is constructed at runtime and the closed union types must be rooted
