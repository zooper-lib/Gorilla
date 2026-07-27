using Zooper.Gorilla.Attributes;

namespace Zooper.Gorilla.Sample;

[DiscriminatedUnion]
public abstract partial class SignInError
{
	[Variant]
	public static partial SignInError ServiceUnavailable();

	[Variant]
	public static partial SignInError InvalidCredentials();

	[Variant]
	public static partial SignInError InternalError();
}