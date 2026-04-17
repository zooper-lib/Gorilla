namespace Zooper.Gorilla.Attributes;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class DiscriminatedUnionAttribute : Attribute
{
    /// <summary>
    /// When true, emits [ValidateNever] to prevent ASP.NET model validation from walking into OneOfBase properties.
    /// Auto-enabled when the consuming project references Microsoft.AspNetCore.Mvc.Core. Set to false to suppress.
    /// </summary>
    public bool SuppressValidation { get; set; }

    /// <summary>
    /// The JSON property name used as the discriminator field. Defaults to "$type".
    /// </summary>
    public string DiscriminatorFieldName { get; set; } = "$type";

    /// <summary>
    /// When true, generates a System.Text.Json JsonConverter.
    /// Auto-enabled when the consuming project references System.Text.Json. Set to false to suppress.
    /// </summary>
    public bool GenerateJsonConverter { get; set; } = true;

    /// <summary>
    /// When true, generates a Newtonsoft.Json JsonConverter.
    /// Auto-enabled when the consuming project references Newtonsoft.Json. Set to false to suppress.
    /// </summary>
    public bool GenerateNewtonsoftJsonConverter { get; set; } = true;
}