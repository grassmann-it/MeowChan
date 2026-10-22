using System.Text;

namespace MeowChan.PluginHost.Runtime;

internal sealed class BoundedLog(int maximumBytes)
{
    private readonly Queue<string> _lines = new();
    private int _bytes;
    public void Add(string line)
    {
        if (line.Length > maximumBytes / 4) line = line[^Math.Max(1, maximumBytes / 4)..];
        lock (_lines)
        {
            int bytes = Encoding.UTF8.GetByteCount(line);
            while (_bytes + bytes > maximumBytes && _lines.Count > 0) _bytes -= Encoding.UTF8.GetByteCount(_lines.Dequeue());
            if (bytes <= maximumBytes) { _lines.Enqueue(line); _bytes += bytes; }
        }
    }
    public string[] Snapshot() { lock (_lines) return _lines.ToArray(); }
}
