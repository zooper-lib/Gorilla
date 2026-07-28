## ADDED Requirements

### Requirement: A non-abstract union declaration is reported as an error

The generator SHALL report diagnostic `ZGOR003` with severity `Error` and category `Zooper.Gorilla` when a `[DiscriminatedUnion]` type is not declared `abstract`. The generator SHALL NOT inject the `abstract` modifier into its own partial half.

#### Scenario: Plain partial class is rejected

- **WHEN** `[DiscriminatedUnion]` is applied to `public partial class SignInError`
- **THEN** `ZGOR003` is reported at that declaration, naming the type

#### Scenario: Sealed partial class is rejected

- **WHEN** `[DiscriminatedUnion]` is applied to `public sealed partial class EntityState`
- **THEN** `ZGOR003` is reported at that declaration

#### Scenario: Non-abstract record is rejected

- **WHEN** `[DiscriminatedUnion]` is applied to `public partial record Outcome`
- **THEN** `ZGOR003` is reported at that declaration

#### Scenario: Abstract declarations are accepted

- **WHEN** `[DiscriminatedUnion]` is applied to `public abstract partial class` or `public abstract partial record`
- **THEN** no `ZGOR003` is reported and the union is generated

#### Scenario: Sealed abstract is left to the compiler

- **WHEN** a declaration reads `sealed abstract partial class`
- **THEN** the compiler reports `CS0418` at that declaration and the generator does not need to translate it

### Requirement: The diagnostic is registered in the analyzer release tracking file

`ZGOR003` SHALL be listed in `AnalyzerReleases.Unshipped.md` alongside `ZGOR001` and `ZGOR002`.

#### Scenario: Release tracking is complete

- **WHEN** the generator project is built
- **THEN** the build produces no analyzer release tracking warnings and `ZGOR003` appears in the unshipped rules table with category `Zooper.Gorilla` and severity `Error`

### Requirement: A code fix rewrites the declaration modifiers

The generator package SHALL ship a Roslyn `CodeFixProvider` registered for `ZGOR003` that removes `sealed` if present and adds `abstract` to the reported declaration, leaving all other modifiers and their order intact.

#### Scenario: Sealed is replaced by abstract

- **WHEN** the fix is applied to `public sealed partial class EntityState`
- **THEN** the declaration becomes `public abstract partial class EntityState`

#### Scenario: Abstract is added to a plain declaration

- **WHEN** the fix is applied to `public partial class SignInError`
- **THEN** the declaration becomes `public abstract partial class SignInError`

#### Scenario: Records are fixed the same way

- **WHEN** the fix is applied to `internal sealed partial record Outcome`
- **THEN** the declaration becomes `internal abstract partial record Outcome` and the accessibility modifier is preserved

#### Scenario: Fix all occurrences applies across a solution

- **WHEN** "Fix all occurrences in solution" is invoked from a single `ZGOR003` site
- **THEN** every reported declaration in the solution is rewritten and no `ZGOR003` remains

### Requirement: The code fix ships in the analyzer package without new packaging rules

The `CodeFixProvider` SHALL live in the generator assembly, which already packs to `analyzers/dotnet/cs`. `Microsoft.CodeAnalysis.CSharp.Workspaces` SHALL be available to the analyzer at runtime.

#### Scenario: Fix is offered from a consuming project

- **WHEN** a project references the generator package and contains a non-abstract union declaration
- **THEN** the IDE offers the code fix at that declaration

#### Scenario: Packaging layout is unchanged

- **WHEN** a package is produced
- **THEN** the generator assembly is packed to `analyzers/dotnet/cs` as before, with no additional package paths introduced
