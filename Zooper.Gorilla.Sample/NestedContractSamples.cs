using Zooper.Gorilla.Attributes;

namespace Zooper.Gorilla.Sample;

public readonly record struct EntityId(string Value);

public readonly record struct EntityName(string Value);

public readonly record struct EntityOrder(int Value);

/// <summary>
/// Demonstrates a flat discriminated union nested inside versioned contract types.
/// </summary>
public partial interface IEntityCreatedContract
{
    public sealed record ContractVersion1(
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

public static class NestedContractSamples
{
    public static IEntityCreatedContract CreateContract() =>
        new IEntityCreatedContract.ContractVersion1(
            new EntityId("entity-42"),
            new EntityName("Orders"),
            IEntityCreatedContract.IEntityPayload.V1.Standard("alpha", true),
            new EntityOrder(1));

    public static string DescribePayload(IEntityCreatedContract.IEntityPayload.V1 payload) =>
        payload.Match(
            created => "created",
            archived => "archived",
            standard => $"standard:{standard.Category}:{standard.IsVisible}",
            composite => $"composite:{composite.ParentId}:{composite.ChildCount}",
            external => $"external:{external.Provider}:{external.ExternalId}");
}