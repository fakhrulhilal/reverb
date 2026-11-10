using System.Text;

namespace Reverb.Kernel;

/// <summary>
/// The kernel process's entry point. The CLI calls it for <c>reverb __kernel</c>, and a kernel built
/// for a notebook's packages calls it from its own Main.
/// </summary>
public static class KernelProcess
{
    /// <summary>
    /// Written next to a package kernel's entry assembly when it's built: the package assemblies
    /// cells compile against, one path per line.
    /// </summary>
    public const string ReferencesFile = "reverb-references.txt";

    public static Task<int> RunAsync()
    {
        KernelTrace.Enable();
        KernelTrace.Mark("main");
        return KernelServer.RunAsync(new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)), Console.OpenStandardOutput());
    }

    /// <summary>The package assemblies this kernel was built with. Empty for the plain kernel.</summary>
    public static IReadOnlyList<string> PackageReferences()
    {
        var file = Path.Combine(AppContext.BaseDirectory, ReferencesFile);
        return File.Exists(file)
            ? [.. File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0)]
            : [];
    }
}
