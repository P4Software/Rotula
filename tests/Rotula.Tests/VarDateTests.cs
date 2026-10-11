using System.Globalization;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Variables;

namespace Rotula.Tests;

/// <summary>Date arithmetic, rounding, formats and cultures of date variables (all on a fake clock with a daylight-saving zone).</summary>
public class VarDateTests
{
    private static DateOffset Days(int n) => new(n, DateUnit.Days);

    [Fact]
    public void ExpiryIsTodayPlus30Days_PrintedAsYYMMDD()
    {
        var time = FakeTime.AtLocal(2026, 3, 7, 14, 5, 9);
        Assert.Equal("260406", Vars.EvalDate(time, "gs1", Days(30)));
        Assert.Equal("260307", Vars.EvalDate(time, "gs1"));
    }

    [Theory]
    [InlineData(2026, 1, 31, 1, "2026-02-28")]   // 31 January + 1 month clamps to the last day of February
    [InlineData(2028, 1, 31, 1, "2028-02-29")]   // leap year
    [InlineData(2026, 3, 31, -1, "2026-02-28")]
    [InlineData(2026, 12, 31, 2, "2027-02-28")]
    [InlineData(2026, 11, 30, 3, "2027-02-28")]
    public void Months_ClampToTheLastDayOfTheTargetMonth(int y, int m, int d, int months, string expected)
    {
        var time = FakeTime.AtLocal(y, m, d, 10);
        Assert.Equal(expected, Vars.EvalDate(time, "iso", new DateOffset(months, DateUnit.Months)));
    }

    [Theory]
    [InlineData(2028, 2, 29, 1, "2029-02-28")]
    [InlineData(2028, 2, 29, 4, "2032-02-29")]
    [InlineData(2026, 3, 1, -1, "2025-03-01")]
    public void Years_AndLeapDay(int y, int m, int d, int years, string expected)
    {
        var time = FakeTime.AtLocal(y, m, d, 10);
        Assert.Equal(expected, Vars.EvalDate(time, "iso", new DateOffset(years, DateUnit.Years)));
    }

    [Fact]
    public void Weeks_AreSevenDays_AndOffsetsApplyInOrder()
    {
        var time = FakeTime.AtLocal(2026, 1, 31, 10);
        Assert.Equal("2026-02-14", Vars.EvalDate(time, "iso", new DateOffset(2, DateUnit.Weeks)));
        // +1 month then +30 days is not the same as +30 days then +1 month.
        Assert.Equal("2026-03-30", Vars.EvalDate(time, "iso", new DateOffset(1, DateUnit.Months), Days(30)));
        Assert.Equal("2026-04-02", Vars.EvalDate(time, "iso", Days(30), new DateOffset(1, DateUnit.Months)));
    }

    [Fact]
    public void TimeUnitsAreElapsedTime_DaysKeepTheWallClock_AcrossTheSpringForwardJump()
    {
        // Saturday 7 March 2026 14:05 local; the clocks jump forward on Sunday 8 March at 02:00.
        var time = FakeTime.AtLocal(2026, 3, 7, 14, 5);
        Assert.Equal("14:05", Vars.EvalDate(time, "HH:mm", Days(1)));                              // a calendar day: same wall time
        Assert.Equal("15:05", Vars.EvalDate(time, "HH:mm", new DateOffset(24, DateUnit.Hours)));   // 24 real hours: the clock shows one hour more
        Assert.Equal("15:05", Vars.EvalDate(time, "HH:mm", new DateOffset(1440, DateUnit.Minutes)));
        // 00:30 on the day of the jump plus two real hours: the first 90 minutes reach 02:00, which becomes 03:00, so the clock reads 03:30.
        var early = FakeTime.AtLocal(2026, 3, 8, 0, 30);
        Assert.Equal("03:30", Vars.EvalDate(early, "HH:mm", new DateOffset(2, DateUnit.Hours)));
    }

    [Fact]
    public void ATimeThatDoesNotExist_MovesOneHourLater_AndARepeatedOneUsesTheFirst()
    {
        var time = FakeTime.AtLocal(2026, 3, 7, 2, 30);
        Assert.Equal("03:30", Vars.EvalDate(time, "HH:mm", Days(1)));  // 02:30 on 8 March was skipped
        // 1 November 01:30 happens twice; the arithmetic must not throw and keeps the wall time.
        var fall = FakeTime.AtLocal(2026, 10, 31, 1, 30);
        Assert.Equal("01:30", Vars.EvalDate(fall, "HH:mm", Days(1)));
        // Elapsed time across the fall-back hour: 00:30 + 3 real hours = 02:30 standard time.
        var before = FakeTime.AtLocal(2026, 11, 1, 0, 30);
        Assert.Equal("02:30", Vars.EvalDate(before, "HH:mm", new DateOffset(3, DateUnit.Hours)));
    }

    [Theory]
    [InlineData(2026, 3, 6, 1, "2026-03-09")]    // Friday + 1 business day = Monday
    [InlineData(2026, 3, 4, 5, "2026-03-11")]    // Wednesday + 5 = next Wednesday
    [InlineData(2026, 3, 9, -1, "2026-03-06")]   // Monday - 1 = Friday
    [InlineData(2026, 3, 7, 1, "2026-03-09")]    // Saturday + 1 = Monday
    [InlineData(2026, 3, 7, 0, "2026-03-07")]    // zero business days leaves the date alone
    [InlineData(2026, 3, 2, 10, "2026-03-16")]
    public void BusinessDays_SkipWeekends(int y, int m, int d, int n, string expected)
    {
        var time = FakeTime.AtLocal(y, m, d, 10);
        Assert.Equal(expected, Vars.EvalDate(time, "iso", new DateOffset(n, DateUnit.BusinessDays)));
    }

    [Fact]
    public void BusinessDays_SkipHolidays()
    {
        var o = new DateVariableOptions { Format = "iso" };
        o.Offsets.Add(new DateOffset(1, DateUnit.BusinessDays));
        o.Holidays.Add(new DateOnly(2026, 3, 9));
        o.Holidays.Add(new DateOnly(2026, 3, 10));
        Assert.Equal("2026-03-11", Vars.EvalDate(FakeTime.AtLocal(2026, 3, 6, 10), o, out _));
    }

    [Theory]
    [InlineData(DateRounding.StartOfDay, "2026-02-10 00:00:00")]
    [InlineData(DateRounding.EndOfDay, "2026-02-10 23:59:59")]
    [InlineData(DateRounding.StartOfMonth, "2026-02-01 00:00:00")]
    [InlineData(DateRounding.EndOfMonth, "2026-02-28 23:59:59")]
    [InlineData(DateRounding.StartOfYear, "2026-01-01 00:00:00")]
    [InlineData(DateRounding.EndOfYear, "2026-12-31 23:59:59")]
    [InlineData(DateRounding.None, "2026-02-10 13:14:15")]
    public void Rounding(DateRounding rounding, string expected)
    {
        var o = new DateVariableOptions { Format = "yyyy-MM-dd HH:mm:ss", Rounding = rounding };
        Assert.Equal(expected, Vars.EvalDate(FakeTime.AtLocal(2026, 2, 10, 13, 14, 15), o, out _));
    }

    [Fact]
    public void ExpiryAtTheEndOfNextMonth()
    {
        var o = new DateVariableOptions { Format = "iso", Rounding = DateRounding.EndOfMonth };
        o.Offsets.Add(new DateOffset(1, DateUnit.Months));
        Assert.Equal("2026-03-31", Vars.EvalDate(FakeTime.AtLocal(2026, 2, 10), o, out _));
    }

    [Fact]
    public void PresetsMatchTheirDocumentedExamples()
    {
        var time = FakeTime.AtLocal(2026, 3, 7, 14, 5, 9);
        foreach (var preset in DatePresets.All)
            Assert.Equal(preset.Example, Vars.EvalDate(time, preset.Id));
    }

    [Fact]
    public void PresetIdsAreNotCaseSensitive_AndCustomPatternsWork()
    {
        var time = FakeTime.AtLocal(2026, 3, 7, 14, 5, 9);
        Assert.Equal("260307", Vars.EvalDate(time, "GS1"));
        Assert.Equal("07/03/2026 14:05", Vars.EvalDate(time, "dd/MM/yyyy HH:mm"));
        Assert.Equal("7", Vars.EvalDate(time, "d"));   // one letter is the day, not .NET's "short date" standard format
    }

    [Theory]
    [InlineData(2024, 12, 30, "2025-W01")]   // 30 December 2024 belongs to ISO week 1 of 2025
    [InlineData(2027, 1, 3, "2026-W53")]     // 3 January 2027 still belongs to week 53 of 2026
    [InlineData(2026, 1, 1, "2026-W01")]
    public void IsoWeek_UsesTheWeekBasedYear(int y, int m, int d, string expected) =>
        Assert.Equal(expected, Vars.EvalDate(FakeTime.AtLocal(y, m, d), "isoWeek"));

    [Fact]
    public void JulianDay_IsThreeDigits_AndCountsLeapDays()
    {
        Assert.Equal("001", Vars.EvalDate(FakeTime.AtLocal(2026, 1, 1), "julian"));
        Assert.Equal("365", Vars.EvalDate(FakeTime.AtLocal(2026, 12, 31), "julian"));
        Assert.Equal("366", Vars.EvalDate(FakeTime.AtLocal(2028, 12, 31), "julian"));
        Assert.Equal("28060", Vars.EvalDate(FakeTime.AtLocal(2028, 2, 29), "julianYear"));
    }

    [Fact]
    public void CultureNames_FollowTheCulture_NotTheMachine()
    {
        var culture = DateFormats.ResolveCulture("es-ES");
        if (culture is null || culture.Name.Length == 0) return; // globalization-invariant runtime: nothing to compare
        var o = new DateVariableOptions { Format = "MMM", Culture = "es-ES" };
        string expected = culture.DateTimeFormat.GetAbbreviatedMonthName(3);
        Assert.Equal(expected, Vars.EvalDate(FakeTime.AtLocal(2026, 3, 7), o, out _));
        o.Format = "MMMM";
        Assert.Equal(culture.DateTimeFormat.GetMonthName(3), Vars.EvalDate(FakeTime.AtLocal(2026, 3, 7), o, out _));
    }

    [Fact]
    public void ABuddhistCalendarCulture_StillPrintsGregorianYears()
    {
        var o = new DateVariableOptions { Format = "yyyy", Culture = "th-TH" };
        string? text = Vars.EvalDate(FakeTime.AtLocal(2026, 3, 7), o, out _);
        Assert.Equal("2026", text);
    }

    [Fact]
    public void AnUnknownCulture_WarnsAndFallsBackToInvariant()
    {
        var o = new DateVariableOptions { Format = "MMM", Culture = "xx-NOPE-zz" };
        Assert.Equal("Mar", Vars.EvalDate(FakeTime.AtLocal(2026, 3, 7), o, out var issues));
        var issue = Assert.Single(issues);
        Assert.Equal((ValidationSeverity.Warning, VariableMessageKeys.DateCultureUnknown), (issue.Severity, issue.MessageKey));
    }

    [Fact]
    public void Utc_UsesUniversalTime()
    {
        // 23:30 local on 7 March (UTC-5) is 04:30 UTC on 8 March.
        var time = FakeTime.AtLocal(2026, 3, 7, 23, 30);
        var local = new DateVariableOptions { Format = "iso" };
        var utc = new DateVariableOptions { Format = "iso", Utc = true };
        Assert.Equal("2026-03-07", Vars.EvalDate(time, local, out _));
        Assert.Equal("2026-03-08", Vars.EvalDate(time, utc, out _));
    }

    [Fact]
    public void FixedDate_IgnoresTheClock_ButStillUsesOffsets()
    {
        var o = new DateVariableOptions { Source = DateSource.Fixed, FixedValue = new DateTime(2030, 1, 31, 8, 0, 0), Format = "iso" };
        o.Offsets.Add(new DateOffset(1, DateUnit.Months));
        Assert.Equal("2030-02-28", Vars.EvalDate(FakeTime.AtLocal(2026, 3, 7), o, out _));
        o.FixedValue = null;
        Assert.Null(Vars.EvalDate(FakeTime.AtLocal(2026, 3, 7), o, out var issues));
        Assert.Equal(VariableMessageKeys.DateFixedMissing, Assert.Single(issues).MessageKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("yyyy-MM-dd'unclosed")]
    public void ABadFormat_IsAnError_NotAnException(string format)
    {
        var o = new DateVariableOptions { Format = format };
        Assert.Null(Vars.EvalDate(FakeTime.AtLocal(2026, 3, 7), o, out var issues));
        Assert.Equal(VariableMessageKeys.DateFormatInvalid, Assert.Single(issues).MessageKey);
    }

    [Fact]
    public void HugeOffsets_AreRefused_AndAnOverflowingDateIsAnError()
    {
        var o = new DateVariableOptions { Format = "iso" };
        o.Offsets.Add(new DateOffset(int.MaxValue, DateUnit.Years));
        Assert.Null(Vars.EvalDate(FakeTime.AtLocal(2026, 3, 7), o, out var issues));
        Assert.Equal(VariableMessageKeys.DateOffsetRange, Assert.Single(issues).MessageKey);

        o.Offsets.Clear();
        for (int i = 0; i < 8; i++) o.Offsets.Add(new DateOffset(VariableLimits.MaxOffsetAmount, DateUnit.Years)); // 800,000 years: beyond year 9999
        Assert.Null(Vars.EvalDate(FakeTime.AtLocal(2026, 3, 7), o, out issues));
        Assert.Equal(VariableMessageKeys.DateOutOfRange, Assert.Single(issues).MessageKey);

        o.Offsets.Clear();
        for (int i = 0; i <= VariableLimits.MaxOffsets; i++) o.Offsets.Add(Days(1));
        Assert.Null(Vars.EvalDate(FakeTime.AtLocal(2026, 3, 7), o, out issues));
    }

    [Fact]
    public void ADayOfBusinessDayArithmetic_WithEveryDayAHoliday_Terminates()
    {
        var o = new DateVariableOptions { Format = "iso" };
        o.Offsets.Add(new DateOffset(5, DateUnit.BusinessDays));
        var start = new DateOnly(2026, 3, 1);
        for (int i = 0; i < VariableLimits.MaxHolidays; i++) o.Holidays.Add(start.AddDays(i));
        // 400 consecutive holidays cover well over a year of weekdays; the loop must still end (with a date or an error), not spin.
        var text = Vars.EvalDate(FakeTime.AtLocal(2026, 3, 7), o, out _);
        Assert.NotNull(text);
    }

    [Fact]
    public void TheEngineProducesTheSameTextForTheSameInstant()
    {
        var doc = new LabelDocument();
        doc.Variables.Add(Vars.Date("exp", "gs1", Days(30)));
        var a = VariableEngine.Resolve(doc, new VariableContext { Time = FakeTime.AtLocal(2026, 3, 7) }).Values["exp"];
        var b = VariableEngine.Resolve(doc, new VariableContext { Time = FakeTime.AtLocal(2026, 3, 7) }).Values["exp"];
        Assert.Equal(a, b);
        Assert.Equal("260406", a);
    }
}
