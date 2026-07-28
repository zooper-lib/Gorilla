namespace Zooper.Gorilla.Generators.Tests;

internal static class TestSources
{
    internal const string TopLevelFlatUnion = """
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial class EntityState
{
    [Variant]
    public static partial EntityState Created();

    [Variant]
    public static partial EntityState Archived();

    [Variant]
    public static partial EntityState Standard(string category, bool isVisible);

    [Variant]
    public static partial EntityState Composite(string parentId, int childCount);

    [Variant]
    public static partial EntityState External(string provider, string externalId);
}

public static class Usage
{
    public static string Run()
    {
        var state = EntityState.Standard("alpha", true);
        return state.Match(
            created => "created",
            archived => "archived",
            standard => standard.Category,
            composite => composite.ParentId,
            external => external.Provider);
    }
}
""";

    internal const string NamedMatchUsage = """
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial class PersistError
{
    [Variant]
    public static partial PersistError StorageUnavailable();

    [Variant]
    public static partial PersistError Conflict(string reason);

    [Variant]
    public static partial PersistError ConcurrencyConflict(string reason);
}

public static class Usage
{
    public static string Run()
    {
        var error = PersistError.Conflict("duplicate");

        // Named arguments, deliberately out of declaration order.
        var matched = error.Match(
            conflict: c => "conflict:" + c.Reason,
            concurrencyConflict: cc => "concurrency:" + cc.Reason,
            storageUnavailable: _ => "storage");

        error.Switch(
            concurrencyConflict: _ => { },
            storageUnavailable: _ => { },
            conflict: _ => { });

        return matched;
    }
}
""";

    internal const string NestedUnionInsideClass = """
using Zooper.Gorilla.Attributes;

public partial class Container
{
    [DiscriminatedUnion]
    public abstract partial class State
    {
        [Variant]
        public static partial State Started();

        [Variant]
        public static partial State Stopped(string reason);
    }
}

public static class Usage
{
    public static string Run()
    {
        var state = Container.State.Stopped("maintenance");
        return state.Match(
            started => "started",
            stopped => stopped.Reason);
    }
}
""";

    internal const string NestedUnionInsideInterface = """
using Zooper.Gorilla.Attributes;

public partial interface IContainer
{
    [DiscriminatedUnion]
    public abstract partial class State : IContainer
    {
        [Variant]
        public static partial State Ready();

        [Variant]
        public static partial State Faulted(string reason);
    }
}

public static class Usage
{
    public static string Run()
    {
        var state = IContainer.State.Faulted("boom");
        return state.Match(
            ready => "ready",
            faulted => faulted.Reason);
    }
}
""";

    internal const string DeeplyNestedUnion = """
using Zooper.Gorilla.Attributes;

public partial interface IOuter
{
    public partial interface IInner
    {
        [DiscriminatedUnion]
        public abstract partial class V1 : IInner
        {
            [Variant]
            public static partial V1 A();

            [Variant]
            public static partial V1 B(string code);
        }
    }
}

public static class Usage
{
    public static string Run()
    {
        var value = IOuter.IInner.V1.B("x");
        return value.Match(
            a => "a",
            b => b.Code);
    }
}
""";

    internal const string DistinctHintNames = """
using Zooper.Gorilla.Attributes;

public partial interface IContractA
{
    [DiscriminatedUnion]
    public abstract partial class V1 : IContractA
    {
        [Variant]
        public static partial V1 A();
    }
}

public partial interface IContractB
{
    [DiscriminatedUnion]
    public abstract partial class V1 : IContractB
    {
        [Variant]
        public static partial V1 B();
    }
}
""";

    internal const string NestedImplementsContainingInterface = """
using Zooper.Gorilla.Attributes;

public partial interface IEntityPayload
{
    [DiscriminatedUnion]
    public abstract partial class V1 : IEntityPayload
    {
        [Variant]
        public static partial V1 Created();

        [Variant]
        public static partial V1 Standard(string category, bool isVisible);
    }
}

public static class Usage
{
    public static string Run()
    {
        IEntityPayload value = IEntityPayload.V1.Standard("alpha", true);
        return ((IEntityPayload.V1)value).Match(
            created => "created",
            standard => standard.Category);
    }
}
""";

    internal const string PrimaryNestedExample = """
using Zooper.Gorilla.Attributes;

public readonly record struct EntityId(string Value);
public readonly record struct EntityName(string Value);
public readonly record struct EntityOrder(int Value);

public partial interface IEntityCreatedContract
{
    public sealed record V1(
        EntityId EntityId,
        EntityName Name,
        IEntityPayload Info,
        EntityOrder Order) : IEntityCreatedContract;

    public partial interface IEntityPayload
    {
        [DiscriminatedUnion]
        public abstract partial class V1 : IEntityPayload
        {
            [Variant]
            public static partial V1 Created();

            [Variant]
            public static partial V1 Archived();

            [Variant]
            public static partial V1 Standard(string category, bool isVisible);

            [Variant]
            public static partial V1 Composite(string parentId, int childCount);

            [Variant]
            public static partial V1 External(string provider, string externalId);
        }
    }
}

public static class Usage
{
    public static string Run()
    {
        var info = IEntityCreatedContract.IEntityPayload.V1.Standard("alpha", true);
        return info.Match(
            created => "created",
            archived => "archived",
            standard => standard.Category,
            composite => composite.ParentId,
            external => external.Provider);
    }
}
""";

    internal const string AbstractHierarchicalUnion = """
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial record ContractOutcome
{
    [Variant]
    public static partial ContractOutcome Success(string contractId);

    [DiscriminatedUnion]
    public abstract partial record Rejected : ContractOutcome
    {
        [Variant]
        public static partial Rejected Validation(string field);

        [Variant]
        public static partial Rejected Conflict(string resourceId);

        [DiscriminatedUnion]
        public abstract partial record Security : Rejected
        {
            [Variant]
            public static partial Security Forbidden();

            [Variant]
            public static partial Security Unauthorized(string reason);
        }
    }
}

public static class Usage
{
    public static string OuterMatch()
    {
        ContractOutcome outcome = ContractOutcome.Success("c-42");
        return outcome.Match(
            success => success.ContractId,
            rejected => "rejected");
    }

    public static string DispatchSubUnionThroughOuter()
    {
        ContractOutcome outcome = ContractOutcome.Rejected.Validation("field");
        return outcome.Match(
            success => "success",
            rejected => rejected.Match(
                validation => $"validation:{validation.Field}",
                conflict => $"conflict:{conflict.ResourceId}",
                security => "security"));
    }

    public static string DoubleNested()
    {
        ContractOutcome outcome = ContractOutcome.Rejected.Security.Unauthorized("expired");
        return outcome.Match(
            success => "success",
            rejected => rejected.Match(
                validation => $"validation:{validation.Field}",
                conflict => $"conflict:{conflict.ResourceId}",
                security => security.Match(
                    forbidden => "forbidden",
                    unauthorized => unauthorized.Reason)));
    }
}
""";

    internal const string NonPartialContainingType = """
using Zooper.Gorilla.Attributes;

public interface IOuter
{
    [DiscriminatedUnion]
    public abstract partial class V1 : IOuter
    {
        [Variant]
        public static partial V1 A();
    }
}
""";

    internal const string NestedSealedJsonRoundTrip = """
using System.Text.Json;
using Zooper.Gorilla.Attributes;

public partial interface IEntityPayload
{
    [DiscriminatedUnion]
    public abstract partial class V1 : IEntityPayload
    {
        [Variant]
        public static partial V1 Created();

        [Variant]
        public static partial V1 Standard(string category, bool isVisible);
    }
}

public static class Usage
{
    public static string RoundTrip()
    {
        var value = IEntityPayload.V1.Standard("alpha", true);
        var json = JsonSerializer.Serialize(value);
        var roundTripped = JsonSerializer.Deserialize<IEntityPayload.V1>(json)!;
        return roundTripped.Match(
            created => "created",
            standard => $"{standard.Category}:{standard.IsVisible}");
    }
}
""";

    internal const string OptionsAwareConverters = """
using Stj = System.Text.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial class Shape
{
    [Variant]
    public static partial Shape Rectangle(string label, bool isVisible);

    [Variant]
    public static partial Shape Circle(double radius);
}

[DiscriminatedUnion]
public abstract partial record Outcome
{
    [Variant]
    public static partial Outcome Success(string contractId);

    [DiscriminatedUnion]
    public abstract partial record Rejected : Outcome
    {
        [Variant]
        public static partial Rejected Validation(string fieldName);
    }
}

public static class Usage
{
    private static string Describe(Shape shape) => shape.Match(
        rectangle => $"{rectangle.Label}:{rectangle.IsVisible}",
        circle => $"circle:{circle.Radius}");

    // STJ camelCase round-trip, multi-param variant
    public static string StjCamelRoundTrip()
    {
        var options = new Stj.JsonSerializerOptions { PropertyNamingPolicy = Stj.JsonNamingPolicy.CamelCase };
        var json = Stj.JsonSerializer.Serialize(Shape.Rectangle("box", true), options);
        return Describe(Stj.JsonSerializer.Deserialize<Shape>(json, options)!);
    }

    // STJ snake_case actually transforms the key off the PascalCase property
    public static string StjSnakeKey()
        => Stj.JsonSerializer.Serialize(
            Shape.Rectangle("box", true),
            new Stj.JsonSerializerOptions { PropertyNamingPolicy = Stj.JsonNamingPolicy.SnakeCaseLower });

    // STJ hierarchical round-trip under a naming policy (snake_case)
    public static string StjHierarchicalRoundTrip()
    {
        var options = new Stj.JsonSerializerOptions { PropertyNamingPolicy = Stj.JsonNamingPolicy.SnakeCaseLower };
        Outcome value = Outcome.Rejected.Validation("email");
        var json = Stj.JsonSerializer.Serialize(value, options);
        var back = Stj.JsonSerializer.Deserialize<Outcome>(json, options)!;
        return back.Match(
            success => $"success:{success.ContractId}",
            rejected => rejected.Match(validation => $"validation:{validation.FieldName}"));
    }

    // STJ case-insensitive deserialize, mixed-case input keys
    public static string StjCaseInsensitive()
    {
        var options = new Stj.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        const string json = "{\"$type\":\"Rectangle\",\"LABEL\":\"box\",\"ISVISIBLE\":true}";
        return Describe(Stj.JsonSerializer.Deserialize<Shape>(json, options)!);
    }

    // Newtonsoft CamelCase resolver round-trip (flat)
    public static string NewtonsoftCamelRoundTrip()
    {
        var settings = new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() };
        var json = JsonConvert.SerializeObject(Shape.Rectangle("box", true), settings);
        return Describe(JsonConvert.DeserializeObject<Shape>(json, settings)!);
    }

    // Newtonsoft hierarchical round-trip with CamelCase resolver
    public static string NewtonsoftHierarchicalRoundTrip()
    {
        var settings = new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() };
        Outcome value = Outcome.Rejected.Validation("email");
        var json = JsonConvert.SerializeObject(value, settings);
        var back = JsonConvert.DeserializeObject<Outcome>(json, settings)!;
        return back.Match(
            success => $"success:{success.ContractId}",
            rejected => rejected.Match(validation => $"validation:{validation.FieldName}"));
    }

    // Newtonsoft snake_case actually transforms the key
    public static string NewtonsoftSnakeKey()
    {
        var settings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() }
        };
        return JsonConvert.SerializeObject(Shape.Rectangle("box", true), settings);
    }

    // Default-options golden output (STJ)
    public static string StjDefaultJson()
        => Stj.JsonSerializer.Serialize(Shape.Rectangle("box", true));

    // Default-resolver golden output (Newtonsoft)
    public static string NewtonsoftDefaultJson()
        => JsonConvert.SerializeObject(Shape.Rectangle("box", true));
}
""";

    // Same simple-name unions declared in two different namespaces. Before the fix the
    // source hint ignored the namespace, so both produced "SectorEvolver.g.cs" and the
    // collision dropped/duplicated generated sources (CS8795/CS0101/CS0111 downstream).
    internal const string SameNameUnionsInDifferentNamespaces = """
using Zooper.Gorilla.Attributes;

namespace Modules.Economy.Domain.Construction.Aggregates
{
    [DiscriminatedUnion]
    public abstract partial class SectorEvolver
    {
        [Variant]
        public static partial SectorEvolver Evolve(string id);
    }
}

namespace Modules.Economy.Domain.Trade.Aggregates
{
    [DiscriminatedUnion]
    public abstract partial class SectorEvolver
    {
        [Variant]
        public static partial SectorEvolver Evolve(string id);
    }
}
""";

    // Same nested union name under same-named containing types, in two different
    // namespaces. The containing-type path matched ("Sector.Evolver"), so the namespace
    // was the only differentiator and was previously omitted from the hint.
    internal const string SameNameNestedUnionsInDifferentNamespaces = """
using Zooper.Gorilla.Attributes;

namespace Modules.Economy.Domain.Construction.Aggregates
{
    public partial class Sector
    {
        [DiscriminatedUnion]
        public abstract partial class Evolver
        {
            [Variant]
            public static partial Evolver Evolve(string id);
        }
    }
}

namespace Modules.Economy.Domain.Trade.Aggregates
{
    public partial class Sector
    {
        [DiscriminatedUnion]
        public abstract partial class Evolver
        {
            [Variant]
            public static partial Evolver Evolve(string id);
        }
    }
}
""";

    internal const string HierarchicalJsonRoundTrip = """
using System.Text.Json;
using Newtonsoft.Json;
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial record ContractOutcome
{
    [Variant]
    public static partial ContractOutcome Success(string contractId);

    [DiscriminatedUnion]
    public abstract partial record Rejected : ContractOutcome
    {
        [Variant]
        public static partial Rejected Validation(string field);

        [Variant]
        public static partial Rejected Conflict(string resourceId);

        [DiscriminatedUnion]
        public abstract partial record Security : Rejected
        {
            [Variant]
            public static partial Security Forbidden();

            [Variant]
            public static partial Security Unauthorized(string reason);
        }
    }
}

public static class Usage
{
    public static string RoundTripLeaf()
    {
        ContractOutcome value = ContractOutcome.Success("c-42");
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<ContractOutcome>(json)!;
        return roundTripped.Match(
            success => success.ContractId,
            rejected => "rejected");
    }

    public static string RoundTripSubUnion()
    {
        ContractOutcome value = ContractOutcome.Rejected.Security.Unauthorized("expired");
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<ContractOutcome>(json)!;
        return roundTripped.Match(
            success => "success",
            rejected => rejected.Match(
                validation => $"validation:{validation.Field}",
                conflict => $"conflict:{conflict.ResourceId}",
                security => security.Match(
                    forbidden => "forbidden",
                    unauthorized => unauthorized.Reason)));
    }

    public static string RoundTripInnerNewtonsoft()
    {
        var value = ContractOutcome.Rejected.Security.Unauthorized("expired");
        var json = JsonConvert.SerializeObject(value);
        var roundTripped = JsonConvert.DeserializeObject<ContractOutcome.Rejected.Security>(json)!;
        return roundTripped.Match(
            forbidden => "forbidden",
            unauthorized => unauthorized.Reason);
    }
}
""";

    internal const string SwitchAndAccessors = """
using System;
using System.Collections.Generic;
using System.Reflection;
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial record Pay
{
    [Variant]
    public static partial Pay Card(string number);

    [Variant]
    public static partial Pay Cash();
}

[DiscriminatedUnion]
public abstract partial record Outcome
{
    [Variant]
    public static partial Outcome Success(string contractId);

    [DiscriminatedUnion]
    public abstract partial record Rejected : Outcome
    {
        [Variant]
        public static partial Rejected Validation(string field);

        [Variant]
        public static partial Rejected Conflict(string resourceId);
    }
}

public static class Usage
{
    public static string SwitchWithoutSubUnions()
    {
        var result = "none";
        Pay.Card("4111").Switch(
            card => result = "card:" + card.Number,
            cash => result = "cash");
        return result;
    }

    public static string SwitchWithSubUnions()
    {
        var result = "none";
        Outcome value = Outcome.Rejected.Validation("email");
        value.Switch(
            success => result = "success",
            rejected => result = "rejected:" + rejected.AsValidation().Field);
        return result;
    }

    public static string VariantAccessors()
    {
        Pay pay = Pay.Card("4111");
        var parts = new List<string>
        {
            "IsCard=" + pay.IsCard,
            "IsCash=" + pay.IsCash,
            "AsCard=" + pay.AsCard().Number,
            "TryPickCard=" + pay.TryPickCard(out var card) + ":" + card.Number,
            "TryPickCash=" + pay.TryPickCash(out var cash) + ":" + (cash is null),
        };

        try
        {
            pay.AsCash();
            parts.Add("AsCash=did-not-throw");
        }
        catch (InvalidOperationException ex)
        {
            parts.Add("AsCash=" + ex.Message);
        }

        return string.Join("|", parts);
    }

    public static string SubUnionAccessors()
    {
        Outcome value = Outcome.Rejected.Validation("email");
        return "IsSuccess=" + value.IsSuccess
            + "|IsRejected=" + value.IsRejected
            + "|AsRejected=" + value.AsRejected().AsValidation().Field
            + "|TryPickRejected=" + value.TryPickRejected(out var rejected) + ":" + rejected.IsValidation
            + "|TryPickSuccess=" + value.TryPickSuccess(out var success) + ":" + (success is null);
    }

    // Every public property getter is invoked. AsX must be a method, or a property walker
    // (ASP.NET validation, a debugger watch window, a mapper) hits a throwing getter.
    public static string WalkPublicProperties()
    {
        object pay = Pay.Card("4111");
        var names = new List<string>();

        foreach (var property in pay.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            property.GetValue(pay);
            names.Add(property.Name);
        }

        names.Sort(StringComparer.Ordinal);
        return string.Join(",", names);
    }

    public static string NoPositionalMembers()
    {
        var names = new List<string>();
        foreach (var member in typeof(Pay).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            if (member.Name is "IsT0" or "AsT0" or "TryPickT0" or "Value" or "Index")
            {
                names.Add(member.Name);
            }
        }
        return string.Join(",", names);
    }
}
""";

    internal const string TenVariantUnion = """
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial record Step
{
    [Variant] public static partial Step S1();
    [Variant] public static partial Step S2();
    [Variant] public static partial Step S3();
    [Variant] public static partial Step S4();
    [Variant] public static partial Step S5();
    [Variant] public static partial Step S6();
    [Variant] public static partial Step S7();
    [Variant] public static partial Step S8();
    [Variant] public static partial Step S9();
    [Variant] public static partial Step S10(string tag);
    [Variant] public static partial Step S11(string tag);
}

public static class Usage
{
    public static string Run()
    {
        Step step = Step.S11("done");

        var matched = step.Match(
            s1 => "1", s2 => "2", s3 => "3", s4 => "4", s5 => "5", s6 => "6",
            s7 => "7", s8 => "8", s9 => "9", s10 => "10:" + s10.Tag, s11 => "11:" + s11.Tag);

        var switched = "none";
        step.Switch(
            s1 => switched = "1", s2 => switched = "2", s3 => switched = "3",
            s4 => switched = "4", s5 => switched = "5", s6 => switched = "6",
            s7 => switched = "7", s8 => switched = "8", s9 => switched = "9",
            s10 => switched = "10", s11 => switched = "11:" + s11.Tag);

        return matched + "|" + switched;
    }
}
""";

    internal const string EqualitySemantics = """
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial record RecordPay
{
    [Variant]
    public static partial RecordPay Card(string number);
}

[DiscriminatedUnion]
public abstract partial class ClassPay
{
    [Variant]
    public static partial ClassPay Card(string number);
}

public static class Usage
{
    public static string RecordEquality()
    {
        var a = RecordPay.Card("4111");
        var b = RecordPay.Card("4111");
        return a.Equals(b) + "|" + (a.GetHashCode() == b.GetHashCode()) + "|" + a.ToString();
    }

    public static string ClassEquality()
    {
        var a = ClassPay.Card("4111");
        var b = ClassPay.Card("4111");
        return a.Equals(b) + "|" + a.Equals(a);
    }
}
""";

    internal const string DeserializationFailures = """
using System;
using Stj = System.Text.Json;
using Newtonsoft.Json;
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial record Pay
{
    [Variant]
    public static partial Pay Card(string number);

    [Variant]
    public static partial Pay Cash();
}

[DiscriminatedUnion]
public abstract partial record Outcome
{
    [Variant]
    public static partial Outcome Success(string contractId);

    [DiscriminatedUnion]
    public abstract partial record Rejected : Outcome
    {
        [Variant]
        public static partial Rejected Validation(string field);
    }
}

[DiscriminatedUnion(DiscriminatorFieldName = "kind")]
public abstract partial record Kinded
{
    [Variant]
    public static partial Kinded Alpha();
}

public static class Usage
{
    private static string Capture(Action action)
    {
        try
        {
            action();
            return "did-not-throw";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public static string StjMissing()
        => Capture(() => Stj.JsonSerializer.Deserialize<Pay>("{\"bogus\":1}"));

    public static string StjUnrecognized()
        => Capture(() => Stj.JsonSerializer.Deserialize<Pay>("{\"$type\":\"Nonexistent\"}"));

    public static string StjMissingWithSubUnions()
        => Capture(() => Stj.JsonSerializer.Deserialize<Outcome>("{\"bogus\":1}"));

    public static string StjUnrecognizedWithSubUnions()
        => Capture(() => Stj.JsonSerializer.Deserialize<Outcome>("{\"$type\":\"Nonexistent\"}"));

    public static string StjConfiguredFieldName()
        => Capture(() => Stj.JsonSerializer.Deserialize<Kinded>("{\"bogus\":1}"));

    public static string StjForeignObject()
        => Capture(() => Stj.JsonSerializer.Deserialize<Pay>("{\"number\":\"4111\",\"junk\":true}"));

    public static string NewtonsoftMissing()
        => Capture(() => JsonConvert.DeserializeObject<Pay>("{\"bogus\":1}"));

    public static string NewtonsoftUnrecognized()
        => Capture(() => JsonConvert.DeserializeObject<Pay>("{\"$type\":\"Nonexistent\"}"));

    public static string NewtonsoftMissingWithSubUnions()
        => Capture(() => JsonConvert.DeserializeObject<Outcome>("{\"bogus\":1}"));

    // A discriminator naming a sub-union's variant still resolves.
    public static string SubUnionDiscriminatorResolves()
    {
        var value = Stj.JsonSerializer.Deserialize<Outcome>("{\"$type\":\"Validation\",\"field\":\"email\"}")!;
        return value.Match(
            success => "success",
            rejected => "rejected:" + rejected.AsValidation().Field);
    }
}
""";

    internal const string RoundTripEdgeCases = """
using System.Text.Json;
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial record Pay
{
    [Variant]
    public static partial Pay Card(string number);

    [Variant]
    public static partial Pay Cash();

    [Variant]
    public static partial Pay CardWithCvv(string number, string cvv);
}

// Three field-less variants: undecidable without a discriminator, which is why the old
// property-set inference could not round-trip this union at all.
[DiscriminatedUnion]
public abstract partial record SignInError
{
    [Variant] public static partial SignInError ServiceUnavailable();
    [Variant] public static partial SignInError InvalidCredentials();
    [Variant] public static partial SignInError InternalError();
}

// Two variants with identical parameter sets.
[DiscriminatedUnion]
public abstract partial record Message
{
    [Variant] public static partial Message Text(string body);
    [Variant] public static partial Message Memo(string body);
}

public static class Usage
{
    public static string FieldlessRoundTrip()
    {
        SignInError value = SignInError.InvalidCredentials();
        var json = JsonSerializer.Serialize(value);
        return json + "|" + JsonSerializer.Deserialize<SignInError>(json)!.Match(
            serviceUnavailable => "serviceUnavailable",
            invalidCredentials => "invalidCredentials",
            internalError => "internalError");
    }

    public static string IdenticalShapesRoundTrip()
    {
        Message value = Message.Memo("hi");
        var json = JsonSerializer.Serialize(value);
        return json + "|" + JsonSerializer.Deserialize<Message>(json)!.Match(
            text => "text:" + text.Body,
            memo => "memo:" + memo.Body);
    }

    public static string SupersetVariantReads()
    {
        var value = JsonSerializer.Deserialize<Pay>("{\"$type\":\"CardWithCvv\",\"number\":\"4111\",\"cvv\":\"123\"}")!;
        return value.Match(
            card => "card:" + card.Number,
            cash => "cash",
            cardWithCvv => "cardWithCvv:" + cardWithCvv.Number + ":" + cardWithCvv.Cvv);
    }

    public static string GoldenPayloadVariant()
        => JsonSerializer.Serialize<Pay>(Pay.Card("4111"));

    public static string GoldenFieldlessVariant()
        => JsonSerializer.Serialize<Pay>(Pay.Cash());

    // A document written before this change, read after it.
    public static string StoredDocumentStillReads()
        => JsonSerializer.Deserialize<Pay>("{\"$type\":\"Card\",\"number\":\"4111\"}")!.Match(
            card => "card:" + card.Number,
            cash => "cash",
            cardWithCvv => "cardWithCvv");
}
""";

    // A hand-written subtype outside the union body. The union's constructor is private, so
    // this is a compile error at the declaration rather than an InvalidOperationException at
    // dispatch time.
    internal const string ExternalSubtype = """
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial record Outcome
{
    [Variant]
    public static partial Outcome Paid(string id);
}

public sealed record Cancelled(string Reason) : Outcome;
""";

    internal const string NonAbstractUnion = """
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public sealed partial class EntityState
{
    [Variant]
    public static partial EntityState Created();
}
""";

    internal const string PlainPartialUnion = """
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public partial record Outcome
{
    [Variant]
    public static partial Outcome Paid(string id);
}
""";

    // Accessor names derive from variant names, so a union declaring its own IsCard collides.
    internal const string AccessorNameCollision = """
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial record Pay
{
    [Variant]
    public static partial Pay Card(string number);

    public bool IsCard => false;
}
""";

    // A union that declares nothing. Degenerate, but it must not emit source that fails to compile.
    internal const string VariantlessUnion = """
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial record Empty
{
}
""";

    // A union whose only subtypes are sub-unions — no [Variant] of its own.
    internal const string SubUnionsOnlyUnion = """
using System.Text.Json;
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial record Outcome
{
    [DiscriminatedUnion]
    public abstract partial record Rejected : Outcome
    {
        [Variant]
        public static partial Rejected Validation(string field);
    }
}

public static class Usage
{
    public static string MatchAndRoundTrip()
    {
        Outcome value = Outcome.Rejected.Validation("email");
        var json = JsonSerializer.Serialize(value);
        var back = JsonSerializer.Deserialize<Outcome>(json)!;

        var switched = "none";
        back.Switch(rejected => switched = "rejected");

        return json + "|" + back.Match(rejected => rejected.AsValidation().Field) + "|" + switched;
    }
}
""";

    // Payloads beyond string/bool/int: collections, enums, nullables, and another union.
    internal const string PayloadTypes = """
using System.Collections.Generic;
using Stj = System.Text.Json;
using Newtonsoft.Json;
using Zooper.Gorilla.Attributes;

public enum Severity { Low, High }

[DiscriminatedUnion]
public abstract partial record Inner
{
    [Variant]
    public static partial Inner Leaf(int value);
}

[DiscriminatedUnion]
public abstract partial record Signal
{
    [Variant]
    public static partial Signal Tagged(List<string> tags, Severity severity);

    [Variant]
    public static partial Signal Optional(string? note);

    [Variant]
    public static partial Signal Wrapped(Inner payload);
}

public static class Usage
{
    private static string Describe(Signal signal) => signal.Match(
        tagged => "tagged:" + string.Join("+", tagged.Tags) + ":" + tagged.Severity,
        optional => "optional:" + (optional.Note ?? "<null>"),
        wrapped => "wrapped:" + wrapped.Payload.AsLeaf().Value);

    public static string StjCollectionAndEnum()
    {
        var json = Stj.JsonSerializer.Serialize<Signal>(Signal.Tagged(new List<string> { "a", "b" }, Severity.High));
        return json + "|" + Describe(Stj.JsonSerializer.Deserialize<Signal>(json)!);
    }

    public static string StjNullPayload()
    {
        var json = Stj.JsonSerializer.Serialize<Signal>(Signal.Optional(null));
        return json + "|" + Describe(Stj.JsonSerializer.Deserialize<Signal>(json)!);
    }

    public static string StjUnionInsideUnion()
    {
        var json = Stj.JsonSerializer.Serialize<Signal>(Signal.Wrapped(Inner.Leaf(7)));
        return json + "|" + Describe(Stj.JsonSerializer.Deserialize<Signal>(json)!);
    }

    public static string NewtonsoftCollectionAndEnum()
    {
        var json = JsonConvert.SerializeObject(Signal.Tagged(new List<string> { "a", "b" }, Severity.High));
        return json + "|" + Describe(JsonConvert.DeserializeObject<Signal>(json)!);
    }

    public static string NewtonsoftUnionInsideUnion()
    {
        var json = JsonConvert.SerializeObject(Signal.Wrapped(Inner.Leaf(7)));
        return json + "|" + Describe(JsonConvert.DeserializeObject<Signal>(json)!);
    }

    public static string NewtonsoftNullValue()
        => JsonConvert.DeserializeObject<Signal>("null") is null ? "null" : "not-null";
}
""";

    internal const string ConfiguredDiscriminatorRoundTrip = """
using Stj = System.Text.Json;
using Newtonsoft.Json;
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion(DiscriminatorFieldName = "kind")]
public abstract partial record Kinded
{
    [Variant]
    public static partial Kinded Alpha(string name);

    [Variant]
    public static partial Kinded Beta();
}

[DiscriminatedUnion]
public abstract partial record Pay
{
    [Variant]
    public static partial Pay Card(string number);
}

public static class Usage
{
    private static string Describe(Kinded value) => value.Match(
        alpha => "alpha:" + alpha.Name,
        beta => "beta");

    public static string StjRoundTrip()
    {
        var json = Stj.JsonSerializer.Serialize<Kinded>(Kinded.Alpha("first"));
        return json + "|" + Describe(Stj.JsonSerializer.Deserialize<Kinded>(json)!);
    }

    public static string NewtonsoftRoundTrip()
    {
        var json = JsonConvert.SerializeObject(Kinded.Alpha("first"));
        return json + "|" + Describe(JsonConvert.DeserializeObject<Kinded>(json)!);
    }

    // Gorilla writes the variant's C# name; a differently-cased value still resolves.
    public static string StjDiscriminatorIsCaseInsensitive()
        => Stj.JsonSerializer.Deserialize<Pay>("{\"$type\":\"card\",\"number\":\"4111\"}")!
            .Match(card => "card:" + card.Number);

    public static string NewtonsoftDiscriminatorIsCaseInsensitive()
        => JsonConvert.DeserializeObject<Pay>("{\"$type\":\"CARD\",\"number\":\"4111\"}")!
            .Match(card => "card:" + card.Number);
}
""";

    internal const string NewtonsoftEdgeCases = """
using Newtonsoft.Json;
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial record SignInError
{
    [Variant] public static partial SignInError ServiceUnavailable();
    [Variant] public static partial SignInError InvalidCredentials();
    [Variant] public static partial SignInError InternalError();
}

[DiscriminatedUnion]
public abstract partial record Message
{
    [Variant] public static partial Message Text(string body);
    [Variant] public static partial Message Memo(string body);
}

public static class Usage
{
    public static string FieldlessRoundTrip()
    {
        var json = JsonConvert.SerializeObject(SignInError.InvalidCredentials());
        return json + "|" + JsonConvert.DeserializeObject<SignInError>(json)!.Match(
            serviceUnavailable => "serviceUnavailable",
            invalidCredentials => "invalidCredentials",
            internalError => "internalError");
    }

    public static string IdenticalShapesRoundTrip()
    {
        var json = JsonConvert.SerializeObject(Message.Memo("hi"));
        return json + "|" + JsonConvert.DeserializeObject<Message>(json)!.Match(
            text => "text:" + text.Body,
            memo => "memo:" + memo.Body);
    }

    public static string NullSerializesToNull()
        => JsonConvert.SerializeObject((Message?)null);
}
""";

    internal const string InternalSealedRecordUnion = """
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
internal sealed partial record Outcome
{
    [Variant]
    public static partial Outcome Paid(string id);
}
""";

    internal const string StructUnion = """
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public partial struct Coord
{
    [Variant]
    public static partial Coord Origin();
}
""";
}
