## 1. STJ flat converter (`GenerateJsonConverterClass`)

- [x] 1.1 Emit `private static string ResolvePropertyName(string name, JsonSerializerOptions options)` helper into the generated class
- [x] 1.2 Emit `private static JsonElement GetMember(JsonElement root, string name, JsonSerializerOptions options)` helper (exact `TryGetProperty`, then `OrdinalIgnoreCase` scan when `PropertyNameCaseInsensitive`, else throw `JsonException`)
- [x] 1.3 Write path: replace literal `writer.WritePropertyName("<param>")` (`:1027`) with `ResolvePropertyName("<param>", options)`
- [x] 1.4 Read path: replace `root.GetProperty("<param>")` (`:949`) with `GetMember(root, ResolvePropertyName("<param>", options), options)`
- [x] 1.5 Change `InferVariantFromProperties` (`:971`) signature to accept `JsonSerializerOptions options`; build set with `OrdinalIgnoreCase` comparer when case-insensitive; compare `ResolvePropertyName(...)`; update call site `:932`
- [x] 1.6 Confirm discriminator write (`WriteString("$type", ...)`) stays literal

## 2. STJ hierarchical converter (`GenerateHierarchicalJsonConverterClass`)

- [x] 2.1 Emit `ResolvePropertyName` + `GetMember` helpers into the generated class
- [x] 2.2 Write path: replace literal key (`:754`) with `ResolvePropertyName("<param>", options)`
- [x] 2.3 Read path: replace `root.GetProperty("<param>")` (`:683`) with `GetMember(root, ResolvePropertyName(...), options)`
- [x] 2.4 Thread `options` into `InferVariantFromProperties` (`:712`); resolved + case-aware set; update call site `:664`
- [x] 2.5 Confirm discriminator write (`:883`) and outer-then-inner dispatch unaffected

## 3. Newtonsoft converter (`GenerateNewtonsoftJsonConverterClass` + hierarchical path)

- [x] 3.1 Emit `private static string ResolvePropertyName(string clrName, Type owner, JsonSerializer serializer)` resolving via `ContractResolver.ResolveContract(owner)` → `JsonObjectContract.Properties` (match `UnderlyingName`), fallback to `clrName`
- [x] 3.2 Write path: replace literal key (`:1158`, `:887`) with `ResolvePropertyName("<param>", typeof(<variantValueType>), serializer)`
- [x] 3.3 Read path: look up via resolved name with `obj.GetValue(resolvedName, StringComparison.OrdinalIgnoreCase)`; keep `ToObject<T>(serializer)` for the value
- [x] 3.4 Thread `serializer` into both `InferVariantFromProperties` (`:1100`, `:843`); resolve param names through resolver; ordinal-ignore-case `HashSet`; update call sites (`:1061` + hierarchical)
- [x] 3.5 Confirm discriminator write (`:1151`) stays literal

## 4. Tests (`Zooper.Gorilla.Generators.Tests`)

- [x] 4.1 STJ camelCase round-trip — flat union, multi-param variant
- [x] 4.2 STJ camelCase round-trip — hierarchical union
- [x] 4.3 STJ case-insensitive deserialize — mixed-case input keys
- [x] 4.4 STJ inference under camelCase — no discriminator field present
- [x] 4.5 Newtonsoft `CamelCasePropertyNamesContractResolver` round-trip — flat + hierarchical
- [x] 4.6 Newtonsoft inference under camelCase
- [x] 4.7 Default-options / default-resolver golden snapshot — byte-identical output, both serializers
- [x] 4.8 Discriminator field name + value unaffected by naming policy — both serializers
- [x] 4.9 Run full `Zooper.Gorilla.Generators.Tests` suite — all existing tests pass

## 5. Docs

- [x] 5.1 README JSON Serialization section: note converters honor `JsonSerializerOptions` naming policy + case sensitivity (and Newtonsoft contract resolver)
- [x] 5.2 CHANGELOG entry
