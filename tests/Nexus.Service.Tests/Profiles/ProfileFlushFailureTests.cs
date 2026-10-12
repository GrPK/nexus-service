using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Profiles;

public class ProfileFlushFailureTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonConfigStore _store;
    private readonly ProfileManager _profiles;

    public ProfileFlushFailureTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-profile-flush-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
        _profiles = new ProfileManager(_store);
        _profiles.Initialize();
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void A_profile_file_that_cannot_be_written_does_not_throw_out_of_dispose()
    {
        // A directory at the staging path makes the write fail the way a locked file does on Windows.
        var files = Directory.GetFiles(_tempDir, "profile-*.json");
        Assert.NotEmpty(files);
        foreach (var file in files)
            Directory.CreateDirectory(file + ".tmp");
        _store.Update(s => s.DeviceCategorySeeded = true);

        Assert.Null(Record.Exception(_profiles.Dispose));
    }
}
