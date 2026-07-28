namespace Zooper.Gorilla.Generators.Tests;

/// <summary>
/// Sources for generic unions, converter registration, and the runtime-type discriminator fix.
/// Kept apart from <see cref="TestSources"/> only to keep that file navigable.
/// </summary>
internal static class GenericUnionSources
{
    internal const string GenericUnions = """
using System.Collections.Generic;
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial record Disclosure<T>
{
    [Variant]
    public static partial Disclosure<T> Visible(T value);

    [Variant]
    public static partial Disclosure<T> Many(IReadOnlyList<T> values);

    [Variant]
    public static partial Disclosure<T> NotProvided();

    [Variant]
    public static partial Disclosure<T> Redacted();
}

[DiscriminatedUnion]
public abstract partial class Result<TValue, TError>
{
    [Variant]
    public static partial Result<TValue, TError> Ok(TValue value);

    [Variant]
    public static partial Result<TValue, TError> Fail(TError error);
}

public static class Usage
{
    private static string Describe<T>(Disclosure<T> disclosure) => disclosure.Match(
        visible => $"visible:{visible.Value}",
        many => $"many:{many.Values.Count}",
        notProvided => "notProvided",
        redacted => "redacted");

    public static string MatchVisible() => Describe(Disclosure<string>.Visible("x"));

    public static string MatchListPayload() => Describe(Disclosure<int>.Many(new[] { 1, 2, 3 }));

    public static string MatchPayloadFree() => Describe(Disclosure<string>.NotProvided());

    public static string SwitchVisible()
    {
        var captured = "none";
        Disclosure<string>.Visible("y").Switch(
            visible => captured = $"visible:{visible.Value}",
            many => captured = "many",
            notProvided => captured = "notProvided",
            redacted => captured = "redacted");
        return captured;
    }

    public static string Accessors()
    {
        var value = Disclosure<string>.Visible("z");
        return $"{value.IsVisible}:{value.AsVisible().Value}:{value.TryPickMany(out _)}";
    }

    // TValue and TError must stay in the declared order through every emitted member.
    public static string ResultArgumentOrder()
    {
        var ok = Result<int, string>.Ok(7);
        var fail = Result<int, string>.Fail("bad");
        return ok.Match(o => $"ok:{o.Value}", f => $"fail:{f.Error}")
            + "|"
            + fail.Match(o => $"ok:{o.Value}", f => $"fail:{f.Error}");
    }
}
""";

    // Both collide with the emitted Match result parameter: one declares TResult itself, the
    // other inherits it from a containing type. Either shadows it as CS0693.
    internal const string ResultTypeParameterCollisions = """
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial class Box<TResult>
{
    [Variant]
    public static partial Box<TResult> Filled(TResult item);

    [Variant]
    public static partial Box<TResult> Empty();
}

public partial class Holder<TResult>
{
    [DiscriminatedUnion]
    public abstract partial class Leaf
    {
        [Variant]
        public static partial Leaf One(TResult item);

        [Variant]
        public static partial Leaf None();
    }
}

public static class Usage
{
    public static string BoxMatch()
        => Box<string>.Filled("a").Match(filled => $"filled:{filled.Item}", empty => "empty");

    public static string LeafMatch()
        => Holder<int>.Leaf.One(5).Match(one => $"one:{one.Item}", none => "none");
}
""";

    internal const string SameNameDifferentArity = """
using Zooper.Gorilla.Attributes;

namespace Sample
{
    [DiscriminatedUnion]
    public abstract partial class Foo
    {
        [Variant]
        public static partial Foo A();
    }

    [DiscriminatedUnion]
    public abstract partial class Foo<T>
    {
        [Variant]
        public static partial Foo<T> B(T value);
    }

    public partial class Outer
    {
        [DiscriminatedUnion]
        public abstract partial class Leaf
        {
            [Variant]
            public static partial Leaf C();
        }
    }

    public partial class Outer<T>
    {
        [DiscriminatedUnion]
        public abstract partial class Leaf
        {
            [Variant]
            public static partial Leaf D(T value);
        }
    }
}
""";

    internal const string NonAbstractGenericUnion = """
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public partial class Disclosure<T>
{
    [Variant]
    public static partial Disclosure<T> Visible(T value);
}
""";

    // Weird names the enclosing union with the wrong type arguments, so it is not a subtype of
    // this construction: it cannot appear in Outcome<T>'s Match. Rejected, beside it, is correct.
    //
    // Weird declares no variants on purpose. Deriving from a different construction also makes the
    // simple name 'Weird' resolve, inside Weird's own body, to the 'Weird' inherited from
    // Outcome<int> — so a [Variant] factory declared as 'partial Weird Odd()' returns
    // Outcome<int>.Weird and the generated code does not compile. That is the declaration being
    // broken, not the emitter, and it is precisely the surprise ZGOR005 exists to report.
    internal const string MismatchedSubUnionBase = """
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial class Outcome<T>
{
    [Variant]
    public static partial Outcome<T> Success(T value);

    [DiscriminatedUnion]
    public abstract partial class Weird : Outcome<int>
    {
    }

    [DiscriminatedUnion]
    public abstract partial class Rejected : Outcome<T>
    {
        [Variant]
        public static partial Rejected Validation(string field);
    }
}
""";

    // Sibling derives from the grandparent rather than from Rejected: a legitimate sub-union of
    // some other union, and none of ZGOR005's business.
    internal const string NestedUnionWithUnrelatedBase = """
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial class Outcome<T>
{
    [Variant]
    public static partial Outcome<T> Success(T value);

    [DiscriminatedUnion]
    public abstract partial class Rejected : Outcome<T>
    {
        [Variant]
        public static partial Rejected Validation(string field);

        [DiscriminatedUnion]
        public abstract partial class Sibling : Outcome<T>
        {
            [Variant]
            public static partial Sibling Other();
        }
    }
}
""";

    // A non-generic union written by its runtime type: the static type is the variant, which is
    // what an ASP.NET minimal API or JsonSerializer.Serialize(object) hands the serializer.
    internal const string RuntimeTypeDiscriminator = """
using Stj = System.Text.Json;
using Newtonsoft.Json;
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial class Plain
{
    [Variant]
    public static partial Plain Visible(string value);

    [Variant]
    public static partial Plain NotProvided();
}

public static class Usage
{
    public static string ThroughVariantStaticType()
    {
        var variant = (Plain.VisibleVariant)Plain.Visible("hello");
        return Stj.JsonSerializer.Serialize(variant);
    }

    public static string ThroughObject()
    {
        object value = Plain.Visible("hello");
        return Stj.JsonSerializer.Serialize(value);
    }

    public static string PayloadFreeThroughVariantStaticType()
    {
        var variant = (Plain.NotProvidedVariant)Plain.NotProvided();
        return Stj.JsonSerializer.Serialize(variant);
    }

    public static string NewtonsoftThroughObject()
    {
        object value = Plain.Visible("hello");
        return JsonConvert.SerializeObject(value);
    }
}
""";

    internal const string GenericUnionJson = """
using Stj = System.Text.Json;
using Newtonsoft.Json;
using Zooper.Gorilla.Attributes;

[DiscriminatedUnion]
public abstract partial record Disclosure<T>
{
    [Variant]
    public static partial Disclosure<T> Visible(T value);

    [Variant]
    public static partial Disclosure<T> NotProvided();
}

public sealed class Envelope
{
    public Disclosure<string> Payload { get; set; } = Disclosure<string>.NotProvided();
}

public static class Usage
{
    private static string Describe<T>(Disclosure<T> disclosure) =>
        disclosure.Match(visible => $"visible:{visible.Value}", notProvided => "notProvided");

    public static string StjStringRoundTrip()
    {
        var json = Stj.JsonSerializer.Serialize(Disclosure<string>.Visible("hello"));
        var back = Stj.JsonSerializer.Deserialize<Disclosure<string>>(json)!;
        return $"{json}|{Describe(back)}|{back == Disclosure<string>.Visible("hello")}";
    }

    public static string StjIntRoundTrip()
    {
        var json = Stj.JsonSerializer.Serialize(Disclosure<int>.Visible(42));
        var back = Stj.JsonSerializer.Deserialize<Disclosure<int>>(json)!;
        return $"{json}|{Describe(back)}|{back == Disclosure<int>.Visible(42)}";
    }

    public static string NewtonsoftStringRoundTrip()
    {
        var json = JsonConvert.SerializeObject(Disclosure<string>.Visible("hello"));
        var back = JsonConvert.DeserializeObject<Disclosure<string>>(json)!;
        return $"{json}|{Describe(back)}|{back == Disclosure<string>.Visible("hello")}";
    }

    public static string NewtonsoftIntRoundTrip()
    {
        var json = JsonConvert.SerializeObject(Disclosure<int>.Visible(42));
        var back = JsonConvert.DeserializeObject<Disclosure<int>>(json)!;
        return $"{json}|{Describe(back)}|{back == Disclosure<int>.Visible(42)}";
    }

    public static string StjThroughVariantStaticType()
    {
        var variant = (Disclosure<string>.VisibleVariant)Disclosure<string>.Visible("hello");
        return Stj.JsonSerializer.Serialize(variant);
    }

    public static string StjThroughObject()
    {
        object value = Disclosure<string>.Visible("hello");
        return Stj.JsonSerializer.Serialize(value);
    }

    // The shim's own type test, which is what a manual registration consults. A direct
    // generic-type-definition comparison answers false for every variant.
    public static string ShimCanConvert()
    {
        var shim = new DisclosureNewtonsoftJsonConverterShim();
        return $"{shim.CanConvert(typeof(Disclosure<string>))}"
            + $":{shim.CanConvert(typeof(Disclosure<string>.VisibleVariant))}"
            + $":{shim.CanConvert(typeof(string))}";
    }

    public static string ShimManualRegistration()
    {
        var settings = new JsonSerializerSettings();
        settings.Converters.Add(new DisclosureNewtonsoftJsonConverterShim());
        var variant = (Disclosure<string>.VisibleVariant)Disclosure<string>.Visible("hello");
        return JsonConvert.SerializeObject(variant, settings);
    }

    public static string DtoPropertyRoundTrip()
    {
        var json = Stj.JsonSerializer.Serialize(new Envelope { Payload = Disclosure<string>.Visible("in-dto") });
        var back = Stj.JsonSerializer.Deserialize<Envelope>(json)!;
        return $"{json}|{Describe(back.Payload)}";
    }
}
""";

    internal const string ConstrainedGenericUnions = """
using System;
using System.Collections.Generic;
using Zooper.Gorilla.Attributes;

public class Root
{
}

[DiscriminatedUnion]
public abstract partial class NotNullBox<T> where T : notnull
{
    [Variant]
    public static partial NotNullBox<T> Filled(T value);
}

[DiscriminatedUnion]
public abstract partial class ClassBox<T> where T : class
{
    [Variant]
    public static partial ClassBox<T> Filled(T value);
}

[DiscriminatedUnion]
public abstract partial class StructBox<T> where T : struct
{
    [Variant]
    public static partial StructBox<T> Filled(T value);
}

[DiscriminatedUnion]
public abstract partial class UnmanagedBox<T> where T : unmanaged
{
    [Variant]
    public static partial UnmanagedBox<T> Filled(T value);
}

[DiscriminatedUnion]
public abstract partial class ComparableBox<T> where T : IComparable<T>, new()
{
    [Variant]
    public static partial ComparableBox<T> Filled(T value);
}

// Declared with an unqualified name under a using directive: the captured clause must be
// fully qualified, because the generated file's using block is one line.
[DiscriminatedUnion]
public abstract partial class CacheBox<T> where T : IEqualityComparer<T>, new()
{
    [Variant]
    public static partial CacheBox<T> Filled(T value);
}

// Primary constraint, interface, and new() together: the rendering must keep that order.
[DiscriminatedUnion]
public abstract partial class CompoundBox<T> where T : Root, IComparable<T>, new()
{
    [Variant]
    public static partial CompoundBox<T> Filled(T value);
}
""";

    internal const string UnionsInGenericContainers = """
using Stj = System.Text.Json;
using Newtonsoft.Json;
using Zooper.Gorilla.Attributes;

public partial class OuterA<TOuter>
{
    [DiscriminatedUnion]
    public abstract partial class Leaf
    {
        [Variant]
        public static partial Leaf Tagged(string tag);
    }
}

public partial class Outer<TOuter> where TOuter : notnull
{
    [DiscriminatedUnion]
    public abstract partial record Leaf<T> where T : class
    {
        [Variant]
        public static partial Leaf<T> Pair(TOuter key, T value);

        [Variant]
        public static partial Leaf<T> Empty();
    }
}

public static class Usage
{
    public static string NonGenericLeafRoundTrip()
    {
        var json = Stj.JsonSerializer.Serialize(OuterA<int>.Leaf.Tagged("t"));
        var back = Stj.JsonSerializer.Deserialize<OuterA<int>.Leaf>(json)!;
        return $"{json}|{back.Match(tagged => tagged.Tag)}";
    }

    public static string GenericLeafStjRoundTrip()
    {
        var json = Stj.JsonSerializer.Serialize(Outer<int>.Leaf<string>.Pair(7, "v"));
        var back = Stj.JsonSerializer.Deserialize<Outer<int>.Leaf<string>>(json)!;
        return $"{json}|{back.Match(pair => $"{pair.Key}:{pair.Value}", empty => "empty")}";
    }

    public static string GenericLeafNewtonsoftRoundTrip()
    {
        var json = JsonConvert.SerializeObject(Outer<int>.Leaf<string>.Pair(7, "v"));
        var back = JsonConvert.DeserializeObject<Outer<int>.Leaf<string>>(json)!;
        return $"{json}|{back.Match(pair => $"{pair.Key}:{pair.Value}", empty => "empty")}";
    }
}
""";
}
