using System.Diagnostics;
using System.Text;

namespace Reverb.Kernel;

/// <summary>
/// Startup timing for the kernel process, written to stderr when REVERB_KERNEL_TRACE is set.
/// The client shows the kernel's stderr when it doesn't start, so this says how far it got.
/// </summary>
public static class KernelTrace
{
    static bool enabled;

    /// <summary>Called by the kernel process at startup; sessions in other processes stay quiet.</summary>
    public static void Enable() => enabled = Environment.GetEnvironmentVariable("REVERB_KERNEL_TRACE") is { Length: > 0 };
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static readonly Lock gate = new();
    static StreamWriter? writer;

    public static void Mark(string step)
    {
        if (!enabled) return;
        lock (gate)
        {
            writer ??= new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine($"[kernel {Environment.ProcessId} +{clock.ElapsedMilliseconds} ms] {step}");
        }
    }
}
