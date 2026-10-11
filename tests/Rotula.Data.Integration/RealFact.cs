using Xunit.Abstractions;
using Xunit.Sdk;

namespace Rotula.Data.Integration;

/// <summary>Marks a test class as belonging to one engine (see <see cref="ServerCatalog"/>); <see cref="RealFactAttribute"/> uses it to skip.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class EngineAttribute : Attribute
{
    /// <summary>Creates the marker.</summary>
    public EngineAttribute(string engine) => Engine = engine;

    /// <summary>"postgresql" or "mariadb".</summary>
    public string Engine { get; }
}

/// <summary>
/// A fact that is SKIPPED, with a plain-language reason, when the server of the test class's <see cref="EngineAttribute"/> is not
/// running. xunit 2 has no run-time skip, so a discoverer decides at discovery time; the same inherited test method can therefore be
/// skipped for one engine and run for the other.
/// </summary>
[XunitTestCaseDiscoverer("Rotula.Data.Integration.RealFactDiscoverer", "Rotula.Data.Integration")]
public sealed class RealFactAttribute : FactAttribute
{
}

/// <summary>Discoverer for <see cref="RealFactAttribute"/>.</summary>
public sealed class RealFactDiscoverer : FactDiscoverer
{
    /// <summary>Creates the discoverer.</summary>
    public RealFactDiscoverer(IMessageSink diagnosticMessageSink) : base(diagnosticMessageSink) { }

    /// <inheritdoc />
    protected override IXunitTestCase CreateTestCase(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo factAttribute)
        => new RealTestCase(DiagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(), discoveryOptions.MethodDisplayOptionsOrDefault(), testMethod);
}

/// <summary>A test case whose skip reason comes from the availability of the class's engine.</summary>
public sealed class RealTestCase : XunitTestCase
{
    /// <summary>For the serializer only.</summary>
    [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes", true)]
    public RealTestCase() { }

    /// <summary>Creates the test case.</summary>
    public RealTestCase(IMessageSink diagnosticMessageSink, TestMethodDisplay defaultMethodDisplay, TestMethodDisplayOptions defaultMethodDisplayOptions, ITestMethod testMethod)
        : base(diagnosticMessageSink, defaultMethodDisplay, defaultMethodDisplayOptions, testMethod) { }

    /// <inheritdoc />
    protected override void Initialize()
    {
        base.Initialize();
        // A test that documents a problem of the product (see KnownBugs) is tagged so the weekly workflow can run it separately.
        foreach (var attr in TestMethod.TestClass.Class.GetCustomAttributes(typeof(EngineAttribute).AssemblyQualifiedName))
        {
            string engine = (string)attr.GetConstructorArguments().First();
            if (KnownBugs.Find(engine, TestMethod.Method.Name) is { } id)
            {
                Traits["Category"] = new List<string> { "KnownBug" };
                Traits["BugId"] = new List<string> { id };
            }
        }
    }

    /// <inheritdoc />
    protected override string GetSkipReason(IAttributeInfo factAttribute)
    {
        // Looked up from the class every time (also after de-serialization), so no extra state has to be serialized.
        foreach (var attr in TestMethod.TestClass.Class.GetCustomAttributes(typeof(EngineAttribute).AssemblyQualifiedName))
        {
            string engine = (string)attr.GetConstructorArguments().First();
            return ServerCatalog.SkipReason(engine)!;
        }
        return "The test class has no [Engine] attribute.";
    }
}

/// <summary>Collection that runs every real-server test one after another: they share two servers, and the memory test needs a quiet process.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealServersCollection
{
    /// <summary>Collection name.</summary>
    public const string Name = "RealServers";
}
