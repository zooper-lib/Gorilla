namespace Zooper.Gorilla.Generators.Tests;

internal static class TestSources
{
    internal const string TopLevelFlatUnion = """
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public sealed partial class EntityState
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

    internal const string NestedUnionInsideClass = """
using Zooper.Gorilla.Attributes;

public partial class Container
{
    [DiscriminatedUnion]
    public sealed partial class State
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
    public sealed partial class State : IContainer
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
        public sealed partial class V1 : IInner
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
    public sealed partial class V1 : IContractA
    {
        [Variant]
        public static partial V1 A();
    }
}

public partial interface IContractB
{
    [DiscriminatedUnion]
    public sealed partial class V1 : IContractB
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
    public sealed partial class V1 : IEntityPayload
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
        public sealed partial class V1 : IEntityPayload
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
    public sealed partial class V1 : IOuter
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
    public sealed partial class V1 : IEntityPayload
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
public sealed partial class Shape
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

    // 4.1 STJ camelCase round-trip, multi-param variant
    public static string StjCamelRoundTrip()
    {
        var options = new Stj.JsonSerializerOptions { PropertyNamingPolicy = Stj.JsonNamingPolicy.CamelCase };
        var json = Stj.JsonSerializer.Serialize(Shape.Rectangle("box", true), options);
        return Describe(Stj.JsonSerializer.Deserialize<Shape>(json, options)!);
    }

    // 4.1 (cont.) STJ snake_case actually transforms the key off the PascalCase property
    public static string StjSnakeKey()
        => Stj.JsonSerializer.Serialize(
            Shape.Rectangle("box", true),
            new Stj.JsonSerializerOptions { PropertyNamingPolicy = Stj.JsonNamingPolicy.SnakeCaseLower });

    // 4.2 STJ hierarchical round-trip under a naming policy (snake_case)
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

    // 4.3 STJ case-insensitive deserialize, mixed-case input keys
    public static string StjCaseInsensitive()
    {
        var options = new Stj.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        const string json = "{\"$type\":\"Rectangle\",\"LABEL\":\"box\",\"ISVISIBLE\":true}";
        return Describe(Stj.JsonSerializer.Deserialize<Shape>(json, options)!);
    }

    // 4.4 STJ inference under camelCase, no discriminator field present
    public static string StjInferenceCamel()
    {
        var options = new Stj.JsonSerializerOptions { PropertyNamingPolicy = Stj.JsonNamingPolicy.CamelCase };
        const string json = "{\"label\":\"box\",\"isVisible\":true}";
        return Describe(Stj.JsonSerializer.Deserialize<Shape>(json, options)!);
    }

    // 4.5 Newtonsoft CamelCase resolver round-trip (flat)
    public static string NewtonsoftCamelRoundTrip()
    {
        var settings = new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() };
        var json = JsonConvert.SerializeObject(Shape.Rectangle("box", true), settings);
        return Describe(JsonConvert.DeserializeObject<Shape>(json, settings)!);
    }

    // 4.5 (cont.) Newtonsoft hierarchical round-trip with CamelCase resolver
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

    // 4.5 (cont.) Newtonsoft snake_case actually transforms the key
    public static string NewtonsoftSnakeKey()
    {
        var settings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() }
        };
        return JsonConvert.SerializeObject(Shape.Rectangle("box", true), settings);
    }

    // 4.6 Newtonsoft inference under camelCase
    public static string NewtonsoftInferenceCamel()
    {
        var settings = new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() };
        const string json = "{\"label\":\"box\",\"isVisible\":true}";
        return Describe(JsonConvert.DeserializeObject<Shape>(json, settings)!);
    }

    // 4.7 Default-options golden output (STJ)
    public static string StjDefaultJson()
        => Stj.JsonSerializer.Serialize(Shape.Rectangle("box", true));

    // 4.7 Default-resolver golden output (Newtonsoft)
    public static string NewtonsoftDefaultJson()
        => JsonConvert.SerializeObject(Shape.Rectangle("box", true));
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
}
