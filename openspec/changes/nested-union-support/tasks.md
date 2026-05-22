## 1. Model Changes

- [x] 1.1 Add `ContainingTypeInfo` record with `Name`, `Keyword`, and `AccessModifier` fields to `DiscriminatedUnionGenerator.cs`
- [x] 1.2 Add `ContainingTypes: EquatableArray<ContainingTypeInfo>` field to `UnionModel`
- [x] 1.3 Add `IsAbstract: bool` field to `UnionModel`
- [x] 1.4 Add `SubUnions: EquatableArray<string>` field to `UnionModel` (short names of direct nested `[DiscriminatedUnion]` subtypes that inherit from this type)

## 2. Symbol Extraction Changes

- [x] 2.1 Extend the `ForAttributeWithMetadataName` predicate to accept `RecordDeclarationSyntax` in addition to `ClassDeclarationSyntax`
- [x] 2.2 In `CreateUnionModel`, walk `classSymbol.ContainingType` up to the namespace boundary and populate `ContainingTypes` (outermost-first), deriving each entry's `Keyword` from `TypeKind` and `IsRecord`
- [x] 2.3 In `CreateUnionModel`, populate `IsAbstract` from `classSymbol.IsAbstract`
- [x] 2.4 In `CreateUnionModel`, scan `classSymbol.GetTypeMembers()` to populate `SubUnions` — include any nested named type that has `[DiscriminatedUnion]` and a base type matching the current symbol

## 3. Diagnostic — Non-Partial Containing Type

- [x] 3.1 Add `ZGOR002` `DiagnosticDescriptor` for non-partial containing type warning
- [x] 3.2 In `Execute`, check each `ContainingTypeInfo` symbol for the `partial` modifier and emit `ZGOR002` for any that are missing it (pass the containing type name and union name in the message)

## 4. Containing-Type Scaffold Emission

- [x] 4.1 Implement `EmitContainingTypeOpen(StringBuilder sb, ContainingTypeInfo type, int indentLevel)` helper that writes the opening `partial <keyword> <name>` declaration and brace
- [x] 4.2 Implement `EmitContainingTypeClose(StringBuilder sb, int indentLevel)` helper that writes the closing brace
- [x] 4.3 Refactor `GenerateSource` to emit one open call per containing type (outermost-first) before the union body and one close call per containing type (innermost-first) after
- [x] 4.4 Thread `indentLevel` into `GenerateClassHeader`, `GenerateConstructor`, `GenerateVariantMethods`, `GenerateVariantClasses`, and the converter generators so indentation tracks nesting depth
- [x] 4.5 Update the `AddSource` hint to use the fully-qualified dotted path (`ContainingTypes.Name + "." + ClassName + ".g.cs"` when containing types are present)

## 5. Flat Sealed-Class Nested Union — OneOfBase Strategy

- [x] 5.1 Verify that after tasks 4.1–4.5 the primary example (`IEntityCreatedContract.IEntityPayload.V1`) compiles and produces correct factory methods and `Match` via the unchanged `OneOfBase` path
- [x] 5.2 Verify no regression for existing top-level flat sealed unions

## 6. Abstract Hierarchical Union — Hand-Rolled Strategy

- [x] 6.1 Add `GenerateHierarchicalSource` method that is dispatched from `Execute` when `union.IsAbstract` is true
- [x] 6.2 In `GenerateHierarchicalSource`, emit a `private protected <TypeName>() { }` constructor in the generated partial
- [x] 6.3 Generate `*Variant` nested classes for each `[Variant]` method; each class SHALL inherit from the abstract union type and have the appropriate internal constructor and properties
- [x] 6.4 Generate factory method implementations that return `new *Variant(...)` (the variant inherits, so it IS the union type — no `OneOf` wrapping)
- [x] 6.5 Generate `Match<T>(...)` using a C# `switch` expression with `is`-pattern arms — one arm per `[Variant]` (using the `*Variant` type) and one arm per entry in `SubUnions` (using the sub-union type directly), plus a `_ => throw new InvalidOperationException(...)` default
- [x] 6.6 Verify the double-nested example (`ContractOutcome` → `Rejected` → `Security`) compiles and all three `Match` levels dispatch correctly

## 7. JSON Converters for Abstract Hierarchical Unions

- [x] 7.1 Implement `GenerateHierarchicalJsonConverterClass` for `System.Text.Json`: Write side uses a C# `switch` on the concrete type (one case per `*Variant`, one case per sub-union), Read side uses `$type` discriminator + factory dispatch
- [x] 7.2 Implement `GenerateHierarchicalNewtonsoftJsonConverterClass` for Newtonsoft.Json using the same dispatch pattern
- [x] 7.3 Emit `[System.Text.Json.Serialization.JsonConverter(...)]` and `[Newtonsoft.Json.JsonConverterAttribute(...)]` attributes on abstract union types in the generated output, conditioned on the same `generateJsonConverter` / `generateNewtonsoftJsonConverter` flags
- [x] 7.4 For sub-union values in the Write path, delegate serialization to the sub-union's own converter (recursive — handled naturally since each level has its own converter)

## 8. Test Project Setup

- [x] 8.1 Create `Zooper.Gorilla.Generators.Tests` xUnit project targeting `net8.0`
- [x] 8.2 Add `Microsoft.CodeAnalysis.CSharp` and `Microsoft.CodeAnalysis.Analyzers` test dependencies for in-process generator invocation
- [x] 8.3 Add a `GeneratorTestHelper` utility that compiles a given C# source string with the `DiscriminatedUnionGenerator` applied and returns diagnostics and generated source text

## 9. Generator Tests — Flat Nested Unions

- [x] 9.1 Test: top-level flat union still generates correctly (regression baseline)
- [x] 9.2 Test: union nested inside a `class` generates factory methods and `Match`
- [x] 9.3 Test: union nested inside an `interface` generates factory methods and `Match`
- [x] 9.4 Test: union nested inside a type that is itself nested inside another interface generates correctly
- [x] 9.5 Test: two `V1` types in different containing scopes produce distinct source hints and both compile without collision
- [x] 9.6 Test: nested union implementing a containing interface (the `IEntityPayload.V1 : IEntityPayload` pattern) compiles and is usable
- [x] 9.7 Test: the full primary example (`IEntityCreatedContract.IEntityPayload.V1`) produces a compilable output with all five variants and a working `Match`

## 10. Generator Tests — Hierarchical Unions

- [x] 10.1 Test: abstract `[DiscriminatedUnion]` record generates factory methods and `Match<T>` with type-switch dispatch
- [x] 10.2 Test: inner sub-union declared as subtype of outer union is included as a typed case in the outer `Match`
- [x] 10.3 Test: a value created via the inner sub-union's factory is assignable to the outer union type and dispatches to the correct outer `Match` arm
- [x] 10.4 Test: the double-nested example (`ContractOutcome.Rejected.Security`) — all three `Match` levels compile and dispatch correctly end-to-end
- [x] 10.5 Test: `ZGOR002` warning is emitted when a containing type is not `partial`

## 11. Generator Tests — JSON Converters

- [x] 11.1 Test: JSON round-trip for a nested sealed-class union preserves variant and all property values
- [x] 11.2 Test: JSON round-trip for an outer abstract union holding a leaf variant preserves type and properties
- [x] 11.3 Test: JSON round-trip for an outer abstract union holding an inner sub-union value preserves the inner variant and its properties
- [x] 11.4 Test: inner sub-union value serialized independently round-trips correctly via its own converter
