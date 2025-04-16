using System;
using Zooper.Gorilla.Attributes;

namespace Zooper.Gorilla.Sample;

/// <summary>
/// A sample class which shows how to use the DiscriminatedUnion attribute.
/// </summary>
[DiscriminatedUnion]
public partial class SignUpError
{
	[Variant]
	public static partial SignUpError ServiceUnavailable();

	[Variant]
	public static partial SignUpError InvalidCredentials();

	[Variant]
	public static partial SignUpError InternalError(Exception exception);
}