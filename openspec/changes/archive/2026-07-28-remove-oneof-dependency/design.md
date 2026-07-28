## Context

`DiscriminatedUnionGenerator.cs` (1384 lines) branches at `:229` on `union.IsAbstract` into two emitters that have nothing structurally in common:

| | flat (`GenerateFlatSource`, `:281`) | hierarchical (`GenerateHierarchicalSource`, `:367`) |
|---|---|---|
| representation | `: OneOfBase<T0…Tn>` wrapper (`:463`) | `abstract` base, sealed nested subtypes |
| ctor | `private (OneOf<…> value) : base(value)` (`:479`) | `private protected ()` (`:583`) |
| dispatch | delegates to OneOf, variant-named overloads *hide* positional ones (`:504`) | type-switch |
| `Switch` | yes (inherited, re-declared) | no — `CS1061` |
| JSON `Write` | `value.Switch(…)` with positional lambdas | type-switch on value |
| variant ceiling | 9 | none |
| record union | impossible (`CS8864`) | works |

Every feature lands twice; every bug fix must be considered twice. The hierarchical half has shipped since 1.3.0, never used OneOf, and is covered by the behavioral test suite.

The test suite is behavioral, not snapshot-based: it compiles generated source, emits an assembly, and invokes it. It validates the *contract*, so it largely survives a change of representation — this is the safety net that makes replacing the foundation tractable.

Half the target design is already in production. The work is deletion plus convergence, not invention.

## Goals / Non-Goals

**Goals:**

- One emit path. `GenerateFlatSource` and everything reached only from it are deleted; the hierarchical emitter serves every union.
- Zero third-party dependency in the package and in generated code.
- Full ownership of the public API surface — no inherited members we cannot override.
- No variant ceiling.
- The migration edit is mechanical and tool-applicable.
- Byte-identical serialized output, so no stored or in-flight document changes meaning.

**Non-Goals:**

- Generic unions (`partial class Result<T>`). Pre-existing gap, our own, neither fixed nor worsened. Removing OneOf removes the structural obstacle that would have made it painful — that is all.
- An exhaustiveness analyzer over type patterns. Pre-existing gap; Decision 14's closed hierarchy makes it tractable, which is a reason to defer it deliberately rather than absorb it.
- Struct unions. Not deferred — made permanently impossible by Decision 1. Stated as a narrowing, not a gap.
- Attributes on `[Variant]` parameters (currently dropped at `:543`/`:646`). Unrelated, unaffected.
- Preserving OneOf call-site shape. Matching a library we are deleting is not a design constraint.

## Decisions

### 1. Flat unions adopt the hierarchical representation

A union is an `abstract` base whose variants are sealed nested subtypes. One representation, not two.

*Alternative considered: keep the wrapper, hand-roll it without OneOf.* Removes the ceiling and the dependency without touching user source — but preserves four asymmetries permanently: type patterns stay a compile error on wrapper unions (`CS8121`), so `Match` remains the only way in; the two JSON converters still cannot share an implementation (`Write` must dispatch through `Switch(…)` with positional lambdas versus type-switching on the value — the fiddliest code in the generator, duplicated); a wrapper variant can never itself be a union, so nested sub-unions stay structurally impossible on the flat path; and every value costs two allocations with the variant not returnable where the union is expected.

Rejected because a major version means users must act regardless. Spending that budget on one representation rather than two is the better trade, and the surviving code is the half already in production.

*Also ruled out:* the wrapper as a per-union fallback. A representation selected by a modifier reintroduces both codepaths while making which one you get non-obvious. A `sealed` union is an error, not a signal.

### 2. `abstract` is required, not injected

A non-`abstract` `[DiscriminatedUnion]` type is rejected with a diagnostic.

*Alternative considered: inject `abstract` into the generated partial half.* This compiles — the keyword is technically redundant. Rejected for two reasons:

The `sealed` migration must explain itself. With `abstract` injected silently, a user who still has `sealed` gets `error CS0418: an abstract type cannot be sealed or static` pointing at a declaration reading `sealed partial class` and nothing else — a message about a modifier they never wrote. When the declaration says `abstract`, `sealed abstract` is self-evidently contradictory and no diagnostic is needed to translate the compiler.

The declaration must not disagree with the type. Version 1.2.0 fixed phantom `CS8795` errors caused by the IDE and the generator disagreeing about generated partial halves. User source saying `class` while the real type is `abstract` reintroduces exactly that semantic gap.

Migration cost at the margin is zero: every declaration carrying `sealed` must be edited anyway, and `sealed` → `abstract` *is* that edit.

*Cost, named plainly:* `abstract` is conceptually wrong for a small closed set of values. Nobody modelling `SignInError` thinks "abstract base class." The declaration now leaks the implementation strategy into the domain model. Accepted for the two properties above.

*Also ruled out:* making it optional-but-injected later. After users have written it everywhere, that restores the desync surface for no gain.

With every union abstract by construction, `union.IsAbstract` and the `:229` branch disappear; `GetTypeDeclarationPrefix` always emits the modifier and its `includeAbstractModifier` parameter goes with the flat path.

### 3. The `Variant` suffix stays

`ServiceUnavailable()` produces `ServiceUnavailableVariant`.

Not cosmetic: a nested type cannot share a name with a member of the same type (`CS0102`), and the factory method already holds the variant's name because the user declared it. Some disambiguation is mandatory.

The suffix was previously invisible — variant types were reachable only through `Match` lambdas where inference supplies the name. Decision 1 promotes it to something users type in every pattern, making it real public API. It remains the cheapest disambiguation that reads as deliberate.

*Alternative considered: nest variants under a `Variants` container.* Clean leaf names, but no shorter at the call site, and sub-unions cannot move into it — they are user-declared at the union's own level — so it deepens the asymmetry below. *Also ruled out:* a configurable suffix, whose only effect would be letting two codebases disagree about a name.

*Known asymmetry, accepted not solved:* subtypes are named by two rules — generated variants suffixed, user-declared sub-unions verbatim. `ContractOutcome.g.cs:13-15` shows both in one signature: `Func<SuccessVariant, T>` beside `Func<Rejected, T>`. Since Decision 1 makes them the same kind of thing, a call site cannot tell which rule applied without reading the declaration. The generator cannot rename what the user declared.

### 4. Variant-named accessors, over variants *and* sub-unions

Each variant and each sub-union gets `IsX`, `AsX`, `TryPickX`.

Decisions 1 and 2 already make `is` and `switch` work natively, so accessors are ergonomics, not access. Decision 3 is what makes them worth generating: `error is SignInError.ServiceUnavailableVariant` is long precisely because we chose the explicit name; `error.IsServiceUnavailable` restores brevity for the common payload-free test.

Naming by variant rather than position is the same argument that motivated variant-named `Match`: `IsT0` means nothing at a call site and silently changes meaning when variants are reordered.

Covering sub-unions too follows from where the list comes from: `Match` already concatenates `Variants` and `SubUnions` before emitting handlers (`:607`), and that concatenation is the source of its exhaustiveness. Accessors derived from the same list inherit that property and stay correct when a sub-union is added later. Covering variants only would produce an accessor set that does not cover the type — on `ContractOutcome` you could write `IsSuccess` but would drop to a type pattern for the complementary case in the same `if`/`else`. A shorthand covering half the subtypes is worse than none, because users must first learn which half qualifies.

*Cost:* three ways to interrogate a union where two would do, and accessors are the weakest whenever the payload is needed — `error.AsStandard().Category` throws on the wrong variant, while `error is StandardVariant s` tests and binds in one step. Docs must steer accordingly (Decision 11).

*No replacement for `Value` or `Index`.* `Value` is meaningless once the union *is* the value. `Index` exposes positional identity, which nothing consumes — the converters dispatch on names — and which reintroduces the reorder-fragility variant naming exists to eliminate.

*Consequence:* `TryPickX` cannot carry OneOf's remainder parameter. `TryPickT0(out T0, out TRemainder)` hands back a `OneOf` of the other variants; no such type exists here, so the generated form binds the variant alone.

*New failure mode:* generated names now derive from variant names, so a union declaring both a variant `X` and its own member `IsX`/`AsX`/`TryPickX` collides (`CS0102`). The positional accessors could not collide this way.

### 5. `AsX` is a method; `IsX` is a property

OneOf's `AsT0` is a property whose getter throws — `InvalidOperationException: Cannot return as T1 as result is T0`. That single shape is the origin of an entire feature in this codebase: `CHANGELOG.md:103` states `[ValidateNever]` exists to stop ASP.NET's `ValidationVisitor` from *"walking into `OneOfBase` properties and throwing InvalidOperationException."*

Emitting `AsX` as a property rebuilds that hazard immediately after deleting the dependency that caused it. Anything walking public properties by reflection evaluates a getter that throws for every variant but the live one: the ASP.NET validation visitor, debugger watch windows, IntelliSense tooltips, reflection-based mappers. A method is invisible to all of them. The debugger case is the most consequential in daily use, and no attribute suppresses it.

More generally `AsX` is a partial operation, and a method signature communicates that where a property does not — this is what `CA1065` exists to flag.

*Cost:* `error.AsCash()` is two characters longer than OneOf's shape. Not a goal once OneOf is gone.

`IsX` stays a property: returns `bool`, cheap, cannot throw.

### 6. `SuppressValidation` and `[ValidateNever]` are removed

The knob, the compilation-wide ASP.NET type probe (`:50`), the config plumbing (`:196`, `:213`, `:227`, `SuppressValidation` on the config record at `:1306`), and both emit sites (`:323`, `:404`) are deleted.

Decision 5 removes the cause. With `AsX` a method, property walkers never invoke it, and a union's only remaining properties are `IsX` booleans, which cannot throw. Eight code sites and a type probe exist solely to prevent an exception that can no longer occur.

*Alternative considered: keep `[ValidateNever]`, drop only the knob.* The conservative middle. Rejected for the same reason — it is a fix for a fixed bug, and it costs the probe and both emit sites while removing the ability to opt out of them.

*Verified against the framework* by invoking `IObjectModelValidator` directly on the Decision 1 shape:

| property | declared on | validated? |
|---|---|---|
| get-only | the declared type (`Pay`) | yes — error raised |
| get-only | the runtime variant type (`CardVariant`) | no |
| get/set | the runtime variant type (`CardVariant`) | no |

Row 1 rules out get-only properties being skipped generally; row 3 rules out the missing setter being the cause. `ValidationVisitor` resolves metadata from the **declared** type and never sees a variant's properties.

So removing `[ValidateNever]` does not begin enforcing validation attributes on variant payloads. Two independent facts prevent it: the visitor cannot reach those properties, and the generator emits them attribute-free anyway (`:543`, `:646` both produce a bare `public {Type} {Name} { get; }`, so an attribute on a `[Variant]` parameter never reaches the property).

*Spec consequence:* `openspec/specs/json-converter-options-awareness/spec.md:83` asserts the attribute surface "SHALL remain `SuppressValidation`, `DiscriminatorFieldName`, `GenerateJsonConverter`, `GenerateNewtonsoftJsonConverter`". That becomes false; the spec is amended, not merely the code changed.

### 7. Equality and `ToString` are delegated to the language

No generated `Equals`, `GetHashCode`, or `ToString`. A `class` union has reference equality; a `record` union has value equality. The user selects by choosing a keyword.

Measured, before and after:

| | today, `OneOfBase` | after, `class` | after, `record` |
|---|---|---|---|
| `Card("4111") == Card("4111")` | `False` | `False` | `True` |
| `GetHashCode` agreement | `False` | `False` | `True` |
| `ToString()` | `PayC+CardVariant: PayC+CardVariant` | `PayC+CardVariant` | `CardVariant { Number = 4111 }` |

Unions have no value semantics today — `OneOfBase` compares wrapped variant instances, which are plain classes — and `OneOfBase.ToString()` prints the type name twice. Every cell is preserved or improved by writing nothing.

Record semantics survive the generated variant shape: variants are emitted as a constructor plus get-only properties rather than positional records, and record equality compares backing fields, so both value equality and the readable `ToString` hold.

*Alternative considered: generate value equality for all unions.* Means hand-rolling `Equals`/`GetHashCode` across arbitrary payload types — nullable references, collections, nested unions — to replace something the language already does correctly. That is the part of this work most likely to harbor a subtle bug, for no gain. Rejected.

*Cost:* no value equality on `class` unions; a user wanting it declares a `record`. This keeps equality legible — it follows the keyword, as for every other type in C#. The alternative gives `class` unions semantics no other `class` has.

*Consequence:* `record` unions become the better default for most uses. `README.md` teaches `class` for flat and `record` only for hierarchical; that emphasis is now backwards.

### 8. `Switch` is emitted for every union

Both `Match` and `Switch`, covering variants and sub-unions alike.

`Match` returns a value; `Switch` performs side effects. Both take one handler per case — the exhaustiveness guarantee that makes reordering or adding a variant a compile error.

Today only flat unions have `Switch`; it came from the OneOf base and was re-declared with variant names (`:504`). The hierarchical emitter never gained one, so `Hier.A().Switch(…)` fails with `CS1061`. Decision 1 makes the hierarchical emitter serve every union, which would delete `Switch` from all 20 flat unions unless it is added.

Without it, side-effecting code either abuses `Match` with a meaningless return value or falls back to a plain C# `switch` statement — which compiles with a case missing, discarding the exhaustiveness these methods exist to provide. Removing it would push users toward the one construct that tolerates a forgotten variant. Cost is a few lines in the generator.

### 9. Exhaustiveness belongs to `Match`/`Switch` only

`Match` and `Switch` take one required parameter per variant and sub-union, so adding a variant is a compile error at every call site, naming the case missed:

```
error CS7036: There is no argument given that corresponds to the required parameter 'pending'
of 'Outcome.Match<T>(Func<PaidVariant,T>, Func<FailedVariant,T>, Func<PendingVariant,T>)'
```

Verified against the Decision 1 shape and unaffected by every other decision here.

Non-exhaustive type patterns are not introduced by this change. Hierarchical unions have been abstract types with subtype variants since 1.3.0, so `o is Outcome.PaidVariant` has always compiled and has never been exhaustive. Decision 1 extends that property to flat unions rather than creating it.

Decision 10 does close the hierarchy, but the C# compiler performs no closed-hierarchy exhaustiveness analysis: a `switch` covering every variant still reports `CS8509`, while one with a `_` arm and a missing variant reports nothing. Only an analyzer could enforce exhaustiveness over patterns, and it addresses a pre-existing gap unrelated to this work.

*Also ruled out:* suppressing pattern matching by hiding variant types. `Match` handler parameters are typed by them, so they cannot be made inaccessible.

*Consequence:* `IsX` in an `if`/`else` will not fail when a variant is added. Accepted. Documentation presents `Match`/`Switch` as the guaranteed-complete door, patterns for payload access, accessors as payload-free shorthand.

### 10. The union constructor is `private`, closing the hierarchy

`private`, not `private protected` (today's hierarchical `:583`).

A union can acquire a subtype three ways, and the generator knows two:

```csharp
[DiscriminatedUnion]
public abstract partial record Outcome
{
    [Variant] public static partial Outcome Paid(string id);   // (1) variant — generator emits PaidVariant

    [DiscriminatedUnion]                                       // (2) sub-union — nested, recognised
    public abstract partial record Refused : Outcome
    {
        [Variant] public static partial Refused Expired(string cardEnding);
    }
}

public sealed record Cancelled(string Reason) : Outcome;       // (3) hand-written — invisible to the generator
```

`Match`'s signature is written from (1) and (2) only, so (3) cannot appear in it. The result compiles and fails later:

```
Match knows about: paid, refused
  PaidVariant     -> paid
  ExpiredVariant  -> refused
  Cancelled       -> InvalidOperationException: Unknown variant: Cancelled
```

Form (3) is not exotic. `public sealed record Cancelled(string Reason) : Outcome;` is the idiomatic hand-rolled C# discriminated union — what most developers write without a generator, and the obvious-looking way to say "this is also an Outcome." Reaching for `[Variant]` requires already knowing Gorilla's convention.

`private` makes (3) a compile error at the declaration that caused it: `error CS0122: 'Outcome.Outcome()' is inaccessible due to its protection level`.

(1) and (2) are unaffected: variants are generated as nested types, and sub-unions must be nested to be recognised at all — `GetDirectSubUnions` reads `classSymbol.GetTypeMembers()` (`:136`), which sees only nested types. Everything with a legitimate reason to derive is inside the union and can reach a private member.

*Cost:* the compiler says "inaccessible constructor" rather than "unions are closed, use `[Variant]`" — imprecise, but it appears on the offending line rather than in whichever code path later encountered the value.

*Why it matters here:* this restores a property Decision 1 would otherwise remove. Flat unions emit a `private` constructor today (`SignInError.g.cs:10`) and cannot be subclassed at all; adopting `private protected` wholesale would give every flat union a hole it does not have. It also closes the same hole on hierarchical unions, which have had it since 1.3.0 — any existing hand-written subtype stops compiling, code that was already failing at runtime.

With the hierarchy closed, `Match` and `Switch` are total over the types that can exist, and the `_ => throw new InvalidOperationException($"Unknown variant: …")` arm becomes unreachable rather than a live runtime path.

### 11. Variant guessing is removed; the discriminator is required

`InferVariantFromProperties` (four emit sites: `:755`, `:889`, `:1021`, `:1152`) and its `ResolvePropertyName` call sites in the guessing path are deleted from both converters, along with the ambiguity diagnostics only that path could raise.

The heuristic tests `properties.Contains(field)` once per variant parameter — subset matching, not equality — and that is wrong in three distinct ways. Measured:

```
{"number":"4111"}               =>  CardVariant
{"number":"4111","cvv":"123"}   =>  Unknown variant type:     (a complete, valid CardWithCvv payload)
{"body":"hi"}                   =>  Unknown variant type:     (Text vs Memo — identical shapes)
{"number":"4111","junk":true}   =>  CardVariant               (accepted an unrelated object)
```

*Superset variants are unreadable.* `Card(number)`'s condition is satisfied by every `CardWithCvv` payload, so both match and the result is ambiguous. Any union where one variant's fields are a subset of another's has a variant that can never be guessed — an entirely ordinary shape.

*Subset matching means silent misidentification.* Any foreign object containing a `number` field deserializes as a `Card`. Guessing only ever runs on JSON that Gorilla did not write, which is exactly where field names are least under our control and being confidently wrong costs most.

*Some cases carry no information at all.* Two variants with identical field sets, or two field-less variants, cannot be distinguished by any algorithm. This is the source of the field-less defect — `SignInError` has three field-less variants and cannot round-trip without a discriminator.

Gorilla always writes the discriminator, so the feature exists solely for foreign or hand-written JSON. No consumer of that kind is known: nothing in the repository, sample, or tests reads discriminator-less payloads.

*Alternative considered: exact-set matching* — require the payload's field set to equal a variant's parameter set. Genuinely correct where correctness is possible: reads `CardWithCvv`, rejects the unrelated object, leaves only the undecidable cases ambiguous. Ruled out because it preserves a feature justified by a use case with no evidence behind it, and keeping code for a hypothetical consumer is the cost this change exists to reduce. **If discriminator-less interop turns out to be a real requirement, exact-set matching is the design to reinstate — not the subset heuristic.**

*Consequence:* the field-less defect ceases to exist rather than being fixed; `SignInError` round-trips like every other union.

### 12. Deserialization failures report why the payload was unrecognized

A missing discriminator and an unrecognized discriminator are distinct errors, and the reason survives the sub-union search rather than being replaced by a generic message.

JSON carries no type identity, so Gorilla writes a discriminator naming the variant (`{"$type":"Card","number":"4111"}`). Nested unions add a second step: a discriminator may name a variant belonging to a sub-union, so after its own variants fail the converter hands the payload to each sub-union in turn.

That handoff is why the converters diverge on failure today. The hierarchical one cannot report at the moment of failure — a sub-union may still accept — so it discards the reason, tries the sub-unions, and throws with an empty variant name. The flat one has nothing to try and reports the real reason. Measured:

```
FLAT         {"bogus":1}  =>  Unable to infer variant type from properties. Include the discriminator field.
HIERARCHICAL {"bogus":1}  =>  Unknown variant type:
```

Decision 1 deletes the flat converter, so without this decision every union inherits the uninformative message — the error users meet in production, since it fires on any malformed or unexpected request body.

*Implementation shape:* the sub-union search must carry the original failure reason forward rather than overwrite it, and throw it if every sub-union also fails.

*Alternatives ruled out:* adopting the hierarchical behavior unchanged (a regression); and restoring the good message only for unions without sub-unions (leaves nested unions with a blank error and makes diagnostic quality depend on whether a union happens to be nested — an unrelated property).

*Consequence:* hierarchical unions gain a useful failure message for the first time.

*Serialized output is unaffected.* Both converter designs produce byte-identical payloads — `{"$type":"Card","number":"4111"}`, `{"$type":"Cash"}` — from either path.

### 13. The declaration diagnostic ships with a code fix

A Roslyn `CodeFixProvider` accompanies the Decision 2 diagnostic, which gives "Fix all occurrences in solution" for free.

The change is source-breaking by design, and the required edit is the one part a tool can perform perfectly:

```csharp
public sealed partial class EntityState   →   public abstract partial class EntityState
public partial class SignInError          →   public abstract partial class SignInError
```

Remove `sealed` if present, add `abstract`. No judgement, no context, identical every time. This repo alone has 20 such declarations; consuming codebases have an unknown number, all erroring on upgrade day. The fix does not need to understand unions — only to add and remove a keyword on the declaration node the diagnostic already identifies.

This also governs how the upgrade is perceived: without a fix, a user opening their solution after the version bump sees dozens of errors and judges the upgrade expensive; with one, they see dozens of lightbulbs. The edits are identical; the conclusion about whether to adopt is not.

*Cost, named:* it adds machinery to a change whose theme is removing it, and makes `Microsoft.CodeAnalysis.CSharp.Workspaces` — currently `PrivateAssets="all"` — part of what the analyzer needs at runtime. Packaging is unaffected: `Zooper.Gorilla.Generators.csproj` already packs its own assembly to `analyzers/dotnet/cs`, so a fix provider in that assembly ships with no new packaging rules.

### 14. Struct unions become a rejection

`GetTypeKeyword` (`:147`) maps `TypeKind.Struct` to `struct`/`record struct`. Decision 1 requires variants to derive from the union, and structs cannot be inherited from, so those branches describe a shape this design cannot produce. They become rejections rather than emissions.

This is a genuine narrowing, stated as such: the wrapper representation could in principle have supported value-type unions; the abstract-base representation cannot.

## Risks / Trade-offs

**We inherit maintenance we currently outsource.** OneOf's dispatch, equality, and formatting are someone else's tested code; every edge case becomes ours. → Mitigated structurally rather than by testing alone: Decision 7 delegates equality and `ToString` to the language rather than hand-rolling them, which removes the part most likely to harbor a subtle bug. What remains — dispatch and exhaustiveness — is already carried by the hierarchical path and has not been a source of problems. We are not reimplementing something hard; we are reimplementing something small that was outsourced out of convenience.

**Silent behavior change for discriminator-less deserialization.** Callers relying on guessing see failures. → The payloads in question are currently matched by a heuristic that can select the *wrong* variant, so the change converts silent misidentification into a loud error. Named as a breaking change in the release notes with exact-set matching documented as the design to reinstate if a real consumer appears.

**Consumers lose OneOf transitively.** Every shipped package declares `<dependency id="OneOf" version="3.0.263" exclude="Build,Analyzers" />` (1.0.4 through 1.5.0), so a project using OneOf types in its own code may be compiling only because Gorilla supplied the reference. → Migration notes state explicitly that such projects need their own `PackageReference`; the failure is a compile error at the point of use, not a runtime surprise.

**`abstract` leaks the implementation strategy into the domain model.** Accepted in Decision 2, restated here because it is the one cost with no mitigation — only a trade. F#-style union syntax would not have this problem; we do not have that syntax.

**New collision failure mode.** A union declaring both a variant `X` and a member `IsX`/`AsX`/`TryPickX` fails with `CS0102`. → Compile-time, at the declaration, with a message naming the duplicate member. Documented alongside the accessors.

**Any hand-written subtype of a hierarchical union stops compiling** (Decision 10). → That code was already failing at runtime with `Unknown variant`; this moves the failure to the declaration. Called out in migration notes because the compiler's `CS0122` does not explain *why*.

**Test-suite blind spot.** The behavioral suite validates the contract, so it survives a representation change — but that same property means it will not notice emitted-text regressions that happen to preserve behavior. → Acceptable; behavior is what we ship. Existing tests must be extended for the genuinely new surface: `Switch` on hierarchical unions, accessors on variants *and* sub-unions, closed-hierarchy compile errors, discriminator-required failures and their messages, and `record` unions.

## Migration Plan

Version: major. There is no incremental path — the representation change is atomic within the generator.

**Order of work:**

1. Delete `GenerateFlatSource` and everything reached only from it (the second JSON converter pair, the variant-name-hiding overloads at `:504`, `UnionModel.IsAbstract`, the `:229` branch). This is the largest deletion and everything else lands on the surviving emitter.
2. Add the declaration diagnostic + `CodeFixProvider`; register in `AnalyzerReleases.Unshipped.md`. Landing this early means the repo's own 20 declarations can be migrated by the fix rather than by hand — the fix's first real test.
3. Migrate the 20 repo declarations (17 `sealed partial class`, 3 plain `partial class` → `abstract partial class`).
4. Converge the generator: constructor to `private`, `Switch` for all, accessors for variants and sub-unions, struct rejection.
5. Converters: delete guessing, implement failure-reason propagation through the sub-union search.
6. Remove `SuppressValidation`/`[ValidateNever]` and the ASP.NET probe.
7. Drop the OneOf `PackageReference` and the nuspec dependency. Doing this last means the build proves nothing still references it.
8. Amend `openspec/specs/json-converter-options-awareness/spec.md` and `nested-union-generation/spec.md`; rewrite the affected `README.md` sections (`:37`, `:95`, the hierarchical section, and the `class`-versus-`record` emphasis).

**Rollback:** the released package is immutable; consumers roll back by pinning the previous version. Within the repo, the change is a single branch and reverts as one.

**Consumer migration:** upgrade, run the code fix's "Fix all occurrences in solution", then handle the residue by hand — positional accessors to variant-named ones, `Value`/`Index` removals, hand-written subtypes, and any `PackageReference` to OneOf that was previously transitive.

## Open Questions

- **Diagnostic ID.** A new code joins `ZGOR001`/`ZGOR002` — presumably `ZGOR003`, and its severity: error is the honest signal (the emitted code cannot work otherwise), but a warning would let a solution keep building mid-migration. Error is the recommendation; the code fix is what makes it tolerable.
- **Exact wording of the two deserialization failure messages** (Decision 12). The design fixes that they must be distinct and must survive the sub-union search; the strings themselves are settled in the spec.
- **Whether `TryPickX` is worth emitting at all** given that `is X x` tests and binds in one step and `AsX()` covers the rest. Decision 4 keeps it for symmetry with the OneOf surface being replaced; if it proves unused, it is the cheapest thing to drop later — adding it back is non-breaking, removing it is not.
