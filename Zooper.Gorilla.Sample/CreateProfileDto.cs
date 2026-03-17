using Zooper.Gorilla.Attributes;

namespace Zooper.Gorilla.Sample;

/// <summary>
/// Demonstrates a discriminated union with parameterized variants and JSON converter generation.
/// Serializes to: { "type": "Person", "firstName": "John", "lastName": "Doe" }
///            or: { "type": "Company", "companyName": "Acme Corp" }
/// </summary>
[DiscriminatedUnion]
public partial class CreateProfileDto
{
    [Variant]
    public static partial CreateProfileDto Person(string firstName, string lastName);

    [Variant]
    public static partial CreateProfileDto Company(string companyName);
}
