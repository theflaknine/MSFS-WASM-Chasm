using System.Diagnostics;

namespace WasmChasm.Core;

/// <summary>FR-7: reports whether the sim is running.</summary>
public interface ISimProcessDetector
{
    bool IsSimRunning();
}

public sealed class SimProcessDetector : ISimProcessDetector
{
    private readonly IReadOnlyList<string> _names;

    public SimProcessDetector(IEnumerable<string> processNames) => _names = processNames.ToList();

    public bool IsSimRunning()
    {
        foreach (var name in _names)
        {
            var procs = Process.GetProcessesByName(name);
            try
            {
                if (procs.Length > 0) return true;
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }

        return false;
    }
}
