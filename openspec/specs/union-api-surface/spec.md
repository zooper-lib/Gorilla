# union-api-surface

## Purpose

Defines the members a generated union exposes and what each guarantees: `Match` and `Switch` for exhaustive dispatch over variants and sub-unions, and the variant-named `IsX` / `AsX()` / `TryPickX(out …)` accessors for narrowing. It also records what is deliberately absent — positional accessors, `Value`, `Index` — and that exhaustiveness is promised by `Match` and `Switch` alone.

The result type parameter of `Match` is named `TResult`; see the `generic-union-declaration` capability for the uniquification rule that applies when that name is already in scope.

## Requirements

### Requirement: `Match` is emitted for every union over variants and sub-unions

The generator SHALL emit `Match<TResult>(…)` on every union, taking one required handler parameter per generated variant and per nested sub-union, in declaration order. Each handler parameter SHALL be named after the variant or sub-union it handles, and typed by that subtype.

#### Scenario: One handler per variant

- **WHEN** a union declares three `[Variant]` methods
- **THEN** `Match<TResult>` takes exactly three handler parameters, named after those variants and typed `Func<XVariant, TResult>`

#### Scenario: Sub-unions get their own handler

- **WHEN** a union declares variants and contains a nested `[DiscriminatedUnion]` sub-union
- **THEN** `Match<TResult>` takes a handler per variant plus one handler typed by the sub-union, and a value created through the sub-union dispatches to that handler

#### Scenario: Adding a variant breaks call sites

- **WHEN** a `[Variant]` method is added to a union that has existing `Match` call sites
- **THEN** each call site fails to compile with `CS7036`, naming the parameter for the variant that was not handled

#### Scenario: Reordering variants does not silently remap handlers

- **WHEN** two `[Variant]` declarations are swapped and call sites pass handlers by name
- **THEN** the handlers continue to target the same variants

### Requirement: `Switch` is emitted for every union

The generator SHALL emit `Switch(…)` on every union, with the same parameter set as `Match` but `Action<…>` handlers and no return value. `Switch` SHALL cover variants and sub-unions alike.

#### Scenario: Switch is available on a union with sub-unions

- **WHEN** `Switch(…)` is called on a union that declares nested sub-unions
- **THEN** the call compiles and dispatches to the handler for the active subtype

#### Scenario: Switch is available on a union without sub-unions

- **WHEN** `Switch(…)` is called on a union whose subtypes are all generated variants
- **THEN** the call compiles and dispatches to the handler for the active variant

#### Scenario: Adding a variant breaks Switch call sites

- **WHEN** a `[Variant]` method is added to a union that has existing `Switch` call sites
- **THEN** each call site fails to compile, naming the unhandled case

### Requirement: `IsX` is emitted as a property for each variant and sub-union

For each generated variant and each nested sub-union, the generator SHALL emit a `bool` property named `Is<Name>` that returns whether the value is of that subtype. `IsX` SHALL NOT throw.

#### Scenario: Is returns true for the active variant

- **WHEN** `IsCard` is read on a value created by the `Card` factory
- **THEN** it returns `true`

#### Scenario: Is returns false for other variants

- **WHEN** `IsCash` is read on a value created by the `Card` factory
- **THEN** it returns `false` and no exception is thrown

#### Scenario: Is covers sub-unions

- **WHEN** a union declares a variant `Success` and a sub-union `Rejected`
- **THEN** both `IsSuccess` and `IsRejected` are emitted, and `IsRejected` returns `true` for any value created through the sub-union

### Requirement: `AsX` is emitted as a method, never a property

For each generated variant and each nested sub-union, the generator SHALL emit a method named `As<Name>()` returning that subtype. `AsX` SHALL NOT be emitted as a property, so that reflection-based property walkers never invoke it.

#### Scenario: As returns the narrowed value

- **WHEN** `AsCard()` is called on a value created by the `Card` factory
- **THEN** it returns the value typed as `CardVariant`

#### Scenario: As throws on the wrong variant

- **WHEN** `AsCash()` is called on a value created by the `Card` factory
- **THEN** it throws `InvalidOperationException` naming the requested and actual variants

#### Scenario: As is invisible to property walkers

- **WHEN** the public properties of a union type are enumerated by reflection and every getter is invoked
- **THEN** no exception is thrown, because the only properties present are `IsX` booleans

### Requirement: `TryPickX` is emitted as a method binding the variant alone

For each generated variant and each nested sub-union, the generator SHALL emit `bool TryPick<Name>(out <Subtype> value)`. It SHALL NOT take a remainder parameter.

#### Scenario: TryPick succeeds for the active variant

- **WHEN** `TryPickCard(out var card)` is called on a value created by the `Card` factory
- **THEN** it returns `true` and `card` is the narrowed value

#### Scenario: TryPick fails without throwing

- **WHEN** `TryPickCash(out var cash)` is called on a value created by the `Card` factory
- **THEN** it returns `false`, `cash` is `null`, and no exception is thrown

### Requirement: Positional accessors, `Value`, and `Index` are not emitted

The generator SHALL NOT emit `IsT0`/`AsT0`/`TryPickT0`-style positional members, nor a `Value` member, nor an `Index` member. Union members SHALL be named after variants only.

#### Scenario: No positional members exist

- **WHEN** the public members of a generated union are enumerated
- **THEN** none is named `IsT0`, `AsT0`, `TryPickT0`, `Value`, or `Index`

### Requirement: Exhaustiveness is guaranteed only by `Match` and `Switch`

`Match` and `Switch` SHALL be the only constructs the generator guarantees to be exhaustive. Type patterns and the `IsX`/`AsX`/`TryPickX` accessors SHALL carry no exhaustiveness guarantee, and the generator SHALL NOT emit an analyzer to give them one.

#### Scenario: Accessors do not break when a variant is added

- **WHEN** a `[Variant]` method is added to a union that has `if (x.IsA) … else …` call sites
- **THEN** those call sites continue to compile

#### Scenario: Match still breaks when a variant is added

- **WHEN** the same variant is added
- **THEN** every `Match` and `Switch` call site fails to compile

### Requirement: Generated accessor names collide with conflicting user members

Because accessor names derive from variant names, a union declaring both a variant `X` and its own member named `IsX`, `AsX`, or `TryPickX` SHALL fail to compile.

#### Scenario: Conflicting member is a compile error

- **WHEN** a union declares a variant `Card` and also declares its own member named `IsCard`
- **THEN** compilation fails with `CS0102` naming the duplicate member
