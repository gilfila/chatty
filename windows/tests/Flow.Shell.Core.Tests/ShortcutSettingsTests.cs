using Flow.Shell.Core;

namespace Flow.Shell.Core.Tests;

/// <summary>
/// What Flow remembers between launches. Every failure path here has to leave a working app —
/// refusing to start over an unreadable preference file would be a worse bug than the preference.
/// </summary>
public sealed class ShortcutSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "flow-settings-tests", Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void AFreshInstallUsesTheDefaultShortcutAndHasNotDictatedYet()
    {
        var settings = ShortcutSettings.Load(Path_);

        Assert.Equal(ShortcutCatalog.Default, settings.TriggerKey);
        Assert.False(settings.HasCompletedFirstDictation);
    }

    [Fact]
    public void AChangedShortcutSurvivesARestart()
    {
        var first = ShortcutSettings.Load(Path_);
        Assert.True(first.SetTrigger(ShortcutCatalog.VK_RSHIFT));

        var second = ShortcutSettings.Load(Path_);
        Assert.Equal(ShortcutCatalog.VK_RSHIFT, second.TriggerKey);
    }

    [Fact]
    public void CompletingTheFirstDictationSurvivesARestart()
    {
        var first = ShortcutSettings.Load(Path_);
        first.MarkFirstDictationComplete();

        Assert.True(ShortcutSettings.Load(Path_).HasCompletedFirstDictation);
    }

    [Fact]
    public void CompletingTheFirstDictationTwiceIsHarmless()
    {
        var settings = ShortcutSettings.Load(Path_);
        settings.MarkFirstDictationComplete();
        settings.MarkFirstDictationComplete();

        Assert.True(settings.HasCompletedFirstDictation);
    }

    [Fact]
    public void ADisallowedShortcutIsRefusedAndTheOldOneKept()
    {
        var settings = ShortcutSettings.Load(Path_);
        settings.SetTrigger(ShortcutCatalog.VK_RSHIFT);

        Assert.False(settings.SetTrigger(ShortcutCatalog.VK_RMENU)); // right Alt / AltGr
        Assert.False(settings.SetTrigger(0x41));                     // the letter A
        Assert.Equal(ShortcutCatalog.VK_RSHIFT, settings.TriggerKey);
    }

    [Fact]
    public void AStoredShortcutThatIsNoLongerAllowedFallsBackToTheDefault()
    {
        // Exactly the upgrade case: a file written when right Alt was the default.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, """{"TriggerKey":165,"HasCompletedFirstDictation":true}""");

        var settings = ShortcutSettings.Load(Path_);

        Assert.Equal(ShortcutCatalog.Default, settings.TriggerKey);
        Assert.True(settings.HasCompletedFirstDictation); // the rest of the file is still honoured
    }

    [Fact]
    public void ACorruptFileLeavesAWorkingApp()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "{ this is not json");

        var settings = ShortcutSettings.Load(Path_);

        Assert.Equal(ShortcutCatalog.Default, settings.TriggerKey);
        Assert.False(settings.HasCompletedFirstDictation);
    }

    [Fact]
    public void AnEmptyFileLeavesAWorkingApp()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "");

        Assert.Equal(ShortcutCatalog.Default, ShortcutSettings.Load(Path_).TriggerKey);
    }

    [Fact]
    public void AFileHoldingJsonNullLeavesAWorkingApp()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "null");

        Assert.Equal(ShortcutCatalog.Default, ShortcutSettings.Load(Path_).TriggerKey);
    }

    [Fact]
    public void AnUnwritableLocationDoesNotThrow()
    {
        // Saving is best effort. A locked-down profile must not take a dictation down with it.
        var settings = ShortcutSettings.Load(Path.Combine(_dir, "nested", "settings.json"));

        var exception = Record.Exception(() => settings.MarkFirstDictationComplete());

        Assert.Null(exception);
        Assert.True(settings.HasCompletedFirstDictation); // in-memory value still applies
    }

    [Fact]
    public void TheSettingsFileNeverContainsDictatedText()
    {
        var settings = ShortcutSettings.Load(Path_);
        settings.SetTrigger(ShortcutCatalog.VK_SCROLL);
        settings.MarkFirstDictationComplete();

        var contents = File.ReadAllText(Path_);

        // Only the two remembered facts. This file is safe to hand to a support engineer.
        Assert.Contains("TriggerKey", contents, StringComparison.Ordinal);
        Assert.Contains("HasCompletedFirstDictation", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("Transcript", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Text", contents, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryShortcutTheUserCanCycleToRoundTrips()
    {
        var settings = ShortcutSettings.Load(Path_);
        var key = ShortcutCatalog.Default;

        for (var i = 0; i < ShortcutCatalog.Offered.Count; i++)
        {
            key = ShortcutCatalog.Next(key);

            Assert.True(settings.SetTrigger(key));
            Assert.Equal(key, ShortcutSettings.Load(Path_).TriggerKey);
        }
    }

    [Fact]
    public void TheDefaultPathIsUnderTheUsersLocalAppData()
    {
        var path = ShortcutSettings.DefaultPath();

        Assert.Contains("Flow", path, StringComparison.Ordinal);
        Assert.EndsWith("settings.json", path, StringComparison.Ordinal);
    }
}
