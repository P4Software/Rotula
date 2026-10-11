using Rotula.App.ViewModels;

namespace Rotula.Tests;

/// <summary>Regression: the printer transport radio buttons wrote each other's value back endlessly (stack overflow).</summary>
[Collection("App")]
public class PrinterTransportRadioTests
{
    [Fact]
    public void MirroredRadioWrites_DoNotFlipTheChoiceBack_AndRaiseABoundedNumberOfEvents()
    {
        var fake = new FakeServices();
        var doc = new Rotula.Core.Model.LabelDocument();
        var vm = new Rotula.App.ViewModels.PrinterSettingsViewModel(fake.Printer, new Rotula.App.Services.PrintSettings(), () => { }, () => doc,
            (_, _) => Task.FromResult(new Rotula.App.Services.PrinterDetection(null, null, null)), _ => { });
        int events = 0;
        // Count only; never throw from the handler. The view-model also listens to the static language-changed event, so a throwing
        // handler left subscribed would break unrelated tests that switch the language later.
        System.ComponentModel.PropertyChangedEventHandler counter = (_, _) => events++;
        vm.PropertyChanged += counter;

        // What the radio group does when "Network" is checked: Network = true, then the Windows radio, now unchecked, writes false.
        vm.UseNetwork = true;
        vm.UseWindowsPrinter = false;
        Assert.True(vm.UseNetwork);
        // And when "Windows printer" is checked: Windows = true, then the Network radio writes its unchecked value false.
        vm.UseWindowsPrinter = true;
        vm.UseNetwork = false;
        Assert.False(vm.UseNetwork);
        Assert.True(vm.UseWindowsPrinter);
        vm.PropertyChanged -= counter;
        Assert.InRange(events, 1, 50);
    }
}
