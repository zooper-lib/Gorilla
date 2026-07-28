## MODIFIED Requirements

### Requirement: Containing type chain is preserved in generated output
The generator SHALL read the full containing type chain of an annotated union symbol and emit the generated partial type wrapped in the correct sequence of partial type declarations, matching the containing structure declared in user code.

#### Scenario: Union nested inside a single interface
- **WHEN** a `[DiscriminatedUnion]` abstract partial class is declared inside a single containing interface
- **THEN** the generated file declares the interface as `partial` and places the union partial class inside it

#### Scenario: Union nested inside multiple levels
- **WHEN** a `[DiscriminatedUnion]` abstract partial class is nested inside an interface that is itself nested inside another interface
- **THEN** the generated file declares both containing interfaces as `partial`, nested in declaration order (outermost first)

#### Scenario: Top-level union is unaffected
- **WHEN** a `[DiscriminatedUnion]` type is declared at namespace scope with no containing type
- **THEN** the generated file is namespace-wrapped only, with no extra partial scaffold

### Requirement: Match method is generated for nested unions
The generator SHALL emit a `Match<T>(...)` method for any `[DiscriminatedUnion]` type regardless of nesting depth, with one typed handler parameter per declared variant and per nested sub-union.

#### Scenario: Match on a nested union
- **WHEN** a `[DiscriminatedUnion]` abstract partial class nested inside an interface is used at a call site
- **THEN** `Match(...)` is callable and dispatches to the correct handler based on the active variant

#### Scenario: Match on a deeply nested union
- **WHEN** a `[DiscriminatedUnion]` abstract partial class is nested two levels deep inside containing types
- **THEN** `Match(...)` is callable on the generated type and dispatches correctly

### Requirement: Hierarchical union — abstract outer union generates Match and factory methods
The generator SHALL generate `Match<T>(...)`, `Switch(...)`, and variant infrastructure for every `[DiscriminatedUnion]` type using a single type-switch dispatch strategy, so that any union can serve as an inheritance base for nested sub-unions. There SHALL be no separate emit path for unions that declare no sub-unions.

#### Scenario: Outer union with leaf variants and a sub-union family
- **WHEN** a `[DiscriminatedUnion]` declares both `[Variant]` methods and contains a nested `[DiscriminatedUnion]` that inherits from it
- **THEN** the generated `Match<T>` accepts one handler for each `[Variant]` (typed to the generated `*Variant` class) and one handler for the sub-union type

#### Scenario: Factory methods on an outer union
- **WHEN** a `[DiscriminatedUnion]` declares `[Variant]` factory methods
- **THEN** each factory method is implemented to return a new `*Variant` instance (which inherits from the union type)

#### Scenario: A union with no sub-unions uses the same dispatch
- **WHEN** a `[DiscriminatedUnion]` declares only `[Variant]` methods and no nested sub-union
- **THEN** it is emitted by the same code path, with the same type-switch dispatch, as one that declares sub-unions

### Requirement: JSON converters are generated for nested and hierarchical unions
The generator SHALL emit JSON converters for all unions using a single type-pattern dispatch design. Reading SHALL resolve the variant from the discriminator field, falling back to the nested sub-unions in turn; writing SHALL type-switch on the value.

#### Scenario: JSON round-trip for a nested union
- **WHEN** a nested `[DiscriminatedUnion]` abstract partial class value is serialized and then deserialized
- **THEN** the deserialized value has the same variant and property values as the original

#### Scenario: JSON round-trip for an outer union — leaf variant
- **WHEN** an outer union value holding a leaf variant is serialized and deserialized
- **THEN** the deserialized value holds the same leaf variant

#### Scenario: JSON round-trip for an outer union — sub-union value
- **WHEN** an outer union value holding an inner sub-union instance is serialized and deserialized
- **THEN** the deserialized value is an instance of the correct inner sub-union type with all properties preserved

#### Scenario: Nested sub-union value serialized independently
- **WHEN** a value of an inner sub-union type is serialized using the inner type's converter
- **THEN** the result can be deserialized back to the same inner sub-union variant

#### Scenario: Converters share one implementation shape
- **WHEN** converters are generated for a union with sub-unions and for a union without
- **THEN** both are produced by the same emit code, with no `OneOfBase`-based variant present
