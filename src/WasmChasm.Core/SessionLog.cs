using System.Text;

namespace WasmChasm.Core;

/// <summary>FR-16: appends each session to a plain-text log in %LOCALAPPDATA%\WasmChasm\.</summary>
public sealed class SessionLog
{
    private readonly string _path;
    private readonly object _gate = new();

    public SessionLog(string path) => _path = path;

    public string FilePath => _path;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WasmChasm");

    public static SessionLog Default() => new(Path.Combine(DefaultDirectory, "wasmchasm.log"));

    public void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch (IOException) { /* logging must never break a consignment */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void BeginSession(string description) =>
        Write($"==== SESSION {description} ====");
}
