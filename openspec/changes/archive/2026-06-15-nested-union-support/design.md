## Context

`DiscriminatedUnionGenerator` uses Roslyn's incremental API. The current pipeline:

1. `ForAttributeWithMetadataName` finds `[DiscriminatedUnion]` on `ClassDeclarationSyntax` nodes only.
2. `CreateUnionModel` extracts `Namespace` and `ClassName` from the symbol, discarding the containing type chain.
3. `GenerateSource` wraps the generated class directly inside `namespace { ... }`.
4. `AddSource` uses just `ClassName` as the hint.

Three gaps cause the reported compile errors:

- **Predicate gap**: `RecordDeclarationSyntax` nodes are excluded, so `abstract partial record` unions are never found.
- **Model gap**: `UnionModel` has no `ContainingTypes` field, so nested class information is thrown away.
- **Emission gap**: `GenerateSource` cannot emit the partial containing-type scaffold needed by nested unions.

## Goals / Non-Goals

**Goals:**
- Extend `UnionModel` to carry the full containing type chain.
- Extend the syntax predicate to include record declarations.
- Wrap generated code in the correct partial containing-type scaffold.
- Use unique source hints to prevent file-name collisions between same-named nested types.
- Support hierarchical unions where an inner `[DiscriminatedUnion]` is declared as a subtype of its outer union (using a type-switch `Match` strategy instead of `OneOfBase`).

**Non-Goals:**
- Change generated API shape or behavior for existing sealed-class top-level unions.
- Support generic type parameters on containing types (out of scope for this change).
- Move union declarations out of their containing type or flatten the hierarchy.
- Modify the attribute model or how `[Variant]` is authored.

## Decisions

### 1. Extend `UnionModel` with a containing type chain

Add `ContainingTypes: EquatableArray<ContainingTypeInfo>` to `UnionModel`, ordered outermost-first. Each entry carries enough information to re-declare the containing type as `partial`:

```csharp
internal readonly record struct ContainingTypeInfo(
    string Name,
    string Keyword,       // "class" | "interface" | "record" | "record struct"
    string AccessModifier // "public" | "internal" | "protected internal" | etc.
);
```

Extracted in `CreateUnionModel` by walking `classSymbol.ContainingType` upward until `ContainingType` is null (namespace boundary reached).

Keyword derivation from `INamedTypeSymbol`: `TypeKind.Interface` → `"interface"`, `TypeKind.Class` + `IsRecord` → `"record"`, `TypeKind.Class` → `"class"`, `TypeKind.Struct` + `IsRecord` → `"record struct"`.

**Alternative considered**: Store a raw containing-type declaration string. Rejected because it's harder to keep equatable and harder to unit-test.

### 2. Accept `RecordDeclarationSyntax` in the predicate

Change:
```csharp
predicate: static (node, _) => node is ClassDeclarationSyntax,
```
to:
```csharp
predicate: static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
```

This is necessary for `abstract partial record ContractOutcome` and its nested union subtypes.

### 3. Two generation strategies, chosen per union

| Condition | Strategy |
|-----------|----------|
| `sealed class` (existing behavior) | `OneOfBase<...>` wrapping (no change) |
| `abstract class` or `abstract record` | Type-switch `Match` with inheritance |

The `UnionModel` gains an `IsAbstract: bool` field. `GenerateSource` dispatches to `GenerateOneOfSource` (existing) or `GenerateHierarchicalSource` (new) based on this flag.

**Why two strategies**: `OneOfBase` requires exactly the type parameters listed at construction time. An `abstract` union type that sub-unions inherit from cannot also inherit from a second `OneOfBase` instantiation — C# does not allow multiple concrete base classes. The type-switch strategy avoids this constraint by relying on `is`-patterns.

**Alternative considered**: A single strategy using a common interface. Rejected because it would require changing the existing top-level union API, which is a breaking change.

### 4. Hierarchical union: sub-union families as match cases

For the outer `abstract` union, the generator must combine two sources of match cases:

- `[Variant]` methods → generate `*Variant` nested classes that inherit from the union type (same as today for sealed unions, but the variant class inherits rather than wrapping a `OneOf`).
- Nested `[DiscriminatedUnion]` types whose base type is the current union → included directly as a typed case, not wrapped.

`UnionModel` gains `SubUnions: EquatableArray<string>` — short names of direct nested sub-unions that inherit from this type. These are collected in `CreateUnionModel` by scanning `classSymbol.GetTypeMembers()`.

Generated `Match<T>` for the outer union:

```csharp
public T Match<T>(
    Func<SuccessVariant, T> success,
    Func<Rejected, T> rejected) =>
    this switch
    {
        SuccessVariant v => success(v),
        Rejected r => rejected(r),
        _ => throw new InvalidOperationException($"Unknown variant: {GetType().Name}")
    };
```

Each `*Variant` class and each sub-union type inherits from the outer union and seals its own constructor to prevent external instantiation:

```csharp
public class SuccessVariant : ContractOutcome
{
    internal SuccessVariant(string contractId) { ContractId = contractId; }
    public string ContractId { get; }
}
```

The outer union's private constructor (in the generated partial) prevents direct instantiation of the abstract type outside the hierarchy:

```csharp
private ContractOutcome() { }
```

### 5. Unique source hints for nested types

Replace `$"{union.ClassName}.g.cs"` with the full dotted containing path:

```csharp
var hint = union.ContainingTypes.Count > 0
    ? string.Join(".", union.ContainingTypes.Select(t => t.Name)) + "." + union.ClassName + ".g.cs"
    : union.ClassName + ".g.cs";
```

This prevents collisions between `IEntityCreatedContract.IEntityPayload.V1` and any other type named `V1` in a different containing scope.

### 6. Nested partial scaffold in code emission

`GenerateSource` (and the new `GenerateHierarchicalSource`) wrap the inner generated body in one `partial <keyword> <name>` declaration per containing type:

```csharp
// outermost containing type first
sb.AppendLine($"    public partial interface IOuter");
sb.AppendLine("    {");
sb.AppendLine($"        public partial interface IInner");
sb.AppendLine("        {");
// ... generated union body at correct indent depth ...
sb.AppendLine("        }");
sb.AppendLine("    }");
```

Indentation depth increases by one level per containing type. The union body generators receive an `int indentLevel` parameter.

**Alternative considered**: Emit a separate file per containing type re-declaration. Rejected as unnecessarily complex — a single file with nested partials is the standard Roslyn incremental generator pattern.

### 7. Diagnostic warning for non-partial containing types

When `CreateUnionModel` walks the containing type chain, it checks each `INamedTypeSymbol` for the `partial` modifier. If any containing type is not partial, the generator emits a `ZGOR002` diagnostic warning:

```
Containing type 'IOuter' of discriminated union 'V1' is not declared partial.
The generated code will not compile unless all containing types are partial.
```

The union model is still built and code is still emitted (the error will surface as a compile error anyway); the diagnostic is an early, targeted signal that names the problem.

### 8. JSON converters for hierarchical (abstract) unions

`OneOfBase` cannot be used on both sides of a hierarchical union. C# single inheritance prevents `Rejected` from extending both `ContractOutcome` (which IS a `OneOfBase<...>`) and a second `OneOfBase<...>`. This constraint is fundamental and cannot be worked around without removing `OneOfBase` from one side.

**Decision**: Keep `OneOfBase` unchanged for all flat sealed-class unions. Abstract unions use a hand-rolled union pattern with no `OneOfBase` dependency. Since abstract unions do not exist in Gorilla today, there is nothing to break.

The hand-rolled pattern:
- A `private protected` constructor on the abstract union type prevents external direct instantiation while allowing the generated `*Variant` nested classes and sub-union types to call `base()`.
- A generated `Match<T>(...)` method uses C# `switch` with `is`-patterns for exhaustive dispatch.
- JSON converters for abstract unions are generated using type-pattern `switch` on the concrete value instead of `value.Switch(...)`:

```csharp
// Write side
switch (value)
{
    case ContractOutcome.SuccessVariant s:
        writer.WriteString("$type", "Success");
        writer.WritePropertyName("contractId");
        JsonSerializer.Serialize(writer, s.ContractId, options);
        break;
    case ContractOutcome.Rejected r:
        writer.WriteString("$type", "Rejected");
        JsonSerializer.Serialize(writer, r, options);
        break;
}
```

For the read side, the converter reads `$type` and dispatches to the appropriate factory method, exactly as today.

Nested sub-unions are serialized recursively — a `Rejected` value is serialized by the `Rejected`-level converter, which is also generated.

This approach:
- Leaves the OneOf dependency completely untouched for flat sealed unions.
- Gives abstract unions full JSON support through independently generated converters.
- Avoids any change to consuming application code.

## Risks / Trade-offs

- **Private constructor in abstract union types**: The generated `private protected <TypeName>() {}` constructor is added in the generated partial. If the user also declares a constructor in their hand-written partial, there will be a duplicate constructor error. Mitigation: document that abstract union types should not declare their own constructors; the generator owns that constructor.

- **Sub-union detection at model build time**: `CreateUnionModel` reads `classSymbol.GetTypeMembers()` to find sub-unions. This is a Roslyn symbol read at transform time — it is incremental-safe as long as we include the nested types in the `EquatableArray` equality check (which `SubUnions` does).

- **Abstract unions lack compile-time exhaustiveness from OneOf**: The hand-rolled `Match<T>` throws at runtime for unknown cases (the `_` branch). Flat sealed unions via `OneOfBase` cannot reach that branch. Abstract unions tolerate this because the sealed hierarchy of `*Variant` and sub-union types ensures all cases ARE covered at compile time through the `private protected` constructor — no external subtype can exist.

- **Indentation coupling**: The indent-level approach means that very deeply nested types produce correctly indented but verbose generated files. This is cosmetic only; correctness is not affected.
