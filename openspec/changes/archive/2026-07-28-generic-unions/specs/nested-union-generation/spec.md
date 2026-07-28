## MODIFIED Requirements

### Requirement: Match method is generated for nested unions
The generator SHALL emit a `Match<TResult>(...)` method for any `[DiscriminatedUnion]` type regardless of nesting depth, with one typed handler parameter per declared variant and per nested sub-union. The result type parameter is named `TResult` for every union, generic or not, and is uniquified against every type parameter in scope — the containing types' parameters together with the union's own.

#### Scenario: Match on a nested union
- **WHEN** a `[DiscriminatedUnion]` abstract partial class nested inside an interface is used at a call site
- **THEN** `Match(...)` is callable and dispatches to the correct handler based on the active variant

#### Scenario: Match on a deeply nested union
- **WHEN** a `[DiscriminatedUnion]` abstract partial class is nested two levels deep inside containing types
- **THEN** `Match(...)` is callable on the generated type and dispatches correctly

#### Scenario: Result parameter does not shadow an enclosing parameter
- **WHEN** a union is nested inside a generic containing type, or declares type parameters of its own
- **THEN** the emitted result type parameter does not collide with any of them and the generated file compiles with no `CS0693` warning

### Requirement: Source hint is unique per nested union
The generator SHALL use the fully-qualified containing path as the `AddSource` hint, including all ancestor type names separated by dots, to prevent file-name collisions when multiple nested types share the same short name. Any path segment whose arity is greater than zero SHALL have its arity appended after an underscore — `Ns.Foo_1.g.cs` for `Foo<T>`, `Ns.Outer_1.Leaf.g.cs` for `Outer<T>.Leaf` — so that types differing only in arity do not collide. An underscore is used rather than the CLR's backtick, which is not safe in a hint name. Segments of arity zero SHALL carry no suffix.

#### Scenario: Two V1 types in different containing scopes
- **WHEN** a namespace contains both an `IContractA.V1` and an `IContractB.V1` annotated union
- **THEN** each union generates a distinct source file and both compile without collision

#### Scenario: Same name, different arity
- **WHEN** a namespace contains both `Foo` and `Foo<T>` as annotated unions
- **THEN** each generates a distinct source file and both compile, with no `CS8795` on unimplemented partial members

#### Scenario: Containing type arity disambiguates
- **WHEN** a namespace contains both `Outer.Leaf` and `Outer<T>.Leaf` as annotated unions
- **THEN** the two hints differ and both files are emitted

#### Scenario: Arity-zero hints are unchanged
- **WHEN** a union and all its containing types are non-generic
- **THEN** its source hint is byte-identical to the previous release, so no existing generated file is renamed

### Requirement: Record declarations are accepted as union types
The generator SHALL recognise `RecordDeclarationSyntax` nodes in addition to `ClassDeclarationSyntax` when scanning for `[DiscriminatedUnion]` attributes, so that unions declared as `abstract partial record` are processed.

#### Scenario: Abstract partial record union
- **WHEN** `[DiscriminatedUnion]` is applied to an `abstract partial record`
- **THEN** the generator produces factory methods and a `Match<TResult>` method for that record type

#### Scenario: Generic abstract partial record union
- **WHEN** `[DiscriminatedUnion]` is applied to an `abstract partial record` that declares type parameters
- **THEN** the generator produces factory methods and a `Match<TResult>` method carrying those type parameters through

### Requirement: Hierarchical union — abstract outer union generates Match and factory methods
The generator SHALL generate `Match<TResult>(...)`, `Switch(...)`, and variant infrastructure for every `[DiscriminatedUnion]` type using a single type-switch dispatch strategy, so that any union can serve as an inheritance base for nested sub-unions. There SHALL be no separate emit path for unions that declare no sub-unions, and no separate emit path for unions that declare type parameters.

#### Scenario: Outer union with leaf variants and a sub-union family
- **WHEN** a `[DiscriminatedUnion]` declares both `[Variant]` methods and contains a nested `[DiscriminatedUnion]` that inherits from it
- **THEN** the generated `Match<TResult>` accepts one handler for each `[Variant]` (typed to the generated `*Variant` class) and one handler for the sub-union type

#### Scenario: Factory methods on an outer union
- **WHEN** a `[DiscriminatedUnion]` declares `[Variant]` factory methods
- **THEN** each factory method is implemented to return a new `*Variant` instance (which inherits from the union type)

#### Scenario: A union with no sub-unions uses the same dispatch
- **WHEN** a `[DiscriminatedUnion]` declares only `[Variant]` methods and no nested sub-union
- **THEN** it is emitted by the same code path, with the same type-switch dispatch, as one that declares sub-unions

#### Scenario: A generic union uses the same dispatch
- **WHEN** a `[DiscriminatedUnion]` declares type parameters
- **THEN** it is emitted by the same code path as a non-generic union, with the type parameters carried through

### Requirement: Hierarchical union — inner sub-union participates in both its own and the outer union's API
The generator SHALL ensure that an inner `[DiscriminatedUnion]` declared as a subtype of an outer union can be used both as its own union (via its own `Match<TResult>`) and as a value flowing through the outer union's `Match<TResult>`.

#### Scenario: Inner union value flows through outer Match
- **WHEN** a value is created via a factory method on an inner sub-union (e.g., `ContractOutcome.Rejected.Validation(...)`)
- **THEN** it is assignable to the outer union type and the outer `Match(...)` dispatches it to the sub-union handler

#### Scenario: Inner union's own Match dispatches correctly
- **WHEN** the inner sub-union handler in an outer `Match` receives a value
- **THEN** calling `.Match(...)` on that value dispatches among the inner union's own variants

#### Scenario: Double-nested sub-union
- **WHEN** a sub-union itself contains a further nested `[DiscriminatedUnion]` that inherits from it
- **THEN** the same hierarchical rules apply recursively — each level generates its own `Match<TResult>` and factory methods

#### Scenario: Sub-union nested inside a generic union
- **WHEN** a sub-union is declared inside a generic union and derives from it as constructed with the enclosing union's own type parameters
- **THEN** it is collected as a sub-union and participates in the parent's `Match`

## ADDED Requirements

### Requirement: Sub-union membership is declared by the base clause and never inferred from nesting
A nested `[DiscriminatedUnion]` SHALL be treated as a sub-union of its enclosing union when, and only when, it declares that union as its base type. The generator SHALL NOT infer membership from nesting alone and SHALL NOT emit a base clause the user did not write.

Nesting carries two distinct meanings that only the base clause separates: a **sub-union** *is a* kind of its parent and belongs in the parent's `Match`; a **payload union** is merely *scoped inside* its owner, nested for the same reason a helper type is nested. Inferring membership from location would silently make a payload union a case of its parent, and the user's only workaround would be to stop nesting.

A sub-union SHALL derive from its **immediate** enclosing union, never a further ancestor, which is what permits several branches at one layer each with their own children.

#### Scenario: Nested union without a base clause
- **WHEN** a `[DiscriminatedUnion]` is nested inside another `[DiscriminatedUnion]` but declares no base type
- **THEN** it generates as an independent union and does not appear in the enclosing union's `Match`

#### Scenario: Nested union with a base clause
- **WHEN** a nested `[DiscriminatedUnion]` declares the enclosing union as its base type
- **THEN** it appears as a handler in the enclosing union's `Match` and its values are assignable to the enclosing union

#### Scenario: Several sub-unions at one layer
- **WHEN** an enclosing union has more than one nested sub-union, each with nested sub-unions of its own
- **THEN** each layer's `Match` mentions only its direct children

#### Scenario: Membership survives generic unions
- **WHEN** a sub-union derives from a generic enclosing union constructed with that union's own type parameters
- **THEN** it is recognised as a sub-union, because a type constructed with its own type parameters is the same symbol as its definition

### Requirement: ZGOR005 reports a nested union deriving from a different construction of its parent
The generator SHALL emit a `ZGOR005` warning when a nested type carries `[DiscriminatedUnion]`, its base type's original definition equals the enclosing union's definition, and its base type does not equal the enclosing union as constructed. That condition is reachable only by naming the enclosing union with the wrong type arguments, so it has no legitimate use.

Severity is **Warning**, consistent with `ZGOR002`: it reports generated code that will surprise the user rather than a malformed declaration. The message SHALL name both types, since they differ only in their type arguments.

The nested union SHALL still be excluded from the parent's `Match`. Comparing base types by original definition instead would include it, producing generated code that does not compile.

#### Scenario: Wrong construction on the base clause
- **WHEN** a nested `[DiscriminatedUnion]` inside `Outcome<T>` declares `: Outcome<int>` as its base
- **THEN** `ZGOR005` is reported, naming both `Outcome<int>` and `Outcome<T>`, and the nested union is absent from `Outcome<T>`'s `Match`

#### Scenario: Correctly-constructed sibling is unaffected
- **WHEN** a correctly-constructed sub-union sits beside the mis-constructed one
- **THEN** it is still collected and still participates in the parent's `Match`, and reports no diagnostic

#### Scenario: Unrelated base type is not diagnosed
- **WHEN** a nested `[DiscriminatedUnion]` derives from some union other than its enclosing type
- **THEN** no `ZGOR005` is reported, because it is a legitimate sub-union of that other type

#### Scenario: Diagnostic is declared for release tracking
- **WHEN** the analyzer project is built
- **THEN** `ZGOR005` is present in the unshipped analyzer release file and no `RS2008` build failure occurs
