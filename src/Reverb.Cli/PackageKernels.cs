using Reverb.Format;
using Reverb.Kernel;

namespace Reverb.Cli;

/// <summary>Gets the kernel build a notebook's packages need, telling the user on stderr when that takes a while.</summary>
static class PackageKernels
{
    public static string ToolDirectory => Path.GetDirectoryName(typeof(Program).Assembly.Location)!;

    /// <summary>Null for a notebook with no packages. Throws <see cref="KernelBuildException"/> if the build fails.</summary>
    public static Task<KernelBuild?> EnsureAsync(string notebookPath, IEnumerable<Directive> directives)
    {
        var packages = KernelBuilder.PackagesOf(directives);
        return KernelBuilder.EnsureAsync(new KernelBuildRequest
        {
            Packages = packages,
            ToolDirectory = ToolDirectory,
            NotebookDirectory = Path.GetDirectoryName(Path.GetFullPath(notebookPath)),
        }, step => Console.Error.WriteLine($"{step}..."));
    }
}
