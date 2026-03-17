namespace Zooper.Gorilla.Attributes;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class DiscriminatedUnionAttribute : Attribute
{
    /// <summary>
    /// When true, emits [ValidateNever] to prevent ASP.NET model validation from walking into OneOfBase properties.
    /// Requires the consuming project to reference Microsoft.AspNetCore.Mvc.Core.
    /// </summary>
    public bool SuppressValidation { get; set; }

    /// <summary>
    /// When true (default), generates a System.Text.Json JsonConverter for the discriminated union.
    /// </summary>
    public bool GenerateJsonConverter { get; set; } = true;

    /// <summary>
    /// The JSON property name used as the discriminator field. Defaults to "type".
    /// </summary>
    public string DiscriminatorFieldName { get; set; } = "type";

    /// <summary>
    /// When true, generates a Newtonsoft.Json JsonConverter for the discriminated union.
    /// Requires the consuming project to reference Newtonsoft.Json.
    /// </summary>
    public bool GenerateNewtonsoftJsonConverter { get; set; } = true;
}