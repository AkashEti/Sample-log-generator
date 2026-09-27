using System.Text;
using System.Text.Json;

namespace SampleLogGenerator.Output;

/// <summary>Appends one JSON object per line. The file stays readable (and tail-able) while being written.</summary>
public sealed class JsonlWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();

    public JsonlWriter(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public string Path { get; }

    public void WriteMany<T>(IEnumerable<T> items)
    {
        lock (_lock)
        {
            foreach (var item in items)
                _writer.WriteLine(JsonSerializer.Serialize(item, JsonDefaults.Options));
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_lock) _writer.Dispose();
    }
}
