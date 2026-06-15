## Why

The Gorilla source generator only handles top-level `[DiscriminatedUnion]` declarations; unions nested inside interfaces, classes, records, or other unions receive no generated implementation, leaving partial factory methods unimplemented and `Match(...)` absent. Versioned contract patterns — where a payload type lives inside its owning contract (e.g., `IEntityCreatedContract.IEntityPayload.V1`) — require this to work.

## What Changes

- The generator reads and preserves the full containing-type chain for an annotated union (not just the namespace).
- Generated partial classes, factory implementations, and `Match(...)` are emitted inside the correct nested type scaffold.
- Unions nested directly inside another `[DiscriminatedUnion]` are each processed independently as their own full union.
- Inheritance across nested union levels is preserved — an inner union declared as a subtype of its outer union participates in both hierarchies.
- All existing top-level union behavior is unchanged.

## Capabilities

### New Capabilities

- `nested-union-generation`: Generator support for `[DiscriminatedUnion]` types declared inside other types (interfaces, classes, records, other unions), including multi-level nesting and hierarchical union inheritance chains.

### Modified Capabilities

*(none — top-level union requirements are unchanged)*

## Impact

- **Generator project** (`Zooper.Gorilla.Generators`): symbol-walking and code-emission logic needs containing-type chain support.
- **Generator tests**: new test cases for nested, double-nested, versioned-nested, and hierarchical union scenarios.
- **No public API changes** to attributes or consuming application code.
- **No breaking changes** to existing top-level union behavior.
