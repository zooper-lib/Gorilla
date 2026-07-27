# Removing OneOf

## The idea

Gorilla currently builds flat discriminated unions on top of [OneOf](https://github.com/mcintyre321/OneOf) —
generated unions derive from `OneOfBase<T0…Tn>` and delegate their storage, `Match`, and `Switch` to it.
We want to drop that dependency and own the union representation ourselves.

This is less of a rewrite than it sounds. Gorilla already has two emit paths, and the *hierarchical*
one (abstract unions with nested sub-unions) has never used OneOf. It emits an abstract base, sealed
subclasses per variant, and a type-switch for `Match`. That design ships today, is covered by tests,
and has no arity ceiling. The proposal is to make the flat path work the same way and delete the OneOf
path entirely.

## Why

**The 9-variant ceiling is real and has no workaround.** OneOf ships `OneOfBase` arities 1 through 9
and stops. A union with a tenth variant simply cannot be expressed. Every escape hatch — nesting,
grouping, wrapper types — distorts the user's domain model to fit a library limit. Gorilla's whole
pitch is that the union declaration should read like the domain; a hard cap at nine contradicts that.

**Consumers inherit a dependency they never asked for.** Adding Gorilla to a project pulls OneOf into
the dependency graph, and generated code references it by name. For a source generator whose output is
otherwise self-contained, that is an odd tax.

**OneOf leaks through our own abstraction.** We just shipped variant-named `Match`/`Switch` so that
reordering variants becomes a compile error instead of a silent handler remap. That safety is
implemented by *hiding* OneOf's positional versions — and hiding is not overriding. Anyone holding the
value through its OneOf base type gets the positional methods back, and the guarantee quietly evaporates.
We cannot fully control our own API surface while inheriting someone else's.

**Two codepaths for one concept.** Flat and hierarchical unions currently diverge in how they store
values, how they dispatch, and how their JSON converters are written. Every feature and every bug fix
has to be considered twice, and "fixed in flat, still broken in hierarchical" is a shape of bug we keep
inviting. Converging on one representation removes that category.

## What we gain

- No variant limit.
- No third-party dependency in the package or the generated code.
- Full ownership of the public API surface — variant-named accessors replace positional `IsT0` / `AsT0` /
  `TryPickT0`, which mean nothing at a call site and silently remap when variants move (Decisions 4 and 7).
- A single emit path, meaningfully less generator code, and one JSON converter design instead of two.
- Union types can be records, which the OneOf base type forbids (see Decision 1).

## The tensions

**This is a breaking change, including to source.** Anything treating a union as a OneOf type stops
compiling, and the positional `IsT0`/`AsT0`/`TryPickT0`/`Value`/`Index` accessors disappear. Most of that
surface has a better-named replacement (Decision 4), so the migration is largely "these got clearer names."
Every union *declaration* must also be edited (Decisions 1 and 2), which is work no replacement softens.

Consumers also lose OneOf transitively. Every shipped package declares it —
`<dependency id="OneOf" version="3.0.263" exclude="Build,Analyzers" />` in the nuspec of 1.0.4 through
1.5.0 — so a project using OneOf types in its own code may be compiling only because Gorilla supplied the
reference. Those projects need their own `PackageReference` after this change.

**We inherit maintenance we currently outsource.** OneOf's dispatch, equality, and formatting are
someone else's tested code. Once we generate our own, every edge case is ours: null handling, equality
semantics, `ToString`, exhaustiveness. The counter-argument is that the hierarchical path already carries
this burden and has not been a source of problems — and that a union's runtime behavior is genuinely
simple. We are not reimplementing something hard; we are reimplementing something small that we had
outsourced out of convenience.

**Union values become abstract types with sealed variant subclasses.** This is how the hierarchical path
already models things and it is a more faithful representation of a discriminated union than a wrapper
holding one of N unrelated types. But it does change what the type *is*, which is worth stating plainly
rather than presenting the change as purely subtractive. Decision 1 settles it; Decision 6 covers the
equality semantics that follow from it.

**Scope discipline.** Removing OneOf makes adjacent things newly possible — generic unions, record
unions — and newly impossible — struct unions. None of them are required to remove OneOf, and bundling
them would turn a contained, well-tested change into an open-ended one. The exhaustiveness analyzer
described in Decision 11 is explicitly deferred rather than absorbed; it predates this work and is not
caused by it.

## Decisions

### 1. Flat unions adopt the hierarchical representation

A union is an abstract base type whose variants are sealed types deriving from it. There is one
representation, not two. The wrapper shape — a union object holding an unrelated variant object — is
gone.

**Rationale.** The wrapper alternative would have removed the variant ceiling without touching user
source, but it preserves four asymmetries between flat and hierarchical unions permanently:

- Type patterns are a compile error on wrapper unions (`CS8121`), so `Match` is the only way into the
  data. Hierarchical unions support `is`, `switch`, and property patterns.
- The two JSON converters cannot share an implementation. The wrapper's `Write` must dispatch through
  `value.Switch(...)` with positional lambdas; the hierarchical `Write` type-switches on the value. This
  is the fiddliest code in the generator, duplicated, because one shape cannot express the other's
  technique.
- A wrapper variant can never itself be a union, so nested sub-unions are structurally impossible on the
  flat path.
- A wrapper allocates two objects per value and the variant cannot be returned where the union is
  expected.

Since this ships as a major version, users already expect to act. Spending that budget on one
representation rather than two is the better trade: the surviving code is the hierarchical path, which is
already in production and covered by tests.

**What this rules out.** Union types can no longer be declared `sealed` — `sealed` and `abstract` are
mutually exclusive (`CS0418`) and variants must derive from the union (`CS0509`). Every union declaration
that says `sealed` must drop it. This is a source-breaking change to user code, not just a behavioral one.

It also rules out the wrapper as a fallback for awkward cases. There is no per-union escape hatch; a
`sealed` union is an error, not a signal to switch representations. A representation that depends on a
modifier would reintroduce both codepaths while making which one you get non-obvious.

**Consequences in this repo.** 17 union declarations must drop `sealed`:
`Zooper.Gorilla.Sample/NestedContractSamples.cs:25`; `Zooper.Gorilla.Generators.Tests/TestSources.cs`
lines 9, 46, 86, 114, 144, 173, 183, 197, 237, 344, 359, 390, 518, 528, 547, 560. The nested-union example
in `README.md:95` uses `sealed` and must change with them.

`Zooper.Gorilla.Generators/DiscriminatedUnionGenerator.cs` loses `GenerateFlatSource` and the helpers
reached only from it, including the second JSON converter pair. `GetHierarchicalVariantKeyword` and the
`SubUnions` handling become the behavior for all unions rather than the abstract ones.

The variant-named `Match`/`Switch` feature stops being a hack. It is currently implemented by declaring
`public new` members that *hide* OneOf's positional `f0`/`f1` versions, and hiding is not overriding — a
value held as its OneOf base type still resolves to the positional methods, silently discarding the
reorder-safety the feature exists to provide. With no base type, the variant-named forms are the only ones
that exist and the `new` modifier disappears.

**Newly possible.** A record cannot inherit from a class, so `record` unions are impossible today — the
generated `: OneOfBase<…>` base type forbids them (`CS8864`), which is why every flat union in this repo
is a class. Under the abstract-base design a union can be a record, and its variants inherit record
equality.

### 2. Unions must be declared `abstract`, and there is one emit path

The generator does not infer or inject abstractness. A `[DiscriminatedUnion]` type that is not declared
`abstract` is rejected with a diagnostic. Since every union is then abstract by construction, there is no
branch to take: one emitter serves all unions, and a union either has sub-unions or has none.

**Rationale.** The generator could legally add `abstract` to its own partial half — this compiles — so the
keyword is technically redundant. Requiring it anyway buys two things:

The `sealed` migration explains itself. With `abstract` injected silently, a user who still has `sealed`
sees `error CS0418: an abstract type cannot be sealed or static` pointing at a declaration that says
`sealed partial class` and nothing else. The message is about a modifier they never wrote. When the
declaration says `abstract`, `sealed abstract` is self-evidently contradictory and the compiler reads
correctly without a diagnostic to translate it.

The declaration stops disagreeing with the type. Version 1.2.0 fixed phantom `CS8795` errors caused by the
IDE and the generator disagreeing about generated partial halves. A design where user source says `class`
and the real type is `abstract` reintroduces a semantic gap between the two halves — the exact surface
that bug class lives on. A declaration that states the truth has nothing to desync.

The migration cost is zero at the margin: every declaration carrying `sealed` must be edited regardless,
and `sealed` → `abstract` is that same edit.

**What this rules out.** The cost is real and worth naming: `abstract` is conceptually wrong for how users
think about a small closed set of values. Nobody modelling `SignInError` thinks "abstract base class."
The declaration now leaks the implementation strategy into the domain model, which F#-style union syntax
would not. We accept that in exchange for the two properties above.

It also rules out inferring the modifier later as a convenience. Making `abstract` optional-but-injected
after users have written it everywhere would restore the desync surface for no gain.

**Consequences in this repo.** Of 31 union declarations, 20 must change and 11 already comply:

- 17 `sealed partial class` → `abstract partial class`: `Zooper.Gorilla.Sample/NestedContractSamples.cs:25`
  and `Zooper.Gorilla.Generators.Tests/TestSources.cs` lines 9, 46, 86, 114, 144, 173, 183, 197, 237, 344,
  359, 390, 518, 528, 547, 560.
- 3 plain `partial class` → `abstract partial class`: `Zooper.Gorilla.Sample/SignInError.cs`,
  `Zooper.Gorilla.Sample/SignUpError.cs`, `Zooper.Gorilla.Sample/CreateProfileDto.cs`.
- 11 `abstract partial record` declarations in `ContractOutcome.cs` and `TestSources.cs` are already
  correct.

`README.md` teaches two declarations that no longer compile — the flat example at line 37 and the nested
example at line 95 — and its hierarchical section presents `abstract` as what enables sub-unions, which
stops being true when every union has it.

In `DiscriminatedUnionGenerator.cs`, `UnionModel.IsAbstract` and the branch at `:229` are removed;
`GetTypeDeclarationPrefix` always emits the modifier. A new diagnostic joins `ZGOR001`/`ZGOR002` and must
be registered in `AnalyzerReleases.Unshipped.md`. `GetTypeKeyword`'s `TypeKind.Struct` branches describe a
shape this design cannot produce and become rejections rather than emissions.

### 3. Generated variant types keep the `Variant` suffix

`ServiceUnavailable()` produces a `ServiceUnavailableVariant`. The suffix stays.

**Rationale.** It is not cosmetic. A nested type cannot share a name with a member of the same type
(`CS0102`), and the factory method already holds the variant's name because the user declared it. Some
disambiguation is mandatory.

The suffix was previously invisible — variant types were reachable only through `Match` lambdas, where
inference supplies the name and nobody types it. Decision 1 promotes it to something users write in every
type pattern, so it becomes real public API rather than an implementation detail. It is still the cheapest
disambiguation that reads as deliberate: `ServiceUnavailableVariant` is unambiguously the type and never
confusable with the factory.

**What this rules out.** Nesting variants under a `Variants` container was the main alternative. It buys
clean leaf names but is no shorter at the call site, and sub-unions cannot move into that container — they
are user-declared at the union's own level — so it deepens the very asymmetry described below. A
configurable suffix is also ruled out; its only effect would be to let two codebases disagree about a name.

**Known asymmetry.** Subtypes of a union are now named by two rules: generated variants are suffixed,
user-declared sub-unions are verbatim. `ContractOutcome.g.cs:13-15` shows both in one signature —
`Func<SuccessVariant, T>` beside `Func<Rejected, T>`. Since decision 1 makes both the same kind of thing —
a subtype users pattern-match on — a call site cannot tell which rule applied without looking at the
declaration. This is accepted rather than solved; the generator cannot rename what the user declared.

### 4. Variant-named accessors are generated

Each variant gets `IsX`, `AsX`, and `TryPickX` alongside `Match` and `Switch`. These replace OneOf's
positional `IsT0` / `AsT0` / `TryPickT0`.

**Rationale.** Decisions 1 and 2 already make `is` and `switch` work natively, so these are not required
for access — they are an ergonomic choice, and the call site should get to make it. Decision 3 is what
makes them worth generating: `error is SignInError.ServiceUnavailableVariant` is long, and it is long
because we chose the explicit type name. `error.IsServiceUnavailable` restores brevity for the common case
of testing a variant without needing its payload.

Naming them after variants rather than positions is the same argument that motivated variant-named
`Match`: `IsT0` means nothing at a call site and silently changes meaning when variants are reordered.

**What this rules out.** It accepts three ways to interrogate a union — `Match`, patterns, and accessors —
where two would do, and the accessors are the weakest of the three whenever the payload is needed:
`error.AsStandard.Category` throws on the wrong variant, while `error is StandardVariant s` tests and binds
in one step. Documentation should present `Match` for exhaustive handling and patterns for payload access,
with accessors as the shorthand for payload-free tests.

`Value` and `Index` get no replacement. `Value` is meaningless once the union *is* the value. `Index`
exposes positional identity, which nothing in the JSON converters consumes — they dispatch on variant
names — and which reintroduces the reorder-fragility that variant naming exists to eliminate.

**Consequences.** `TryPickX` cannot carry OneOf's remainder parameter. `TryPickT0(out T0 value, out
TRemainder remainder)` hands back a `OneOf` of the other variants; this design has no such type, so the
generated form binds the variant alone.

Generated member names now derive from variant names, so a union declaring both a variant `X` and its own
member named `IsX`, `AsX`, or `TryPickX` collides (`CS0102`). This is a new failure mode that the
positional accessors did not have.

### 5. Accessors cover sub-unions as well as variants

`ContractOutcome` gets `IsSuccess`/`AsSuccess`/`TryPickSuccess` for its variant and
`IsRejected`/`AsRejected`/`TryPickRejected` for its sub-union.

**Rationale.** `Match` already draws from a single list of both — `DiscriminatedUnionGenerator.cs:607`
concatenates `Variants` and `SubUnions` before emitting handlers — and that is where its exhaustiveness
comes from. Accessors derived from the same list inherit that property and stay correct when a sub-union is
added later.

Covering variants only would produce an accessor set that does not cover the type: on `ContractOutcome` you
could write `IsSuccess` but would have to drop to a type pattern for the complementary case in the same
`if`/`else`. A shorthand that covers half the subtypes is worse than no shorthand, because users must first
learn which subtypes qualify.

**What this rules out.** `AsRejected` returns a union that must itself be matched, so it reads like a
partial answer. That is inherent to nested unions rather than to the accessor — `Match`'s `rejected`
handler has the same shape today, and `README.md:173` already shows users chaining through it.

### 6. Equality and `ToString` are delegated to the language

The generator emits no `Equals`, `GetHashCode`, or `ToString`. A `class` union has reference equality; a
`record` union has value equality. The user selects by choosing a keyword.

**Rationale.** Measured behavior, before and after:

| | today, `OneOfBase` | after, `class` union | after, `record` union |
|---|---|---|---|
| `Card("4111") == Card("4111")` | `False` | `False` | `True` |
| `GetHashCode` agreement | `False` | `False` | `True` |
| `ToString()` | `PayC+CardVariant: PayC+CardVariant` | `PayC+CardVariant` | `CardVariant { Number = 4111 }` |

Unions have no value semantics today — `OneOfBase` compares the wrapped variant instances, which are plain
classes, so equality is reference equality. `OneOfBase.ToString()` prints the type name twice. Every cell
is therefore preserved or improved by writing nothing.

Record semantics survive the generated variant shape: variants are emitted as a constructor plus get-only
properties rather than positional records, and record equality compares backing fields, so value equality
and the readable `ToString` both hold.

Generating equality would mean hand-rolling `Equals`/`GetHashCode` across arbitrary payload types —
nullable references, collections, nested unions — to replace something the language already does correctly.
That is the part of this work most likely to harbor a subtle bug, for no gain.

**What this rules out.** Value equality on `class` unions. A user wanting it must declare the union a
`record`. This keeps equality legible: it follows the keyword, exactly as it does for every other type in
C#. The alternative would give `class` unions semantics no other `class` in the codebase has.

**Consequences.** `record` unions are newly possible under Decision 1 and are the better default for most
uses — value semantics and useful diagnostics output for free. `README.md` currently teaches `class` for
flat unions and `record` only for hierarchical ones; that emphasis is now backwards.

### 7. `AsX` is a method, `IsX` is a property

The accessor set from Decision 4 is emitted as an `IsX` property, an `AsX()` method, and a
`TryPickX(out …)` method.

**Rationale.** OneOf's `AsT0` is a property whose getter throws — measured:
`InvalidOperationException: Cannot return as T1 as result is T0`. That single shape is the origin of an
entire feature in this codebase. `CHANGELOG.md:103` states it plainly: `[ValidateNever]` exists to prevent
ASP.NET's `ValidationVisitor` from *"walking into `OneOfBase` properties and throwing
InvalidOperationException."*

Emitting `AsX` as a property would rebuild that hazard immediately after deleting the dependency that
caused it. Anything that walks public properties by reflection evaluates a getter that throws for every
variant except the live one: the ASP.NET validation visitor, debugger watch windows and IntelliSense
tooltips, and reflection-based mappers. A method is invisible to all of them.

The debugger case is the most consequential in daily use — with property-shaped accessors, inspecting any
union shows an exception on every accessor but one, and no attribute suppresses that. More generally,
`AsX` is a partial operation, and a method signature communicates that where a property does not; this is
what `CA1065` exists to flag.

**What this rules out.** Parity with OneOf's call-site shape. `error.AsCash()` costs two characters over
`error.AsCash`, and matching OneOf is not a goal once OneOf is gone.

`IsX` remains a property: it returns `bool`, is cheap, and cannot throw, so none of the above applies.

### 8. `SuppressValidation` and the `[ValidateNever]` emission are removed

The knob, the ASP.NET type probe, the config plumbing, and both emit sites are deleted. Unions are
validated by ASP.NET like any other model.

**Rationale.** ASP.NET's `ValidationVisitor` inspects a bound model by reading every public property via
reflection. On a `OneOfBase` union that walk hits a getter that throws:

```
read Value  -> Cash      read IsT0 -> True     read AsT0 -> Cash
read Index  -> 0         read IsT1 -> False    read AsT1 -> THROWS InvalidOperationException
```

An API endpoint accepting a union in its body would therefore fail with an unhandled exception during
validation, before the action method ran. `[ValidateNever]` tells the visitor not to walk the type, and
`SuppressValidation` is the manual override for that automatic stamping.

Decision 7 removes the cause. With `AsX` emitted as a method, property walkers never invoke it, and a
union's only remaining properties are `IsX` booleans, which cannot throw. Eight code sites and a
compilation-wide type probe exist solely to prevent an exception that can no longer occur.

**What this rules out.** Keeping `[ValidateNever]` without the knob was the conservative middle option. It
is rejected for the same reason: it is a fix for a fixed bug, and it costs the probe and emit sites while
removing the ability to opt out of them.

**Consequences.** ASP.NET will now walk union-typed model properties, and that walk is inert. Verified
against the framework by invoking `IObjectModelValidator` directly on the Decision 1 shape:

| property | declared on | validated? |
|---|---|---|
| get-only | the declared type (`Pay`) | yes — error raised |
| get-only | the runtime variant type (`CardVariant`) | no |
| get/set | the runtime variant type (`CardVariant`) | no |

The first row rules out get-only properties being skipped generally; the third rules out the absence of a
setter being the cause. `ValidationVisitor` resolves metadata from the **declared** type and never sees a
variant's properties.

Removing `[ValidateNever]` therefore does not begin enforcing validation attributes on variant payloads.
Two independent facts prevent it: the visitor cannot reach those properties, and the generator emits them
without attributes in any case — `DiscriminatedUnionGenerator.cs:543` and `:646` both produce a bare
`public {Type} {Name} { get; }`, so an attribute on a `[Variant]` parameter never reaches the property.
After this change the visitor walks a union's declared surface, which is the `IsX` booleans, finds no
validation attributes, and stops.

`openspec/specs/json-converter-options-awareness/spec.md:83` and the archived proposal both state that the
attribute surface "SHALL remain `SuppressValidation`, `DiscriminatorFieldName`, `GenerateJsonConverter`,
`GenerateNewtonsoftJsonConverter`". That assertion becomes false and the spec must be amended, not merely
the code changed.

### 9. Deserialization failures report why the payload was unrecognized

When a payload cannot be resolved to a variant, the converter reports why. A missing discriminator and an
unrecognized discriminator are distinct errors, and the reason survives the sub-union search rather than
being replaced by a generic message.

**Rationale.** JSON carries no type identity, so Gorilla writes a discriminator field naming the variant
(`{"$type":"Card","number":"4111"}`). Nested unions add a second step: a discriminator may name a variant
belonging to a sub-union rather than to the union being read, so after its own variants fail the converter
hands the payload to each sub-union in turn.

That handoff is why the two converters diverge on failure today. The hierarchical one cannot report a
failure at the moment it occurs — a sub-union may still accept the payload — so it discards the reason,
tries the sub-unions, and throws a message with an empty variant name. The flat one has nothing to try and
reports the real reason. Measured:

```
FLAT         {"bogus":1}  =>  Unable to infer variant type from properties. Include the discriminator field.
HIERARCHICAL {"bogus":1}  =>  Unknown variant type:
```

Decision 1 deletes the flat converter, so without this decision every union would inherit the
uninformative message. This is the error users meet in production, since it fires on any malformed or
unexpected request body.

**What this rules out.** Adopting the hierarchical behavior unchanged, which would be a regression. Also
ruled out is restoring the good message only for unions without sub-unions: that leaves nested unions with
a blank error and makes diagnostic quality depend on whether a union happens to be nested — an unrelated
property.

**Consequences.** Hierarchical unions gain a useful failure message for the first time; they currently
produce the blank one on every failure.

Serialized output is unaffected. Both converter designs produce byte-identical payloads —
`{"$type":"Card","number":"4111"}` and `{"$type":"Cash"}` from either path — so no stored or in-flight
document changes meaning.

### 10. `Switch` is emitted for every union

Both `Match` and `Switch` are generated, covering variants and sub-unions alike.

**Rationale.** `Match` returns a value; `Switch` performs side effects. Both require one handler per case,
which is the exhaustiveness guarantee that makes reordering or adding a variant a compile error.

Today only flat unions have `Switch` — it came from the OneOf base type and was re-declared with variant
names. The hierarchical emitter never gained one, so `Hier.A().Switch(...)` fails with `CS1061`. Decision 1
makes the hierarchical emitter serve every union, which would delete `Switch` from all 20 flat unions
unless it is added.

Without `Switch`, side-effecting code must either abuse `Match` with a meaningless return value, or fall
back to a plain C# `switch` statement — which compiles when a case is missing, discarding the exhaustiveness
that these methods exist to provide. Removing it would push users toward the one construct that tolerates a
forgotten variant.

**What this rules out.** Nothing of value; `Switch` is a few lines in the generator. Hierarchical unions
gaining it is a fix rather than a cost.

### 11. Exhaustiveness is a property of `Match` and `Switch` only

`Match` and `Switch` are the exhaustive door and remain compiler-enforced. Type patterns and the `IsX`
accessors are convenience with no exhaustiveness guarantee, and no analyzer is written to give them one.

**Rationale.** `Match` and `Switch` take one required parameter per variant and sub-union, so adding a
variant is a compile error at every call site, naming the case that was missed:

```
error CS7036: There is no argument given that corresponds to the required parameter 'pending'
of 'Outcome.Match<T>(Func<PaidVariant,T>, Func<FailedVariant,T>, Func<PendingVariant,T>)'
```

This is verified against the Decision 1 shape and is unaffected by every decision in this document.

Non-exhaustive type patterns are not introduced here. Hierarchical unions have been abstract types with
subtype variants since 1.3.0, so `o is Outcome.PaidVariant` has always compiled and has never been
exhaustive. Decision 1 extends that property to flat unions rather than creating it.

Decision 14 does close the hierarchy — no subtype can exist beyond the generated variants and nested
sub-unions — but the C# compiler does not use that fact. It performs no closed-hierarchy exhaustiveness
analysis, so a `switch` covering every variant still reports `CS8509`, while one with a `_` arm and a
missing variant reports nothing. An analyzer is the only mechanism that could enforce exhaustiveness over
patterns, and it would be addressing a pre-existing gap unrelated to this work. Decision 14 does make such
an analyzer tractable, since the set of subtypes becomes fixed and knowable.

**What this rules out.** An exhaustiveness analyzer is out of scope. Suppressing pattern matching by
hiding variant types is also ruled out — `Match` handler parameters are typed by them, so they cannot be
made inaccessible.

**Consequences.** Documentation should present `Match`/`Switch` as the guaranteed-complete way to handle a
union, with patterns and accessors as shorthand for cases that do not need completeness. `IsX` in an
`if`/`else` will not fail when a variant is added; that is accepted.

### 12. The declaration diagnostic ships with a code fix

The diagnostic from Decision 2 is accompanied by a Roslyn `CodeFixProvider` that rewrites the modifiers,
which gives "Fix all occurrences in solution" for free.

**Rationale.** The whole change is source-breaking by design, and the required edit is the one part of it a
tool can perform perfectly:

```csharp
public sealed partial class EntityState   →   public abstract partial class EntityState
public partial class SignInError          →   public abstract partial class SignInError
```

Remove `sealed` if present, add `abstract`. No judgement, no context, identical every time. This repo alone
has 20 such declarations and consuming codebases have an unknown number, all of which error on the day they
upgrade.

The fix does not need to understand unions — only to add and remove a keyword on the declaration node the
diagnostic already identifies. Roslyn supplies batch application on top of a single-site fix, turning a
whole-solution migration into one action.

This also governs how the upgrade is perceived. Without a fix, a user opening their solution after the
version bump sees dozens of errors and judges the upgrade expensive. With one, they see dozens of
lightbulbs. The edits are identical; the conclusion about whether to adopt is not.

**What this rules out.** It adds machinery to a change whose theme is removing it. The generator package
ships no code fixes today, so this introduces a `CodeFixProvider` and makes
`Microsoft.CodeAnalysis.CSharp.Workspaces` — currently referenced with `PrivateAssets="all"` — part of what
the analyzer actually needs at runtime. Packaging is unaffected: `Zooper.Gorilla.Generators.csproj` already
packs its own assembly to `analyzers/dotnet/cs`, so a fix provider in that assembly ships with no new
packaging rules.

### 13. Variant guessing is removed; the discriminator is required

The converters no longer infer a variant from which field names are present. A payload without the
discriminator field is an error.

**Rationale.** The heuristic tests `properties.Contains(field)` once per variant parameter — subset
matching, not equality — and that is wrong in three distinct ways. Measured:

```
{"number":"4111"}               =>  CardVariant
{"number":"4111","cvv":"123"}   =>  Unknown variant type:     (a complete, valid CardWithCvv payload)
{"body":"hi"}                   =>  Unknown variant type:     (Text vs Memo — identical shapes)
{"number":"4111","junk":true}   =>  CardVariant               (accepted an unrelated object)
```

*Superset variants are unreadable.* `Card(number)`'s condition is satisfied by every `CardWithCvv` payload,
so both match and the result is ambiguous. Any union where one variant's fields are a subset of another's
has a variant that can never be guessed — an entirely ordinary shape.

*Matching on subsets means silent misidentification.* Any foreign object containing a `number` field
deserializes as a `Card`. Guessing only ever runs on JSON that Gorilla did not write, which is exactly the
context where field names are least under our control and being confidently wrong costs most.

*Some cases carry no information at all.* Two variants with identical field sets, or two field-less
variants, cannot be distinguished by any algorithm. This is the source of the field-less defect —
`SignInError` has three field-less variants and cannot round-trip without a discriminator.

Gorilla always writes the discriminator, so the feature exists solely for foreign or hand-written JSON.
No consumer of that kind is known: nothing in the repository, sample, or tests reads discriminator-less
payloads.

**What this rules out.** Exact-set matching — requiring the payload's field set to equal a variant's
parameter set — was the alternative. It is genuinely correct where correctness is possible: it reads
`CardWithCvv`, rejects the unrelated object, and leaves only the undecidable cases ambiguous. It is ruled
out because it preserves a feature justified by a use case with no evidence behind it, and keeping code
that exists for a hypothetical consumer is the cost this whole change is meant to reduce. If
discriminator-less interop turns out to be a real requirement, exact-set matching is the design to
reinstate — not the subset heuristic.

**Consequences.** The field-less defect deferred under Decision 9 ceases to exist rather than being fixed;
`SignInError` round-trips through the discriminator like every other union.

This is a behavioral break for anyone deserializing payloads without a discriminator. Those payloads are
currently being matched by a heuristic that can select the wrong variant, so the change converts silent
misidentification into a loud error, but callers relying on it will see failures.

`InferVariantFromProperties` and its `ResolvePropertyName` call sites in the guessing path are deleted from
both the `System.Text.Json` and `Newtonsoft.Json` converters, along with the ambiguity diagnostics that
only that path could raise.

## What this does not fix

Generic unions (`[DiscriminatedUnion] partial class Result<T>`) are unsupported today. That is our own
gap, not OneOf's, and removing OneOf neither fixes nor worsens it — though it does remove the structural
obstacle that would have made it painful.

Struct unions become permanently impossible, not merely unsupported. Decision 1 requires variants to
derive from the union, and structs cannot be inherited from. `GetTypeKeyword` in
`DiscriminatedUnionGenerator.cs` still maps `TypeKind.Struct` to `struct` and `record struct`; those
branches describe a shape the design cannot produce and should be rejected rather than emitted. This is a
genuine narrowing — the wrapper representation could in principle have supported value-type unions, and
the abstract-base representation cannot.

### 14. The union constructor is `private`, closing the hierarchy

Unions emit a `private` parameterless constructor rather than `private protected`. Nested variants and
nested sub-unions still derive from the union; nothing else can.

**Rationale.** A union can acquire a subtype in three ways, and the generator only knows two of them:

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

`Match`'s signature is written from (1) and (2) only, so (3) cannot appear in it. The result is a program
that compiles and fails later:

```
Match knows about: paid, refused
  PaidVariant     -> paid
  ExpiredVariant  -> refused
  Cancelled       -> InvalidOperationException: Unknown variant: Cancelled
```

Form (3) is not an exotic mistake. `public sealed record Cancelled(string Reason) : Outcome;` is the
idiomatic hand-rolled C# discriminated union — what most developers write without a generator, and the
obvious-looking way to say "this is also an Outcome." Reaching for `[Variant]` instead requires already
knowing Gorilla's convention.

A `private` constructor makes (3) a compile error at the declaration that caused it:

```
error CS0122: 'Outcome.Outcome()' is inaccessible due to its protection level
```

(1) and (2) are unaffected: variants are generated as nested types and sub-unions must be nested to be
recognised at all — `GetDirectSubUnions` reads `classSymbol.GetTypeMembers()`
(`DiscriminatedUnionGenerator.cs:136`), which sees only nested types. Everything with a legitimate reason
to derive is therefore inside the union and can reach a private member.

With the hierarchy closed, `Match` and `Switch` are total over the types that can exist, and the
`_ => throw new InvalidOperationException($"Unknown variant: …")` arm becomes unreachable rather than a
live runtime path.

**What this rules out.** Deliberately extending a union with a hand-written subtype. That was never
supported — it throws at runtime today — so this converts a runtime failure into a compile-time one. The
compiler's message says "inaccessible constructor" rather than "unions are closed, use `[Variant]`", which
is imprecise, but it appears on the offending line rather than in whichever code path later encountered
the value.

**Consequences.** This restores a property Decision 1 would otherwise remove. Flat unions currently emit a
`private` constructor (`SignInError.g.cs:10`) and cannot be subclassed at all; adopting the hierarchical
path's `private protected` wholesale would give every flat union a hole it does not have today.

It also closes the same hole on hierarchical unions, which have had it since 1.3.0. Any existing
hand-written subtype of a hierarchical union stops compiling — code that was already failing at runtime.

## Related gap, not addressed

Attributes on `[Variant]` parameters are silently dropped. `DiscriminatedUnionGenerator.cs:543` and `:646`
emit variant properties as `public {Type} {Name} { get; }` with nothing carried across, so
`[Variant] public static partial Payment Card([Required] string number)` compiles and does nothing. This
predates the change and is unaffected by it, but it is worth knowing that such attributes are inert rather
than merely unenforced by ASP.NET.

## Confidence

The existing test suite is behavioral rather than snapshot-based: it compiles generated source, emits an
assembly, and invokes it. That means it validates the *contract* rather than the emitted text, so it
largely survives a change of representation. Replacing the foundation of a library is normally a
high-risk move; here the safety net was built for exactly this kind of change, and half the target design
is already in production.
