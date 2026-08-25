namespace FractalFlameCurator.Storage;

public sealed class WorkspacePreferenceStore
{
    private readonly string _filePath;

    public WorkspacePreferenceStore(string preferencesDirectory)
    {
        _filePath = Path.Combine(Path.GetFullPath(preferencesDirectory), "workspace.txt");
    }

    public string? Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return null;
            var workspace = File.ReadAllText(_filePath).Trim();
            return string.IsNullOrWhiteSpace(workspace) ? null : Path.GetFullPath(workspace);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(string workspace)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, Path.GetFullPath(workspace));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }
}
