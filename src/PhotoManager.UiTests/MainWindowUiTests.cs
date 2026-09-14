using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Xunit;

namespace PhotoManager.UiTests;

public sealed class MainWindowUiTests
{
    [Fact]
    [Trait("Category", "UI")]
    public void Startup_shows_safe_initial_state_and_blocks_destructive_actions()
    {
        using var app = LaunchApplication();
        try
        {
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation)
                ?? throw new InvalidOperationException("The application did not expose a main window.");

            Assert.Equal("Photo Manager", window.Title);
            Assert.Equal("Idle", Find(window, "WorkflowStateText").Name);
            Assert.False(Find(window, "ConfigurationPage").IsOffscreen);
            Assert.True(Find(window, "ResetSessionButton").IsEnabled);
            Assert.Null(window.FindFirstDescendant(condition =>
                condition.ByAutomationId("DuplicateWorkPage")));
            Assert.Null(window.FindFirstDescendant(condition =>
                condition.ByAutomationId("DateWorkPage")));
            Assert.True(Find(window, "StartDateWorkButton").IsEnabled);
            Assert.False(Find(window, "ContinueDateWorkButton").IsEnabled);
            Assert.True(Find(window, "StartRotateWorkButton").IsEnabled);
            Assert.False(Find(window, "ContinueRotateWorkButton").IsEnabled);
            Assert.True(Find(window, "ConfigureDuplicatesButton").IsEnabled);
        }
        finally
        {
            app.Kill();
        }
    }

    [Fact]
    [Trait("Category", "UI")]
    public void Invalid_scan_root_is_rejected_without_running_a_scan()
    {
        using var app = LaunchApplication();
        try
        {
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation)
                ?? throw new InvalidOperationException("The application did not expose a main window.");

            var scanRoot = Find(window, "ScanRootTextBox").AsTextBox();
            scanRoot.Text = @"C:\";
            Find(window, "ConfigureDuplicatesButton").AsButton().Invoke();

            var status = Find(window, "StatusMessageText");
            Assert.Contains("drive root", status.Name, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Idle", Find(window, "WorkflowStateText").Name);
        }
        finally
        {
            app.Kill();
        }
    }

    [Fact]
    [Trait("Category", "UI")]
    public void Configuration_can_open_date_work_without_duplicate_scan()
    {
        using var app = LaunchApplication();
        try
        {
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation)
                ?? throw new InvalidOperationException("The application did not expose a main window.");

            Find(window, "StartDateWorkButton").AsButton().Invoke();
            Assert.True(SpinWait.SpinUntil(
                () => window.FindFirstDescendant(condition => condition.ByAutomationId("DateWorkPage")) is not null,
                TimeSpan.FromSeconds(3)));
            Assert.NotNull(window.FindFirstDescendant(condition => condition.ByAutomationId("DateWorkPage")));
            Assert.Null(window.FindFirstDescendant(condition => condition.ByAutomationId("DuplicateWorkPage")));
            Assert.False(Find(window, "ApplyDatesButton").IsEnabled);
            Assert.False(Find(window, "CreateDateSnapshotButton").IsEnabled);
            Assert.True(Find(window, "ScanDatesButton").IsEnabled);
            Assert.False(Find(window, "OpenDateUndoButton").IsEnabled);
            Assert.Null(window.FindFirstDescendant(condition =>
                condition.ByAutomationId("DateUndoPage")));
        }
        finally
        {
            app.Kill();
        }
    }

    [Fact]
    [Trait("Category", "UI")]
    public void Invalid_scan_root_blocks_direct_date_work()
    {
        using var app = LaunchApplication();
        try
        {
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation)
                ?? throw new InvalidOperationException("The application did not expose a main window.");

            Find(window, "ScanRootTextBox").AsTextBox().Text = @"C:\";
            Find(window, "StartDateWorkButton").AsButton().Invoke();

            Assert.True(SpinWait.SpinUntil(
                () => Find(window, "StatusMessageText").Name.Contains("drive root", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(3)));
            Assert.Equal("Idle", Find(window, "WorkflowStateText").Name);
            Assert.False(Find(window, "ConfigurationPage").IsOffscreen);
        }
        finally
        {
            app.Kill();
        }
    }

    [Fact]
    [Trait("Category", "UI")]
    public void Reset_from_date_work_returns_to_configuration()
    {
        using var app = LaunchApplication();
        try
        {
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation)
                ?? throw new InvalidOperationException("The application did not expose a main window.");

            Find(window, "StartDateWorkButton").AsButton().Invoke();
            Assert.True(SpinWait.SpinUntil(
                () => window.FindFirstDescendant(condition => condition.ByAutomationId("DateWorkPage")) is not null,
                TimeSpan.FromSeconds(3)));
            Find(window, "ResetSessionButton").AsButton().Invoke();

            Assert.True(SpinWait.SpinUntil(
                () => Find(window, "ConfigurationPage") is not null && !Find(window, "ConfigurationPage").IsOffscreen,
                TimeSpan.FromSeconds(3)));
            Assert.Equal("Idle", Find(window, "WorkflowStateText").Name);
            Assert.Null(window.FindFirstDescendant(condition => condition.ByAutomationId("DateWorkPage")));
        }
        finally
        {
            app.Kill();
        }
    }

    private static Application LaunchApplication()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "PhotoManager.exe");
        Assert.True(File.Exists(executable), $"Expected application next to UI tests: {executable}");
        return Application.Launch(executable);
    }

    private static AutomationElement Find(AutomationElement window, string automationId)
    {
        var element = window.FindFirstDescendant(
            condition => condition.ByAutomationId(automationId));
        Assert.NotNull(element);
        return element!;
    }
}
