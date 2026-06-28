using System.Runtime.InteropServices;

namespace Weaver;

/// <summary>
/// Snapshot of the machine a benchmark test run executed on. Captured so that
/// scores can be compared fairly across different hardware / OS configurations.
/// </summary>
public class EnvironmentMetadata
{
    public string Os { get; set; } = "";
    public string OsArchitecture { get; set; } = "";
    public int CpuCores { get; set; }
    public double RamGb { get; set; }
    public string MachineName { get; set; } = "";
    public string Runtime { get; set; } = "";

    /// <summary>
    /// Collects machine metadata in a cross-platform way. RAM is reported from the
    /// GC's view of total available memory, which is a reasonable portable proxy for
    /// physical RAM (and respects container limits when running inside one).
    /// </summary>
    public static EnvironmentMetadata Collect()
    {
        double ramGb = 0;
        try
        {
            var totalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (totalBytes > 0)
                ramGb = Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 1);
        }
        catch { /* best-effort */ }

        return new EnvironmentMetadata
        {
            Os = RuntimeInformation.OSDescription,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            CpuCores = Environment.ProcessorCount,
            RamGb = ramGb,
            MachineName = SafeMachineName(),
            Runtime = RuntimeInformation.FrameworkDescription
        };
    }

    static string SafeMachineName()
    {
        try { return Environment.MachineName; }
        catch { return ""; }
    }
}
