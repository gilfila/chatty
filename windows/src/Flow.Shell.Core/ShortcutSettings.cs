using System.Text.Json;

namespace Flow.Shell.Core;

/// <summary>
/// The two things Flow remembers between launches: which key dictates, and whether the user has
/// ever successfully dictated.
/// </summary>
/// <remarks>
/// Deliberately not the transcript store — no dictated text goes in here, so this file is safe to
/// read, log and hand to a support engineer.
///
/// <para>
/// Every failure path falls back to defaults rather than throwing. A settings file that is
/// missing, unreadable, corrupt, or written by a different build must leave Flow working with the
/// default shortcut, because the alternative is an app that will not start over a preference.
/// </para>
/// </remarks>
public sealed class ShortcutSettings
{
    private sealed record Persisted(ushort TriggerKey, bool HasCompletedFirstDictation);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;

    public ShortcutSettings(string? path = null) => _path = path ?? DefaultPath();

    /// <summary>The hold-to-talk key, always one <see cref="ShortcutCatalog"/> currently allows.</summary>
    public ushort TriggerKey { get; private set; } = ShortcutCatalog.Default;

    /// <summary>
    /// Whether the user has completed a dictation. Drives whether the first-run card is shown —
    /// the card teaches one gesture, so it retires the moment the gesture works rather than after
    /// a fixed number of launches.
    /// </summary>
    public bool HasCompletedFirstDictation { get; private set; }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Flow",
        "settings.json");

    public static ShortcutSettings Load(string? path = null)
    {
        var settings = new ShortcutSettings(path);

        try
        {
            if (File.Exists(settings._path))
            {
                var persisted = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(settings._path));
                if (persisted is not null)
                {
                    // Resolve rather than trust: a key that was allowed by an older build may not
                    // be allowed now, and honouring it could leave the user typing into their own
                    // document while they speak.
                    settings.TriggerKey = ShortcutCatalog.Resolve(persisted.TriggerKey);
                    settings.HasCompletedFirstDictation = persisted.HasCompletedFirstDictation;
                }
            }
        }
        catch (Exception)
        {
            // Unreadable or corrupt. Defaults are already in place.
        }

        return settings;
    }

    /// <summary>Adopt a new trigger key. Rejects anything the catalogue does not allow.</summary>
    public bool SetTrigger(ushort virtualKey)
    {
        if (!ShortcutCatalog.Validate(virtualKey).IsAllowed) return false;

        TriggerKey = virtualKey;
        Save();
        return true;
    }

    /// <summary>Record that the user has dictated successfully, retiring the first-run card.</summary>
    public void MarkFirstDictationComplete()
    {
        if (HasCompletedFirstDictation) return;

        HasCompletedFirstDictation = true;
        Save();
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(
                _path,
                JsonSerializer.Serialize(new Persisted(TriggerKey, HasCompletedFirstDictation), Json));
        }
        catch (Exception)
        {
            // A preference that cannot be written is not worth failing a dictation over. The
            // in-memory value still applies for this session.
        }
    }
}
