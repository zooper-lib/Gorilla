# Task: Make generated JSON converters honor serializer settings

## Context

Gorilla is a .NET source generator (`Zooper.Gorilla.Generators`) that emits discriminated unions
and their JSON converters. Today the generated converters **hardcode JSON property-name keys as
string literals**, so they ignore the caller's serializer configuration. This means a caller who
configures `JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase` gets the
union's own variant properties emitted in PascalCase anyway (inconsistent with the rest of their
model), and deserialization of camelCase input fails the property lookup.

The fix is **not** to add naming knobs to Gorilla. The caller's `JsonSerializerOptions`
(System.Text.Json) / `JsonSerializer` settings (Newtonsoft) already own property naming, case
sensitivity, null handling, etc. Gorilla must simply **delegate to them** instead of writing
literals.

### What already works (do NOT change)

Variant *values* are already serialized/deserialized through the caller's options, so naming
policy / custom converters apply correctly to nested values and child types. Examples:
- STJ flat: write `DiscriminatedUnionGenerator.cs:755`, read `:683`; sub-union write `:765`, read `:701`
- STJ hierarchical: write `:1028`, read `:949`
- Newtonsoft: read `:1078` (`ToObject<T>(serializer)`), write `:1159` (`serializer.Serialize`)

Discriminator **field name** is already configurable via `DiscriminatedUnionAttribute.DiscriminatorFieldName`.
Discriminator **value** read comparison is already case-insensitive (`OrdinalIgnoreCase`) at
`:677`, `:940`, `:1069`.

## The bug: property-name keys are literals

All in `Zooper.Gorilla.Generators/DiscriminatedUnionGenerator.cs`.

### System.Text.Json — flat converter (`GenerateJsonConverterClass`)
- Write key literal: line **754** — `writer.WritePropertyName("{param.Name}")`
- Read key literal: line **683** — `root.GetProperty("{param.Name}")`
- Inference set built from literals: lines **728** (`properties.Contains("{p}")`) and the set at **714-716**

### System.Text.Json — hierarchical converter (`GenerateHierarchicalJsonConverterClass`)
- Write key literal: line **1027** (`jsonName` is just an alias for `param.Name`)
- Read key literal: line **949**
- Inference: lines **990**, set at **973-975**

### Newtonsoft — `GenerateNewtonsoftJsonConverterClass`
- Write key literal: line **1158**
- Read key literal: line **1078** (`obj["{paramName}"]`)
- Inference: lines **1119**, set at **1102-1104**

## Required changes

### 1. System.Text.Json converters (both flat and hierarchical)

Emit a private helper into each generated STJ converter class:

```csharp
private static string ResolvePropertyName(string name, System.Text.Json.JsonSerializerOptions options)
    => options.PropertyNamingPolicy?.ConvertName(name) ?? name;
```

Then change the emitted code:

**Write** — replace literal key with resolved key:
```csharp
// before
writer.WritePropertyName("Name");
// after
writer.WritePropertyName(ResolvePropertyName("Name", options));
```
Note: the discriminator field itself (`writer.WriteString("$type", ...)` at `:751`, `:1021`) should
**NOT** go through the naming policy — the discriminator field name is configured explicitly via
`DiscriminatorFieldName`. Leave it literal.

**Read** — resolve the key, and honor `options.PropertyNameCaseInsensitive`:
```csharp
// before
root.GetProperty("Name")
// after
GetMember(root, ResolvePropertyName("Name", options), options)
```
Add a helper that respects case-insensitivity (STJ `GetProperty` is always case-sensitive, so this
must be hand-rolled when `PropertyNameCaseInsensitive` is true):
```csharp
private static System.Text.Json.JsonElement GetMember(
    System.Text.Json.JsonElement root, string name, System.Text.Json.JsonSerializerOptions options)
{
    if (root.TryGetProperty(name, out var direct)) return direct;
    if (options.PropertyNameCaseInsensitive)
    {
        foreach (var prop in root.EnumerateObject())
            if (string.Equals(prop.Name, name, System.StringComparison.OrdinalIgnoreCase))
                return prop.Value;
    }
    throw new System.Text.Json.JsonException($"Missing property '{name}'.");
}
```

**Inference** (`InferVariantFromProperties`) — the property set must compare under the same policy.
Two coupled changes:
1. Build the `HashSet<string>` with a case-insensitive comparer when
   `options.PropertyNameCaseInsensitive` is true. Since this method is currently `static` with no
   `options` param, **thread `options` into it** (change signature to accept
   `JsonSerializerOptions options` and pass at call sites `:664`, `:932`).
2. Compare against the resolved name: `properties.Contains(ResolvePropertyName("Name", options))`.

### 2. Newtonsoft converter — full parity (required)

Newtonsoft naming policy is **not** a simple function — it lives in
`serializer.ContractResolver` (e.g. `CamelCasePropertyNamesContractResolver`), which resolves names
per-type via `ResolveContract(type)`. There is no `ConvertName(string)` equivalent. So resolve keys
through the contract resolver of the variant's value type.

The variant value type is known at generation time (it is the type whose constructor params are the
variant parameters — the same type already referenced for `ToObject<T>`/`serializer.Serialize` at
`:1078`/`:1159`). Emit a helper into the generated Newtonsoft converter class:

```csharp
private static string ResolvePropertyName(
    string clrName, System.Type owner, Newtonsoft.Json.JsonSerializer serializer)
{
    if (serializer.ContractResolver.ResolveContract(owner)
            is Newtonsoft.Json.Serialization.JsonObjectContract objectContract)
    {
        foreach (var prop in objectContract.Properties)
            if (prop.UnderlyingName == clrName && prop.PropertyName != null)
                return prop.PropertyName;
    }
    return clrName;
}
```

Then change emitted code, passing the variant value type via `typeof(<variantValueType>)`:

**Write** (`:1158`) — `writer.WritePropertyName(ResolvePropertyName("Name", typeof(<variantType>), serializer))`.
The discriminator field (`:1151-1152`) stays literal.

**Read** (`:1078`) — look up via the resolved name. Honor case-insensitivity: Newtonsoft `JObject`
indexer is case-sensitive; use `obj.GetValue(resolvedName, StringComparison.OrdinalIgnoreCase)` when
the contract / settings imply it, otherwise exact. Keep `ToObject<T>(serializer)` for the value.

**Inference** (`InferVariantFromProperties`, `:1100-1132`) — resolve each variant's param names
through the resolver before building/comparing the property set. Thread the `serializer` into the
method (change signature, pass at call site `:1061`). Use an ordinal-ignore-case `HashSet` to match
Newtonsoft's default case handling.

Add a test that a `CamelCasePropertyNamesContractResolver` round-trips correctly for both flat and
hierarchical unions.

### 3. Attribute surface

No new knobs. Do not add property-naming or discriminator-value options — caller's
`JsonSerializerOptions` owns naming; discriminator value stays the variant's C# name.

Keep existing `DiscriminatedUnionAttribute` members:
`SuppressValidation`, `DiscriminatorFieldName`, `GenerateJsonConverter`, `GenerateNewtonsoftJsonConverter`.

## Acceptance criteria

1. With `new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }`:
   - serialize emits variant property keys in camelCase
   - round-trip (serialize then deserialize) returns an equal value
   - discriminator field key is unchanged (`$type` or configured value)
2. With `PropertyNameCaseInsensitive = true`, deserializing JSON whose property keys differ only in
   case succeeds.
3. With default options (no naming policy), output is **byte-identical to today** (no regression).
4. Variant inference (deserialize without discriminator field) still works under a naming policy and
   under case-insensitive matching.
5. Hierarchical (nested sub-union) unions pass all of the above, including outer-then-inner dispatch.
6. Existing tests in `Zooper.Gorilla.Generators.Tests` still pass.

## Tests to add (`Zooper.Gorilla.Generators.Tests`)

- STJ camelCase round-trip, flat union with multi-param variant
- STJ camelCase round-trip, hierarchical union
- STJ case-insensitive deserialize (mixed-case input keys)
- STJ inference under camelCase (no discriminator field present)
- Newtonsoft `CamelCasePropertyNamesContractResolver` round-trip, flat + hierarchical
- Newtonsoft inference under camelCase
- default-options / default-resolver output unchanged (golden/snapshot compare, both serializers)
- discriminator field name + value not affected by naming policy (both serializers)

## Files

- `Zooper.Gorilla.Generators/DiscriminatedUnionGenerator.cs` — all converter emission
  (`GenerateJsonConverterClass`, `GenerateHierarchicalJsonConverterClass`,
  `GenerateNewtonsoftJsonConverterClass`, and `InferVariantFromProperties` emitters)
- `Zooper.Gorilla.Generators.Tests/` — new tests
- `README.md` — note that generated converters honor `JsonSerializerOptions` (naming policy, case
  sensitivity); update the JSON Serialization section
- `CHANGELOG.md` — entry

## Decisions (resolved)

1. **Newtonsoft: full parity.** Resolve union property keys through `serializer.ContractResolver`
   (see §2). Not scoped out.
2. **Discriminator value: no naming policy.** It is data, not a property name — stays literal
   (the variant's C# name) in both STJ and Newtonsoft.
