namespace Rotula.Data.SamplePack.Dep;

/// <summary>A dependency that exists only inside the sample pack folder.</summary>
public static class Greeter
{
    /// <summary>Text proving the dependency was resolved from the pack folder.</summary>
    public static string Text => "pack-private-dependency";
}
