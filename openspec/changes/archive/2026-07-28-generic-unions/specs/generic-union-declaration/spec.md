## ADDED Requirements

### Requirement: Union types may declare type parameters
The generator SHALL accept a `[DiscriminatedUnion]` type that declares one or more type parameters and emit the same shape it emits for a non-generic union, with the type parameters carried through. The existing declaration requirements are unchanged: the union MUST still be declared `abstract partial` (`ZGOR003`), and variants are still emitted as nested types deriving from it. Because a nested type inherits its enclosing type's parameters, generated variant classes SHALL NOT declare a type parameter list or constraint clauses of their own.

#### Scenario: Single type parameter
- **WHEN** `[DiscriminatedUnion] public abstract partial record Disclosure<T>` declares `[Variant]` factories `Visible(T value)`, `NotProvided()`, and `Redacted()`
- **THEN** the generated output compiles and `Disclosure<string>.Visible("x")`, `Disclosure<string>.NotProvided()`, and `Disclosure<string>.Redacted()` are callable

#### Scenario: Two type parameters preserve argument order
- **WHEN** a union declares `Result<TValue, TError>` with variants carrying `TValue` and `TError` respectively
- **THEN** the generated factories, `Match`, `Switch`, and accessors all use the declared parameters in the declared order

#### Scenario: Variant class carries no type parameter list
- **WHEN** a generic union's variant classes are generated
- **THEN** each is emitted as a nested type with no type parameter list and no constraint clause, and is referenced from outside as `Disclosure<string>.VisibleVariant`

#### Scenario: Payload is the type parameter or a construction over it
- **WHEN** a generic union declares one variant whose payload is `T` and another whose payload is `IReadOnlyList<T>`
- **THEN** both variants generate correctly and their accessors return the declared payload types

#### Scenario: Match, Switch, and accessors on a generic union
- **WHEN** a value of a closed generic union is dispatched through `Match`, through `Switch`, and through its generated variant accessors
- **THEN** each dispatches to the correct variant and yields the correct payload

### Requirement: Type parameters and constraint clauses are captured as plain strings
`UnionModel` SHALL carry the union's type parameter names and its constraint clauses as pre-rendered plain strings, with no `ITypeSymbol` retained, so incremental caching is preserved. Constraint clauses SHALL be rendered from `ITypeParameterSymbol` with constraint types written via `ToDisplayString()`, matching the convention already used for `VariantParameter.Type`, so the clause is fully qualified and resolves in a generated file regardless of the user's `using` directives and regardless of the scope a converter is emitted into.

Clause parts SHALL be rendered in the order the language requires: the primary constraint first (`class`, `class?`, `struct`, `unmanaged`, `notnull`, or a base type), then interface constraints, then `new()` last.

#### Scenario: Interface constraint is fully qualified
- **WHEN** a user declares `Cache<T> where T : IEqualityComparer<T>, new()` in a file with `using System.Collections.Generic;`
- **THEN** the captured clause is `where T : System.Collections.Generic.IEqualityComparer<T>, new()` and compiles in a generated file whose using block is `using System;` alone

#### Scenario: Constraint ordering is language-legal
- **WHEN** a type parameter declares a primary constraint, an interface constraint, and `new()` together
- **THEN** the rendered clause orders them primary, interfaces, `new()`, and compiles

#### Scenario: Constraint referencing its own type parameter
- **WHEN** a union declares `where T : IComparable<T>`
- **THEN** the clause round-trips unchanged, because no type parameter is renamed anywhere in generated output

#### Scenario: Model equality is unaffected by symbols
- **WHEN** two compilations produce the same generic union declaration
- **THEN** the `UnionModel` instances compare equal, holding only strings and `EquatableArray` values

### Requirement: Emitted self-references carry the type argument list
Every place the emitter writes the union's name as a **type** SHALL append the union's type argument list — the type declaration prefix, the variant base type, factory return types, and nested type references (`Disclosure<T>.VisibleVariant`). The one place the name is an **identifier** and MUST remain bare is the generated private constructor (`private Disclosure()`).

#### Scenario: Factory return type is the closed self type
- **WHEN** a generic union's factory implementations are emitted
- **THEN** each returns `Disclosure<T>`, not `Disclosure`

#### Scenario: Private constructor uses the bare name
- **WHEN** the generated private constructor is emitted for `Disclosure<T>`
- **THEN** it is `private Disclosure()` and the file compiles

#### Scenario: Nested type reference carries type arguments
- **WHEN** the emitter references a variant class as a type inside `Match` or a converter
- **THEN** it writes `Disclosure<T>.VisibleVariant`

### Requirement: Generic containing types are reopened with their type parameters
The generator SHALL record each containing type's type parameter names, arity, and constraint clauses, and SHALL reopen a generic containing type with its type parameter list so the generated partial declares the same type the user declared.

#### Scenario: Union nested in a generic containing type
- **WHEN** a `[DiscriminatedUnion]` is declared inside `public partial class Outer<TKey>`
- **THEN** the generated file reopens it as `partial class Outer<TKey>` and compiles, rather than declaring a distinct non-generic `Outer`

#### Scenario: Generic union nested in a generic containing type
- **WHEN** `Outer<TOuter>.Leaf<T>` is declared, each with constraints
- **THEN** the generated output compiles and values of `Outer<int>.Leaf<string>` round-trip under both serializers

### Requirement: The Match result type parameter is named TResult in all generated output
The generator SHALL name the `Match` result type parameter `TResult` for every union, whether or not the union declares type parameters, producing one emitted shape. If `TResult` is already in scope, the generator SHALL suffix-uniquify it (`TResult1`, `TResult2`, …). The set of taken names SHALL comprise every type parameter in scope — the containing types' parameters together with the union's own — not only the union's own.

`Switch` is unaffected; it has no type parameter.

#### Scenario: Non-generic union uses TResult
- **WHEN** a union with no type parameters is generated
- **THEN** the emitted signature is `public TResult Match<TResult>(`

#### Scenario: Generic union does not shadow its own parameter
- **WHEN** `Box<T>` is generated
- **THEN** `Match` declares `TResult`, and the generated file compiles with no `CS0693` warning

#### Scenario: TResult is taken by the union's own parameter
- **WHEN** a union declares a type parameter literally named `TResult`
- **THEN** the result parameter is uniquified to `TResult1` and the file compiles warning-clean

#### Scenario: TResult is taken by a containing type's parameter
- **WHEN** a union is nested inside `Outer<TResult>`
- **THEN** the result parameter is uniquified and the file compiles with no `CS0693` warning

#### Scenario: Call sites are unchanged
- **WHEN** an existing call site passes the result type positionally, as `value.Match<string>(...)`
- **THEN** it compiles unchanged against the renamed parameter

### Requirement: A union names itself in display form in diagnostics and in bare form in runtime messages
Compile-time diagnostics SHALL name the union in display form, including its type arguments as the user wrote them (`Disclosure<T>`, `Outcome<int>`), so that a generic and a non-generic union of the same name are distinguishable. Message strings emitted **into** generated code SHALL keep the bare name, because the closed type is not knowable when the string is generated and `GetType().Name` already reports the value's actual type at runtime.

#### Scenario: ZGOR003 on a generic union
- **WHEN** `Disclosure<T>` carries `[DiscriminatedUnion]` but is not declared `abstract`
- **THEN** the diagnostic message names it `Disclosure<T>`, not `Disclosure`

#### Scenario: Both arities present
- **WHEN** `Foo` and `Foo<T>` are both declared in one namespace and one produces a diagnostic
- **THEN** the message identifies which of the two it refers to

#### Scenario: Runtime accessor failure message
- **WHEN** an accessor on a `Disclosure<string>` value is called for the wrong variant
- **THEN** the thrown message names the union as `Disclosure` and reports the actual type via `GetType().Name`, never a literal `T`

### Requirement: No companion static factory class is emitted for a generic union
The generator SHALL NOT emit a non-generic companion class of static factory methods beside a generic union. Call sites name the closed type (`Disclosure<string>.Visible("x")`).

#### Scenario: Only the union type is generated
- **WHEN** `Disclosure<T>` is generated
- **THEN** no non-generic `Disclosure` type appears in the generated output

#### Scenario: A user's own non-generic union of the same name still compiles
- **WHEN** a project declares both a non-generic `Disclosure` union and a generic `Disclosure<T>` union in one namespace
- **THEN** both generate and the project compiles with no `CS0101`

### Requirement: No variance support is emitted
The generator SHALL NOT emit a covariant interface, a mapping method, or any conversion intended to make `Disclosure<string>` usable where `Disclosure<object>` is expected. A variance modifier is not available on a union type by construction — `CS1960` restricts variant type parameters to interfaces and delegates, and Gorilla unions must be classes or records.

#### Scenario: No extra interface is generated
- **WHEN** a generic union is generated
- **THEN** the output contains no generated covariant interface and no `Select`-style mapping method

#### Scenario: A user may add their own interface
- **WHEN** a user declares their own covariant interface and adds it to their own `partial` declaration of the union
- **THEN** the generated part combines with it and the project compiles

### Requirement: Generated output for generic unions compiles warning-clean
Generated source for a generic union, its variants, and its converters SHALL produce no compiler warnings, so that a consumer building with `TreatWarningsAsErrors` is not broken by a file they cannot edit.

#### Scenario: Warning-clean build of a generic union
- **WHEN** a generic union with constraints, a generic containing type, and JSON converters is compiled with warnings treated as errors
- **THEN** the build succeeds
