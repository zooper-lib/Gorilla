# Gorilla Nested Versioned Discriminated Union Support

## Purpose

Define the missing Zooper.Gorilla feature needed for discriminated unions that are nested inside other types, including other union declarations, and versioned as part of a containing contract.

This document is an implementation brief for an AI agent. It is not itself an implemented change.

## Problem Statement

Some consumers want to model contract-owned, version-scoped discriminated unions directly inside an owning contract.

Some consumers also want to model hierarchical unions where a broad outer union contains a more specific inner union, and that inner union is itself one of the outer union's variants.

The intended style is:

- the union belongs to the owning contract
- the union can be versioned alongside that contract
- the generated API matches the existing top-level Gorilla experience

That style works conceptually for versioned contracts, but `Zooper.Gorilla.Generators` 1.2.0 does not generate the required code for nested unions in this shape.

Observed symptoms:

- the nested variant factory methods remain unimplemented partial methods
- the generated `Match(...)` API is missing
- callers see the nested type as a plain partial shell rather than a generated union

Representative compile errors:

```text
Partial method 'IEntityCreatedContract.IEntityPayload.V1.Created()' must have an implementation part because it has accessibility modifiers.

'IEntityCreatedContract.IEntityPayload.V1' does not contain a definition for 'Match'
```

## Desired Capability

Gorilla should support `[DiscriminatedUnion]` types that are nested inside other types, including these cases:

- nested inside an interface
- nested inside a class
- nested inside a record
- nested inside another nested type
- nested inside another discriminated union declaration
- nested inside another discriminated union declaration while also acting as a variant of the outer union through inheritance

The generated code must preserve the full containing type chain, not just the namespace.

## Primary Example

The motivating example is a versioned contract where the payload itself owns a versioned discriminated union.

Desired authoring style:

```csharp
using Zooper.Gorilla.Attributes;

public interface IEntityCreatedContract
{
    public sealed record V1(
        EntityId EntityId,
        EntityName Name,
        IEntityPayload Info,
        EntityOrder Order) : IEntityCreatedContract;

    public interface IEntityPayload
    {
        [DiscriminatedUnion]
        public sealed partial class V1 : IEntityPayload
        {
            [Variant]
            public static partial V1 Created();

            [Variant]
            public static partial V1 Archived();

            [Variant]
            public static partial V1 Standard(string category, bool isVisible);

            [Variant]
            public static partial V1 Composite(string parentId, int childCount);

            [Variant]
            public static partial V1 External(string provider, string externalId);
        }
    }
}
```

This should compile and generate the same style of API that Gorilla already generates for top-level unions.

## Double Nested Union Example

Gorilla should also support a discriminated union declared inside another discriminated union declaration.

That inner union may itself be a variant of the outer union by inheriting from it, and the pattern should continue to work if the inner union contains another nested union.

Desired authoring style:

```csharp
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial record ContractOutcome
{
    [Variant]
    public static partial ContractOutcome Success(string contractId);

    [DiscriminatedUnion]
    public abstract partial record Rejected : ContractOutcome
    {
        [Variant]
        public static partial Rejected Validation(string field);

        [Variant]
        public static partial Rejected Conflict(string resourceId);

        [DiscriminatedUnion]
        public abstract partial record Security : Rejected
        {
            [Variant]
            public static partial Security Forbidden();

            [Variant]
            public static partial Security Unauthorized(string reason);
        }
    }
}
```

This should generate a usable outer union API for `ContractOutcome`, a usable nested union API for `ContractOutcome.Rejected`, and a usable double-nested union API for `ContractOutcome.Rejected.Security`.

## Current Working Baseline

Gorilla already supports top-level unions successfully.

Example that works today:

```csharp
[DiscriminatedUnion]
public sealed partial class EntityState
{
    [Variant]
    public static partial EntityState Created();

    [Variant]
    public static partial EntityState Archived();

    [Variant]
    public static partial EntityState Standard(string category, bool isVisible);

    [Variant]
    public static partial EntityState Composite(string parentId, int childCount);

    [Variant]
    public static partial EntityState External(string provider, string externalId);
}
```

The nested/versioned feature should behave like this working top-level case, but inside the containing type chain.

## Required Generated Behavior

For nested unions, Gorilla must generate all current top-level conveniences:

- implementation parts for all variant factory methods
- generated variant wrapper types if Gorilla normally emits them
- `Match(...)` and any other standard matching helpers
- equality and type checks consistent with existing Gorilla behavior
- any implicit or explicit conversions Gorilla normally emits for top-level unions

For nested unions inside other unions, Gorilla must additionally preserve the declared inheritance relationship between union levels.

That means:

- an inner union such as `ContractOutcome.Rejected` must compile and function as a union in its own right
- that same inner union must also participate correctly as a subtype and variant family of `ContractOutcome` when declared that way
- a double-nested union such as `ContractOutcome.Rejected.Security` must work both as its own union and as part of the containing union hierarchy

The generated code must use the correct fully-qualified nested type name.

For the example above, the generated target type is not a top-level type. It is:

```text
IEntityCreatedContract.IEntityPayload.V1
```

## Containing Type Semantics

The generator must preserve the exact containing structure of the annotated union.

For example, if the author writes:

```csharp
namespace Example;

public interface IOuter
{
    public interface IInnerContract
    {
        [DiscriminatedUnion]
        public sealed partial class V1 : IInnerContract
        {
            [Variant]
            public static partial V1 A();
        }
    }
}
```

then the generated code must target that same nested location, not a flattened top-level approximation.

It must work regardless of whether the containing types are:

- public or internal
- interfaces or classes
- generic or non-generic, if Gorilla otherwise supports generic containing types
- ordinary containing types or containing types that are themselves discriminated unions

When a containing type is itself a discriminated union, the generator must still preserve the exact nested declaration shape rather than flattening inner unions into sibling top-level unions or losing the declared inheritance chain.

## Versioning Expectations

The point of this feature is not just nesting. It is nesting plus versioning.

The generator must allow authors to express patterns like:

- `IContract.V1Info`
- `IContract.IInfo.V1`
- `OuterType.Payload.V2`
- `OuterUnion.InnerUnion`
- `OuterUnion.InnerUnion.V2`
- `OuterUnion.InnerUnion.DeeperUnion`

without forcing the union to be moved to a top-level type.

This matters for versioned contracts because versioned payload types should stay visually and semantically attached to the owning contract version.

## Consumer Experience

Once generated, the nested versioned union should be usable exactly like a top-level Gorilla union.

Expected call sites:

```csharp
var info = IEntityCreatedContract.IEntityPayload.V1.Standard(
    category: "alpha",
    isVisible: true);

var result = info.Match(
    created => "created",
    archived => "archived",
    standard => standard.Category,
    composite => composite.ParentId,
    external => external.Provider);
```

If Gorilla's existing top-level `Match(...)` shape uses parameterless handlers for unit variants, the nested generated API should match that exact established behavior.

For double nested unions, the consumer experience should remain consistent at each level.

Expected call sites:

```csharp
ContractOutcome outcome = ContractOutcome.Rejected.Security.Forbidden();

var outer = outcome.Match(
    success => "success",
    rejected => rejected.Match(
        validation => $"validation:{validation.Field}",
        conflict => $"conflict:{conflict.ResourceId}",
        security => security.Match(
            forbidden => "forbidden",
            unauthorized => unauthorized.Reason)));
```

The important behavior is that each generated `Match(...)` operates on the correct union level while the nested union instance still flows through outer union APIs according to its declared inheritance.

## Non-Goals

This feature request is not asking Gorilla to:

- change the public API style for existing top-level unions
- redesign how variants are declared
- introduce a different attribute model
- add manual fallback code paths to consuming applications

The goal is parity for nested versioned unions, not a second union system.

## Likely Root Cause

Based on the observed behavior in this repo, the generator appears to handle namespace-scoped union declarations but not nested containing types.

Evidence:

- top-level Gorilla unions work
- the nested event-owned union receives no generated implementation
- the package README documents only top-level examples
- quick inspection of the installed generator assembly showed a `ContainingNamespace` string but no obvious `ContainingType` handling

This is not a proven implementation detail, but it is the most likely gap the implementation agent should investigate first.

## Implementation Requirements

The implementation agent should verify and, if necessary, change the generator so that it:

1. Reads and preserves the full containing symbol chain for the annotated union type.
2. Emits generated code into the correct nested type declaration structure.
3. Generates variant methods and matching helpers for nested unions exactly as it does for top-level unions.
4. Supports nested unions declared inside interfaces, not just inside concrete outer classes.
5. Produces compilable generated code when outer and inner types reuse common version names like `V1`.
6. Avoids name collisions between an outer event record named `V1` and an inner union class also named `V1` in a different containing scope.
7. Supports a `[DiscriminatedUnion]` whose containing type is also a `[DiscriminatedUnion]`.
8. Preserves declared inheritance across nested union levels so an inner union can also serve as an outer union variant family.
9. Continues to work when nested union containment and version naming are combined, such as `OuterUnion.Payload.V1`.

## Suggested Test Matrix

The implementation should include generator tests for at least these cases:

1. Top-level union still works unchanged.
2. Union nested in a class generates factories and `Match(...)`.
3. Union nested in an interface generates factories and `Match(...)`.
4. Union nested inside a nested interface generates correctly.
5. Union type named `V1` nested under another type that also contains a `V1` record still generates correctly.
6. Variant payloads with parameters generate correctly in nested unions.
7. Unit variants and payload variants both work in the same nested union.
8. The generated nested union can implement a containing inner interface such as `IEntityPayload`.
9. A union nested directly inside another union declaration generates correctly.
10. A nested union declared as a subtype of its containing union works as both its own union and an outer union variant family.
11. A double-nested union inside an inner union also generates correctly.
12. Outer, inner, and double-nested `Match(...)` APIs all compile and dispatch correctly.
13. Versioned naming and nested-union containment can coexist without collisions.

## Acceptance Criteria

This feature is complete when all of the following are true:

- the primary example in this document compiles without manual implementation parts
- the double nested union example in this document compiles without manual implementation parts
- the nested `Created`, `Archived`, `Standard`, `Composite`, and `External` factory methods are generated
- `Match(...)` is generated for the nested versioned union
- the outer, inner, and double-nested union levels each receive their expected generated factory methods and `Match(...)` APIs
- a nested union declared inside another union can still participate correctly in the outer union hierarchy
- existing top-level Gorilla unions continue to behave exactly as before
- generator tests cover nested, double-nested, and versioned containment scenarios

## Deliverable Expectation For The Implementing Agent

The implementing agent should produce:

- the Gorilla generator changes needed for nested/versioned support
- tests covering the cases above
- a short note describing any explicit limitations that still remain after the change

If interface-nested unions remain intentionally unsupported for a technical reason, that limitation must be documented explicitly in Gorilla instead of failing through missing generated methods.