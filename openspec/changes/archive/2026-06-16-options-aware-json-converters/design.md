## Context

`DiscriminatedUnionGenerator.cs` emits JSON converters that hardcode variant property-name keys as string literals (`writer.WritePropertyName("Name")`, `root.GetProperty("Name")`). Variant *values* already flow through the caller's serializer (STJ `JsonSerializer.Deserialize(..., options)`, Newtonsoft `ToObject<T>(serializer)`), so nested naming policy already works — only the union's own variant keys ignore it.

Four emitter methods are affected, each emitting its own private helpers into the generated converter class:
- `GenerateJsonConverterClass` (STJ flat) — read key `:949`, write key `:1027`, inference `InferVariantFromProperties` `:971`
- `GenerateHierarchicalJsonConverterClass` (STJ hierarchical) — read key `:683`, write key `:754`, inference `:712`
- `GenerateNewtonsoftJsonConverterClass` (flat) — write key `:1158`, inference `:1100`
- Newtonsoft hierarchical path — write key `:887`, inference `:843`

Constraint: generated code is the only surface that changes; no public API / attribute changes. Default-options output must stay byte-identical (regression gate).

## Goals / Non-Goals

**Goals:**
- Variant property keys resolve through the caller's naming policy (STJ `PropertyNamingPolicy`) / contract resolver (Newtonsoft) for write, read, and inference.
- Honor `PropertyNameCaseInsensitive` (STJ) and equivalent case handling (Newtonsoft) on read + inference.
- Discriminator field name and value stay literal.
- Zero output change under default options/resolver.

**Non-Goals:**
- No new attribute knobs or naming configuration on Gorilla's side.
- No change to how variant *values* serialize (already correct).
- No change to discriminator semantics (field name config, case-insensitive value compare).

## Decisions

### D1: STJ — emit a `ResolvePropertyName` + `GetMember` helper per converter class
Each generated STJ converter gets:
```csharp
private static string ResolvePropertyName(string clrName, string fallback, JsonSerializerOptions options)
    => options.PropertyNamingPolicy is { } policy ? policy.ConvertName(clrName) : fallback;
```
`clrName` is the **PascalCase** generated property name (`FirstCharToUpper(param)`, e.g. `Category`); `fallback` is the **legacy literal key** = the variant ctor param name (e.g. `category`). When a policy is set, STJ's own `ConvertName` drives the name off the real property (so camelCase/snake_case/custom all work); when no policy is set, output is the legacy literal — keeping default output byte-identical.
Write: `writer.WritePropertyName(ResolvePropertyName("Category", "category", options))`.
Read: replace `root.GetProperty("category")` with `GetMember(root, ResolvePropertyName("Category", "category", options), options)`, where `GetMember` tries exact `TryGetProperty`, then falls back to an `OrdinalIgnoreCase` scan of `EnumerateObject()` when `options.PropertyNameCaseInsensitive`, else throws `JsonException`.

*Why the `fallback` arg (deviation from the original pure-function sketch):* today's keys are the camelCase ctor param name, not the PascalCase property name. Feeding `ConvertName` the PascalCase name is what actually honors a policy, but under the **default** (no policy) `ConvertName` is never called — so we must emit the legacy param-name literal to satisfy the byte-identical gate. The `fallback` carries it. `GetProperty` is always case-sensitive, so the `GetMember` scan is the one unavoidable hand-roll — STJ exposes no case-insensitive accessor at `JsonElement` level.

### D2: STJ — thread `options` into `InferVariantFromProperties`
Method is currently `static` with no `options`. Change signature to accept `JsonSerializerOptions options`; build the property `HashSet<string>` with `StringComparer.OrdinalIgnoreCase` when `PropertyNameCaseInsensitive` (else `Ordinal`), and compare resolved names: `properties.Contains(ResolvePropertyName("Category", "category", options))`. Update both STJ call sites (`:664`, `:932`).

### D3: Newtonsoft — resolve through the contract resolver, not a string function
Newtonsoft naming lives in `serializer.ContractResolver` per-type; there is no `ConvertName(string)`. Emit:
```csharp
private static string ResolvePropertyName(string clrName, string fallback, System.Type owner, JsonSerializer serializer)
{
    if (serializer.ContractResolver.ResolveContract(owner) is JsonObjectContract oc)
        foreach (var prop in oc.Properties)
            if (prop.UnderlyingName == clrName)
                return prop.PropertyName != null && prop.PropertyName != clrName ? prop.PropertyName : fallback;
    return fallback;
}
```
`clrName` is the PascalCase generated property name (matches `UnderlyingName`); `fallback` is the legacy literal key (param name). `owner` is the variant value type — known at generation time (the nested `<Variant>Variant` class), emitted as `typeof(<variantValueType>)`. The `prop.PropertyName != clrName` guard is the Newtonsoft analogue of STJ's "no policy" check: a `DefaultContractResolver` resolves `Category`→`Category` (unchanged) → emit the legacy `category` literal (byte-identical default); a `CamelCase`/`SnakeCase`/custom resolver changes it → emit the resolver's name. Write uses it at `:1158`/`:887`. Read looks up via the resolved name, using `obj.GetValue(resolvedName, StringComparison.OrdinalIgnoreCase)` for case handling, keeping `ToObject<T>(serializer)` for the value. Inference threads `serializer` (call sites `:1061`, plus hierarchical) and uses an ordinal-ignore-case `HashSet`.

*Why:* contract resolver is the only authority for Newtonsoft names; mirroring a string policy would diverge from custom resolvers / `[JsonProperty]` attributes. *Why `fallback`:* same as D1 — preserve the legacy camelCase param-name key when the resolver applies no transform, so default output stays byte-identical.

### D4: Discriminator stays literal
Discriminator write (`:883`, `:1151`, and STJ `WriteString("$type", ...)`) and value comparisons keep literals. Field name is config (`DiscriminatorFieldName`); value is data (variant C# name). Neither passes through naming policy/resolver.

## Risks / Trade-offs

- [Default-output regression] → `ResolvePropertyName` returns the input unchanged when no policy/resolver customizes the name; add golden/snapshot tests for both serializers asserting byte-identical default output.
- [Newtonsoft contract resolution cost on hot path] → `ResolveContract` is cached internally by the resolver; acceptable. Resolution happens per property write/read as today's literal did, no extra allocation beyond the lookup.
- [`owner` type not a `JsonObjectContract`] → helper falls back to `clrName`, preserving current behavior for non-object contracts.
- [Inference ambiguity under case-insensitive sets] → using a consistent comparer for both build + compare avoids false negatives; existing inference precedence is unchanged.
- [Multiple `InferVariantFromProperties` emitters drift] → all four emitters must be updated together; covered by per-shape tests (flat + hierarchical, both serializers).

## Migration Plan

Pure generated-code change; consumers recompile and pick up new converter bodies. No runtime migration, no rollback concern beyond reverting the generator. Behavior under default options is unchanged, so existing serialized data round-trips identically.

## Open Questions

None — Newtonsoft full-parity and discriminator-value-stays-literal were resolved in the source task.
