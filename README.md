# Gorilla

<img src="icon.png" alt="drawing" width="256"/>

A .NET source generator for creating discriminated unions in C# with OneOf. Inspired by the flutter package [freezed](https://pub.dev/packages/freezed) and the F# discriminated union pattern.
## 🚀 Overview
Gorilla helps you create type-safe, exhaustive discriminated unions in C#. It automatically generates matching methods, type checks, and conversions to make working with union types intuitive and safe.
Gorilla uses [OneOf](https://github.com/mcintyre321/OneOf) under the hood to implement the discriminated union pattern but adds a layer of compile-time convenience and safety through source generation.

## 📦 Installation
``` bash
# Install both packages
dotnet add package Zooper.Gorilla.Attributes
dotnet add package Zooper.Gorilla.Generators
```
## 🔧 Usage
### 1. Define your union type
Add the `[DiscriminatedUnion]` attribute with the types that make up the union:
``` csharp
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public partial class SignInError
{
	[Variant]
	public static partial SignInError ServiceUnavailable();

	[Variant]
	public static partial SignInError InvalidCredentials();

	[Variant]
	public static partial SignInError InternalError();
}
```
### 2. Use your union type
``` csharp
return SignInError.ServiceUnavailable();
```

## 🤝 Contributing
Contributions are welcome! Please feel free to submit a Pull Request.
## 📄 License
This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
## 🙏 Acknowledgements
- [OneOf](https://github.com/mcintyre321/OneOf) - The foundation for this library
- [F# Discriminated Unions](https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/discriminated-unions) - The inspiration for the pattern

Made with ❤️ by the Zooper team
