using Xunit.Abstractions;
using Xunit.Sdk;

namespace Rotula.Tests;

/// <summary>
/// Thrown by <see cref="Skip"/> to end a test early and have it reported as SKIPPED. xunit 2.9 has no run-time skip, and a bare
/// <c>return;</c> in a test body reports a green pass for a check that never ran, which hides a PC (or a CI image) that silently lost
/// coverage. A skipped test shows up in the summary, so the gap stays visible.
/// </summary>
public sealed class SkipTestException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="reason">Plain-language reason shown next to the skipped test.</param>
    public SkipTestException(string reason) : base(reason) { }
}

/// <summary>Run-time skip helpers; only valid inside a test marked <see cref="SkippableFactAttribute"/> or <see cref="LocalDbFactAttribute"/>.</summary>
public static class Skip
{
    /// <summary>Skips the current test when <paramref name="condition"/> is true.</summary>
    public static void If([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(true)] bool condition, string reason)
    {
        if (condition) throw new SkipTestException(reason);
    }

    /// <summary>Skips the current test unless <paramref name="condition"/> is true.</summary>
    public static void IfNot([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string reason) => If(!condition, reason);
}

/// <summary>
/// A [Fact] that may call <see cref="Skip"/> while it runs. A discoverer wraps the normal test case so the failure produced by
/// <see cref="SkipTestException"/> is turned into a skip before it reaches the runner (the same approach the SkippableFact package uses,
/// written out here so the test project needs no extra dependency).
/// </summary>
[XunitTestCaseDiscoverer("Rotula.Tests.SkippableFactDiscoverer", "Rotula.Tests")]
public class SkippableFactAttribute : FactAttribute
{
}

/// <summary>Discoverer of <see cref="SkippableFactAttribute"/>.</summary>
public sealed class SkippableFactDiscoverer : IXunitTestCaseDiscoverer
{
    private readonly IMessageSink _diagnostics;

    /// <summary>Creates the discoverer.</summary>
    public SkippableFactDiscoverer(IMessageSink diagnosticMessageSink) => _diagnostics = diagnosticMessageSink;

    /// <inheritdoc />
    public IEnumerable<IXunitTestCase> Discover(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo factAttribute)
    {
        // A static Skip set on the attribute (see LocalDbFactAttribute) must still win, so it goes to the plain test case.
        if (factAttribute.GetNamedArgument<string>(nameof(FactAttribute.Skip)) is not null)
            yield return new XunitTestCase(_diagnostics, discoveryOptions.MethodDisplayOrDefault(), discoveryOptions.MethodDisplayOptionsOrDefault(), testMethod);
        else
            yield return new SkippableTestCase(_diagnostics, discoveryOptions.MethodDisplayOrDefault(), discoveryOptions.MethodDisplayOptionsOrDefault(), testMethod);
    }
}

/// <summary>Test case that converts <see cref="SkipTestException"/> failures into skips.</summary>
public sealed class SkippableTestCase : XunitTestCase
{
    /// <summary>For the serializer only.</summary>
    [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes", true)]
    public SkippableTestCase() { }

    /// <summary>Creates the test case.</summary>
    public SkippableTestCase(IMessageSink diagnosticMessageSink, TestMethodDisplay defaultMethodDisplay, TestMethodDisplayOptions defaultMethodDisplayOptions, ITestMethod testMethod)
        : base(diagnosticMessageSink, defaultMethodDisplay, defaultMethodDisplayOptions, testMethod) { }

    /// <inheritdoc />
    public override async Task<RunSummary> RunAsync(IMessageSink diagnosticMessageSink, IMessageBus messageBus, object[] constructorArguments,
        ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource)
    {
        var bus = new SkipAwareMessageBus(messageBus);
        var result = await base.RunAsync(diagnosticMessageSink, bus, constructorArguments, aggregator, cancellationTokenSource);
        // The runner counted the converted failure as a failure; move it to the skipped column so the totals match what was reported.
        return bus.Skipped > 0
            ? new RunSummary { Total = result.Total, Failed = result.Failed - bus.Skipped, Skipped = result.Skipped + bus.Skipped, Time = result.Time }
            : result;
    }

    private sealed class SkipAwareMessageBus : IMessageBus
    {
        private readonly IMessageBus _inner;

        public SkipAwareMessageBus(IMessageBus inner) => _inner = inner;

        public int Skipped { get; private set; }

        public bool QueueMessage(IMessageSinkMessage message)
        {
            if (message is ITestFailed failed && failed.ExceptionTypes.FirstOrDefault() == typeof(SkipTestException).FullName)
            {
                Skipped++;
                return _inner.QueueMessage(new TestSkipped(failed.Test, failed.Messages.FirstOrDefault() ?? "Skipped"));
            }
            return _inner.QueueMessage(message);
        }

        public void Dispose() => _inner.Dispose();
    }
}
