## ADDED Requirements

### Requirement: A union is an abstract base type with sealed nested variant subtypes

The generator SHALL emit every `[DiscriminatedUnion]` type as an `abstract` base type whose variants are `sealed` nested types deriving from it. There SHALL be exactly one emit path: the generator SHALL NOT branch on whether a union declares sub-unions, and SHALL NOT emit a wrapper representation in which a union instance holds a separate variant instance.

#### Scenario: Variant instance is the union instance

- **WHEN** a value is produced by a variant factory method on a union
- **THEN** the value is an instance of the generated `sealed` variant type, and that type derives from the union type

#### Scenario: Type patterns work on every union

- **WHEN** a value of a union with no sub-unions is tested with `value is SomeVariant v`
- **THEN** the code compiles and the pattern matches when the value holds that variant

#### Scenario: A variant is assignable where the union is expected

- **WHEN** a variant instance is assigned to a variable, parameter, or return type declared as the union type
- **THEN** the assignment compiles with no conversion

#### Scenario: No OneOf base type is emitted

- **WHEN** any union is generated
- **THEN** the generated source contains no `OneOfBase`, no `OneOf`, and no `using OneOf;`

#### Scenario: A union may declare more than nine variants

- **WHEN** a union declares ten or more `[Variant]` factory methods
- **THEN** the generated source compiles and every variant is reachable through its factory, `Match`, and `Switch`

### Requirement: Union declarations may be classes or records

The generator SHALL accept `[DiscriminatedUnion]` on `abstract partial class` and `abstract partial record` declarations, and SHALL emit variants using the same keyword as the union they belong to.

#### Scenario: Record union emits record variants

- **WHEN** a union is declared `abstract partial record`
- **THEN** the generated variant types are declared `sealed record` and derive from the union

#### Scenario: Class union emits class variants

- **WHEN** a union is declared `abstract partial class`
- **THEN** the generated variant types are declared `sealed class` and derive from the union

### Requirement: Generated variant types carry the `Variant` suffix

For a `[Variant]` factory method named `X`, the generator SHALL emit a nested type named `XVariant`. The suffix SHALL NOT be configurable.

#### Scenario: Variant type name derives from the factory name

- **WHEN** a union declares `[Variant] public static partial Pay Card(string number);`
- **THEN** the generated nested type is named `CardVariant`

#### Scenario: User-declared sub-unions keep their declared names

- **WHEN** a union contains a nested `[DiscriminatedUnion]` sub-union named `Rejected`
- **THEN** the sub-union type is named `Rejected` with no suffix applied

### Requirement: Variant payloads are exposed as get-only properties

The generator SHALL emit each `[Variant]` factory parameter as a get-only property on the variant type, assigned from a constructor taking the same parameters in declaration order. Variants SHALL NOT be emitted as positional records.

#### Scenario: Payload is readable from the variant type

- **WHEN** a variant is created with `Pay.Card("4111")` and the value is narrowed to `CardVariant`
- **THEN** the `Number` property returns `"4111"`

#### Scenario: Record equality compares payloads

- **WHEN** two `CardVariant` values with equal payloads are compared on a `record` union
- **THEN** they compare equal and their hash codes agree

### Requirement: The union constructor is private, closing the hierarchy

The generator SHALL emit a `private` parameterless constructor on every union. Generated variants and user-declared nested sub-unions SHALL still derive from the union; no type declared outside the union SHALL be able to derive from it.

#### Scenario: Hand-written external subtype fails to compile

- **WHEN** a user declares `public sealed record Cancelled(string Reason) : Outcome;` outside the union body
- **THEN** compilation fails with `CS0122` on that declaration, reporting the union's constructor as inaccessible

#### Scenario: Generated variants and nested sub-unions still derive

- **WHEN** a union declares `[Variant]` methods and contains a nested `[DiscriminatedUnion]` sub-union
- **THEN** both the generated variant types and the sub-union compile and derive from the union

#### Scenario: Hierarchical unions no longer accept external subtypes

- **WHEN** an existing hand-written subtype of a union with sub-unions is compiled after this change
- **THEN** compilation fails at that declaration rather than throwing `InvalidOperationException` at dispatch time

### Requirement: Equality and `ToString` are delegated to the language

The generator SHALL NOT emit `Equals`, `GetHashCode`, or `ToString` for unions or variants. Equality semantics SHALL follow the declared keyword: reference equality for a `class` union, value equality for a `record` union.

#### Scenario: Class union has reference equality

- **WHEN** two separately created variant values with equal payloads on a `class` union are compared with `==`
- **THEN** the result is `false`

#### Scenario: Record union has value equality

- **WHEN** two separately created variant values with equal payloads on a `record` union are compared with `==`
- **THEN** the result is `true` and their hash codes agree

#### Scenario: `ToString` is the language default

- **WHEN** `ToString()` is called on a variant of a `record` union
- **THEN** the result is the record's synthesized form, e.g. `CardVariant { Number = 4111 }`, and the type name is not repeated

### Requirement: Struct unions are rejected

The generator SHALL NOT emit `struct` or `record struct` union types. A `[DiscriminatedUnion]` applied to a struct declaration SHALL be reported rather than generated, because variants must derive from the union and structs cannot be inherited from.

#### Scenario: Struct union is not generated

- **WHEN** `[DiscriminatedUnion]` is applied to a `partial struct` or `partial record struct`
- **THEN** the generator reports a diagnostic and emits no union source for that type

### Requirement: The OneOf dependency is absent from the package and generated code

The generator package SHALL NOT reference OneOf, and no shipped package SHALL declare OneOf as a dependency.

#### Scenario: No package reference

- **WHEN** the generator project is built
- **THEN** no `PackageReference` to `OneOf` is present and the build succeeds

#### Scenario: No nuspec dependency

- **WHEN** a package is produced
- **THEN** its dependency list contains no `OneOf` entry
