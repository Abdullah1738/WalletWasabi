using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Fluent.Controls.AdorningContentControlTests;

public class AdorningContentControlTests
{
    [AvaloniaFact]
    public void AdorningContentControl_NoAdornment_DoesNotThrow()
    {
        var window = new AdorningContentControl_NoAdornment();

        // Attaching to the visual tree calls OnPointerOverChanged, which must
        // not throw when no Adornment is set.
        window.Show();

        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(window.TargetControl);
        Assert.True(window.TargetControl.IsVisible);
    }
}
