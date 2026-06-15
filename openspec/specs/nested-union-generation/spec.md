# nested-union-generation

## Purpose

Defines how the Gorilla source generator handles `[DiscriminatedUnion]` types that are nested inside other types and that form hierarchical (abstract base / sub-union) families. This covers preserving the containing type chain in generated output, generating variant factory methods, `Match<T>` dispatch, unique source hints, record support, the `ZGOR002` diagnostic, hierarchical (abstract) union generation, and JSON converters for both nested and hierarchical unions.

## Requirements

### Requirement: Containing type chain is preserved in generated output
The generator SHALL read the full containing type chain of an annotated union symbol and emit the generated partial class wrapped in the correct sequence of partial type declarations, matching the containing structure declared in user code.

#### Scenario: Union nested inside a single interface
- **WHEN** a `[DiscriminatedUnion]` sealed class is declared inside a single containing interface
- **THEN** the generated file declares the interface as `partial` and places the union partial class inside it

#### Scenario: Union nested inside multiple levels
- **WHEN** a `[DiscriminatedUnion]` sealed class is nested inside an interface that is itself nested inside another interface
- **THEN** the generated file declares both containing interfaces as `partial`, nested in declaration order (outermost first)

#### Scenario: Top-level union is unaffected
- **WHEN** a `[DiscriminatedUnion]` class is declared at namespace scope with no containing type
- **THEN** the generated file is identical to current behavior (namespace-wrapped only, no extra partial scaffold)

### Requirement: Variant factory methods are generated for nested unions
The generator SHALL generate implementation parts for all `[Variant]`-attributed partial factory methods on a union that is nested inside other types, using the same logic applied to top-level unions.

#### Scenario: Unit variant inside nested union
- **WHEN** a nested `[DiscriminatedUnion]` declares a `[Variant]` method with no parameters
- **THEN** the generator emits an implementation returning a new instance of the corresponding `*Variant` class

#### Scenario: Payload variant inside nested union
- **WHEN** a nested `[DiscriminatedUnion]` declares a `[Variant]` method with one or more parameters
- **THEN** the generator emits an implementation returning a new instance of the corresponding `*Variant` class, forwarding all parameters

### Requirement: Match method is generated for nested unions
The generator SHALL emit a `Match<T>(...)` method for any `[DiscriminatedUnion]` type regardless of nesting depth, with one typed handler parameter per declared variant.

#### Scenario: Match on a nested sealed-class union
- **WHEN** a `[DiscriminatedUnion]` sealed class nested inside an interface is used at a call site
- **THEN** `Match(...)` is callable and dispatches to the correct handler based on the active variant

#### Scenario: Match on a deeply nested union
- **WHEN** a `[DiscriminatedUnion]` sealed class is nested two levels deep inside containing types
- **THEN** `Match(...)` is callable on the generated type and dispatches correctly

### Requirement: Source hint is unique per nested union
The generator SHALL use the fully-qualified containing path as the `AddSource` hint, including all ancestor type names separated by dots, to prevent file-name collisions when multiple nested types share the same short name.

#### Scenario: Two V1 types in different containing scopes
- **WHEN** a namespace contains both an `IContractA.V1` and an `IContractB.V1` annotated union
- **THEN** each union generates a distinct source file and both compile without collision

### Requirement: Record declarations are accepted as union types
The generator SHALL recognise `RecordDeclarationSyntax` nodes in addition to `ClassDeclarationSyntax` when scanning for `[DiscriminatedUnion]` attributes, so that unions declared as `abstract partial record` are processed.

#### Scenario: Abstract partial record union
- **WHEN** `[DiscriminatedUnion]` is applied to an `abstract partial record`
- **THEN** the generator produces factory methods and a `Match<T>` method for that record type

### Requirement: Diagnostic is emitted when a containing type is not partial
The generator SHALL emit a `ZGOR002` warning diagnostic when any type in the containing chain of an annotated union is not declared `partial`, naming the non-partial type and the union it contains.

#### Scenario: Non-partial outer interface
- **WHEN** a `[DiscriminatedUnion]` is nested inside an interface that is not declared `partial`
- **THEN** the generator emits a `ZGOR002` warning identifying the interface by name

#### Scenario: Partial containing types produce no diagnostic
- **WHEN** all containing types in the chain are declared `partial`
- **THEN** no `ZGOR002` diagnostic is emitted

### Requirement: Hierarchical union — abstract outer union generates Match and factory methods
The generator SHALL generate a `Match<T>(...)` method and variant infrastructure for `[DiscriminatedUnion]` types declared as `abstract`, using a type-switch dispatch strategy rather than `OneOfBase`, so that the type can serve as an inheritance base for nested sub-unions.

#### Scenario: Outer abstract union with leaf variants and a sub-union family
- **WHEN** an `abstract` `[DiscriminatedUnion]` declares both `[Variant]` methods and contains a nested `[DiscriminatedUnion]` that inherits from it
- **THEN** the generated `Match<T>` accepts one handler for each `[Variant]` (typed to the generated `*Variant` class) and one handler for the sub-union type

#### Scenario: Factory methods on outer abstract union
- **WHEN** an `abstract` `[DiscriminatedUnion]` declares `[Variant]` factory methods
- **THEN** each factory method is implemented to return a new `*Variant` instance (which inherits from the abstract union type)

### Requirement: Hierarchical union — inner sub-union participates in both its own and the outer union's API
The generator SHALL ensure that an inner `[DiscriminatedUnion]` declared as a subtype of an outer union can be used both as its own union (via its own `Match<T>`) and as a value flowing through the outer union's `Match<T>`.

#### Scenario: Inner union value flows through outer Match
- **WHEN** a value is created via a factory method on an inner sub-union (e.g., `ContractOutcome.Rejected.Validation(...)`)
- **THEN** it is assignable to the outer union type and the outer `Match(...)` dispatches it to the sub-union handler

#### Scenario: Inner union's own Match dispatches correctly
- **WHEN** the inner sub-union handler in an outer `Match` receives a value
- **THEN** calling `.Match(...)` on that value dispatches among the inner union's own variants

#### Scenario: Double-nested sub-union
- **WHEN** a sub-union itself contains a further nested `[DiscriminatedUnion]` that inherits from it
- **THEN** the same hierarchical rules apply recursively — each level generates its own `Match<T>` and factory methods

### Requirement: JSON converters are generated for nested and hierarchical unions
The generator SHALL emit JSON converters for nested and abstract/hierarchical unions, using type-pattern dispatch for the abstract case and the existing `OneOfBase`-based switch for flat sealed-class unions.

#### Scenario: JSON round-trip for nested sealed-class union
- **WHEN** a nested `[DiscriminatedUnion]` sealed class is serialized and then deserialized
- **THEN** the deserialized value has the same variant and property values as the original

#### Scenario: JSON round-trip for outer abstract union — leaf variant
- **WHEN** an outer abstract union value holding a leaf variant is serialized and deserialized
- **THEN** the deserialized value holds the same leaf variant

#### Scenario: JSON round-trip for outer abstract union — sub-union value
- **WHEN** an outer abstract union value holding an inner sub-union instance is serialized and deserialized
- **THEN** the deserialized value is an instance of the correct inner sub-union type with all properties preserved

#### Scenario: Nested sub-union value serialized independently
- **WHEN** a value of an inner sub-union type is serialized using the inner type's converter
- **THEN** the result can be deserialized back to the same inner sub-union variant
