using Rotula.Core.Model;
using Rotula.Core.Variables;

namespace Rotula.Tests;

/// <summary>A clock the tests control, with a time zone that has daylight saving, so no test depends on the machine's own zone.</summary>
internal sealed class FakeTime : TimeProvider
{
    private DateTimeOffset _utc;
    private readonly TimeZoneInfo _zone;

    public FakeTime(DateTimeOffset utc, TimeZoneInfo? zone = null)
    {
        _utc = utc;
        _zone = zone ?? EastZone;
    }

    public override DateTimeOffset GetUtcNow() => _utc;

    public override TimeZoneInfo LocalTimeZone => _zone;

    public void Set(DateTimeOffset utc) => _utc = utc;

    /// <summary>
    /// UTC-5 with daylight saving (+1 hour) from the second Sunday of March at 02:00 to the first Sunday of November at 02:00, like New York.
    /// In 2026 the clocks jump on 8 March and fall back on 1 November.
    /// </summary>
    public static readonly TimeZoneInfo EastZone = TimeZoneInfo.CreateCustomTimeZone("Test/East", TimeSpan.FromHours(-5), "Test East", "Test East Standard", "Test East Daylight",
        new[]
        {
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday)),
        });

    /// <summary>A clock reading the given local wall time in <see cref="EastZone"/>.</summary>
    public static FakeTime AtLocal(int y, int mo, int d, int h = 12, int mi = 0, int s = 0)
    {
        var wall = new DateTime(y, mo, d, h, mi, s, DateTimeKind.Unspecified);
        var offset = EastZone.GetUtcOffset(wall);
        return new FakeTime(new DateTimeOffset(wall, offset).ToUniversalTime());
    }
}

/// <summary>Builders shared by the variable tests.</summary>
internal static class Vars
{
    public static LabelVariable Date(string name, DateVariableOptions options, string sample = "") =>
        new(name, sample) { Kind = VariableKind.Date, Date = options };

    public static LabelVariable Date(string name, string format, params DateOffset[] offsets)
    {
        var o = new DateVariableOptions { Format = format };
        o.Offsets.AddRange(offsets);
        return Date(name, o);
    }

    public static LabelVariable Counter(string name, Action<CounterVariableOptions>? configure = null)
    {
        var c = new CounterVariableOptions();
        configure?.Invoke(c);
        return new LabelVariable(name) { Kind = VariableKind.Counter, Counter = c };
    }

    public static LabelVariable Computed(string name, string expression) =>
        new(name) { Kind = VariableKind.Computed, Computed = new ComputedVariableOptions { Expression = expression } };

    public static LabelVariable Prompt(string name, Action<PromptVariableOptions>? configure = null, string sample = "")
    {
        var p = new PromptVariableOptions();
        configure?.Invoke(p);
        return new LabelVariable(name, sample) { Kind = VariableKind.Prompt, Prompt = p };
    }

    /// <summary>Evaluates one date variable with a fake clock and returns the text (null when it failed).</summary>
    public static string? EvalDate(FakeTime time, DateVariableOptions o, out List<Rotula.Core.Validation.ValidationIssue> issues)
    {
        issues = new List<Rotula.Core.Validation.ValidationIssue>();
        return DateEvaluator.Evaluate("d", o, time.GetUtcNow(), time.LocalTimeZone, issues);
    }

    public static string? EvalDate(FakeTime time, string format, params DateOffset[] offsets)
    {
        var o = new DateVariableOptions { Format = format };
        o.Offsets.AddRange(offsets);
        return EvalDate(time, o, out _);
    }
}
