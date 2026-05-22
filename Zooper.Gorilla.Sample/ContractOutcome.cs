using System.Text.Json;
using Zooper.Gorilla.Attributes;

namespace Zooper.Gorilla.Sample;

/// <summary>
/// Demonstrates a hierarchical discriminated union where nested unions participate in outer matching.
/// </summary>
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

public static class ContractOutcomeSamples
{
    public static string Describe(ContractOutcome outcome) =>
        outcome.Match(
            success => $"success:{success.ContractId}",
            rejected => rejected.Match(
                validation => $"validation:{validation.Field}",
                conflict => $"conflict:{conflict.ResourceId}",
                security => security.Match(
                    forbidden => "forbidden",
                    unauthorized => $"unauthorized:{unauthorized.Reason}")));

    public static string RoundTrip() =>
        Describe(JsonSerializer.Deserialize<ContractOutcome>(
            JsonSerializer.Serialize<ContractOutcome>(ContractOutcome.Rejected.Security.Unauthorized("expired-session")))!);
}