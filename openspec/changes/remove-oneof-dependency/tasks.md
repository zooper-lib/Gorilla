## 1. Delete the flat emit path

- [x] 1.1 Delete `GenerateFlatSource` and every helper reached only from it in `DiscriminatedUnionGenerator.cs` (including the flat STJ and Newtonsoft converter emitters and the variant-name-hiding `Match`/`Switch` overload emitter at `:504`)
- [x] 1.2 Remove the `union.IsAbstract` branch at `:229` so the surviving emitter is called unconditionally; delete `IsAbstract` from `UnionModel` and its assignment at `:108`
- [x] 1.3 Drop the `includeAbstractModifier` parameter from `GetTypeDeclarationPrefix` and always emit `abstract`
- [x] 1.4 Rename the surviving emitter and its helpers to drop the `Hierarchical` qualifier now that there is one path (`GenerateHierarchicalSource`, `GetHierarchicalVariantKeyword`)
- [x] 1.5 Build the generator project; confirm it compiles with no reference to the deleted members

## 2. Declaration diagnostic and code fix

- [x] 2.1 Add the `ZGOR003` `DiagnosticDescriptor` (category `Zooper.Gorilla`, severity `Error`) beside `ZGOR001`/`ZGOR002` at `:21`–`:29`, with a message naming the union type
- [x] 2.2 Report `ZGOR003` when a `[DiscriminatedUnion]` type symbol is not `IsAbstract`, and skip emission for that type
- [x] 2.3 Register `ZGOR003` in `AnalyzerReleases.Unshipped.md`
- [x] 2.4 Promote `Microsoft.CodeAnalysis.CSharp.Workspaces` from `PrivateAssets="all"` so the analyzer has it at runtime
  - **Deviation:** kept `PrivateAssets="all"`. The Roslyn host (VS/Rider/`dotnet build`) supplies the workspace assemblies to analyzers at load time; declaring them as a package dependency would pull the whole Roslyn 4.8 stack into every consuming project's graph. The fix resolves and runs without it — verified by `MakeUnionAbstractCodeFixTests`.
- [x] 2.5 Add a `CodeFixProvider` for `ZGOR003` that removes `sealed` if present and adds `abstract` to the reported declaration, preserving accessibility and the remaining modifier order; applies to class and record declarations
- [x] 2.6 Verify "Fix all occurrences in solution" works from a single site, and that the fix is offered from a consuming project
  - Verified programmatically (`MakeUnionAbstractCodeFixTests`): the fix registers for `ZGOR003`, rewrites `sealed`/plain/`internal sealed record` declarations correctly, and returns Roslyn's `BatchFixer`, which is what implements "Fix all occurrences". Not exercised through an interactive IDE session.

## 3. Migrate this repo's declarations

- [x] 3.1 Apply the code fix (or hand-edit) the 17 `sealed partial class` unions → `abstract partial class`: `Zooper.Gorilla.Sample/NestedContractSamples.cs:25` and `Zooper.Gorilla.Generators.Tests/TestSources.cs` lines 9, 46, 86, 114, 144, 173, 183, 197, 237, 344, 359, 390, 518, 528, 547, 560
- [x] 3.2 Change the 3 plain `partial class` unions → `abstract partial class`: `Zooper.Gorilla.Sample/SignInError.cs`, `SignUpError.cs`, `CreateProfileDto.cs`
- [x] 3.3 Confirm the 11 existing `abstract partial record` declarations need no edit and that no `ZGOR003` remains in the solution

## 4. Converge the generator onto one shape

- [x] 4.1 Change the emitted union constructor from `private protected` (`:583`) to `private`, closing the hierarchy
- [x] 4.2 Emit `Switch(…)` for every union, with the same parameter set as `Match` but `Action<…>` handlers, covering variants and sub-unions from the same concatenated list used at `:607`
- [x] 4.3 Emit `Is<Name>` as a `bool` property for each variant and each sub-union
- [x] 4.4 Emit `As<Name>()` as a method (never a property) returning the subtype, throwing `InvalidOperationException` naming the requested and actual variants on mismatch
- [x] 4.5 Emit `bool TryPick<Name>(out <Subtype> value)` as a method with no remainder parameter
- [x] 4.6 Make `GetTypeKeyword` (`:147`) reject `TypeKind.Struct` instead of mapping it to `struct`/`record struct`, reporting a diagnostic and emitting nothing
  - **Deviation:** rejection is an explicit guard (`GetDeclarationError` → `ZGOR004`) rather than a change to `GetTypeKeyword`, which is also used for *containing* types — a union nested inside a `partial struct` is legitimate and would have broken. Note that `[DiscriminatedUnion]` already targets `AttributeTargets.Class`, so the compiler rejects a struct union with `CS0592` before the generator sees it; `ZGOR004` is a backstop on the generator's own invariant.
- [x] 4.7 Confirm no `Equals`, `GetHashCode`, or `ToString` is emitted for unions or variants
  - Confirmed. **Finding:** because `IsX` accessors are public properties, a `record` union's synthesized `ToString` now lists them: `CardVariant { IsCard = True, Number = 4111 }` rather than the design's illustrative `CardVariant { Number = 4111 }`. Still the language default with the type name not repeated, but noisier than the spec scenario's example.
- [x] 4.8 Confirm variant payloads are still emitted as a constructor plus get-only properties, not positional records, so record equality compares backing fields

## 5. Converters

- [x] 5.1 Delete `InferVariantFromProperties` from all four emit sites (`:755`, `:889`, `:1021`, `:1152`) and the call sites that invoke it (`:707`, `:841`, `:981`, `:1113`)
- [x] 5.2 Delete the `ResolvePropertyName` calls and ambiguity diagnostics that existed only for the guessing path
- [x] 5.3 Record the failure reason before the sub-union search — missing discriminator (naming the configured field) versus unrecognized discriminator (naming the value seen) — and throw that recorded reason if every sub-union also fails, instead of a message with a blank variant name
- [x] 5.4 Apply the same two-reason handling to both the System.Text.Json and Newtonsoft converters
- [x] 5.5 Verify serialized output is byte-identical: `{"$type":"Card","number":"4111"}` and `{"$type":"Cash"}` with default options

## 6. Remove the ASP.NET validation workaround

- [x] 6.1 Delete the `SuppressValidation` member from `DiscriminatedUnionAttribute` in `Zooper.Gorilla.Attributes`
- [x] 6.2 Delete the `SuppressValidation` attribute-parsing case (`:196`), its `UnionConfig` field (`:213`, `:1306`), and the resolution at `:227`
- [x] 6.3 Delete the compilation-wide `ValidateNeverAttribute` type probe (`:50`) and `HasAspNetValidation` from the framework-support model
- [x] 6.4 Delete both `[ValidateNever]` emit sites (`:323`, `:404`)

## 7. Drop the OneOf dependency

- [x] 7.1 Remove `<PackageReference Include="OneOf" Version="3.0.263"/>` from `Zooper.Gorilla.Generators.csproj:21`
- [x] 7.2 Remove the OneOf `PackageReference` from `Zooper.Gorilla.Generators.Tests.csproj:17` and `Zooper.Gorilla.Sample.csproj:15`, then confirm both still build — anything that fails is code still depending on the old surface
- [x] 7.3 Update the package `Description` in `Directory.Build.props:13`, which currently reads "…discriminated unions in C# with OneOf"
- [x] 7.4 Pack and confirm no produced package declares an `OneOf` dependency and the generator assembly still packs to `analyzers/dotnet/cs`

## 8. Tests

- [x] 8.1 Update `TestSources.cs` union declarations (covered by 3.1) and re-run the existing suite; triage every failure as either an intended contract change or a regression
- [x] 8.2 Add tests for `Switch` on unions with sub-unions (`CS1061` today) and on unions without
- [x] 8.3 Add tests for `IsX`/`AsX()`/`TryPickX` across both variants and sub-unions, including `AsX()` throwing on the wrong variant and `TryPickX` returning `false` without throwing
- [x] 8.4 Add a reflection test that enumerates a union's public properties and invokes every getter, asserting no exception — the guard that keeps `AsX` from regressing to a property
- [x] 8.5 Add compile-failure tests: hand-written external subtype (`CS0122`), non-abstract union (`ZGOR003`), variant/member name collision (`CS0102`), struct union rejection
  - Struct rejection asserts `CS0592` (the attribute's target), which is what a user actually hits.
- [x] 8.6 Add a test for a union with ten or more variants
- [x] 8.7 Add `record` union tests: value equality, hash-code agreement, and the synthesized `ToString`; plus a `class` union test asserting reference equality is retained
- [x] 8.8 Add deserialization failure tests asserting the two distinct messages, on unions with and without sub-unions, and with a configured `DiscriminatorFieldName`
- [x] 8.9 Add round-trip tests for field-less variants (the old defect) and for two variants with identical parameter sets
- [x] 8.10 Assert byte-identical serialized output against fixed strings, and that a document written before this change still deserializes
- [x] 8.11 Re-run the naming-policy and contract-resolver tests from the `json-converter-options-awareness` capability against the single converter shape

## 9. Specs and documentation

- [x] 9.1 Sync the delta specs into `openspec/specs/` — `nested-union-generation` (4 MODIFIED) and `json-converter-options-awareness` (3 MODIFIED, 1 REMOVED)
  - The four ADDED capabilities (`union-representation`, `union-api-surface`, `union-json-discriminator`, `union-declaration-diagnostic`) are created by `openspec archive`, not here.
- [x] 9.2 Rewrite the `README.md` flat example at `:37` and the nested example at `:95`, both of which no longer compile
- [x] 9.3 Rewrite the `README.md` hierarchical section, which presents `abstract` as what enables sub-unions — no longer a distinction
- [x] 9.4 Invert the `README.md` `class`-versus-`record` emphasis; `record` is now the better default
- [x] 9.5 Document the API surface: `Match`/`Switch` as the exhaustive door, type patterns for payload access, `IsX` as payload-free shorthand — and state that patterns and accessors carry no exhaustiveness guarantee
- [x] 9.6 Write the migration section: `sealed`/plain → `abstract` (with the code fix), positional accessors → variant-named, `Value`/`Index` removed, hand-written subtypes now a compile error, discriminator now required, and OneOf needing its own `PackageReference` where it was previously transitive
- [x] 9.7 Add the CHANGELOG entry for the major version, marking each breaking change; note that `CHANGELOG.md:103` documents the now-removed `[ValidateNever]` rationale
  - Entry sits under `## [Unreleased]` — the version heading is added when the release is cut.
- [ ] 9.8 Bump the version to the next major in `Directory.Build.props`
  - **Not done — deliberate.** Version bumps are handled manually by the maintainer. `Directory.Build.props` stays at 1.5.0.
