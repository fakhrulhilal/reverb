using System.Diagnostics;
using System.Runtime.InteropServices;
using Reverb.Format;
using Reverb.Server;

namespace Reverb.Cli;

/// <summary><c>reverb edit</c> and <c>reverb new</c>: start the editor server for one notebook.</summary>
static class EditCommand
{
    sealed record Options(string Path, int Port, bool OpenBrowser, bool Edit);

    public static async Task<int> RunNewAsync(string[] args)
    {
        if (Parse("new", args) is not { } options) return 2;
        var path = System.IO.Path.GetFullPath(options.Path);
        if (File.Exists(path))
        {
            Console.Error.WriteLine($"error: '{options.Path}' already exists. Open it with 'reverb edit {options.Path}'.");
            return 1;
        }
        if (!Directory.Exists(System.IO.Path.GetDirectoryName(path)))
        {
            Console.Error.WriteLine($"error: the folder for '{options.Path}' doesn't exist.");
            return 1;
        }
        NotebookFile.Save(path, NotebookTemplates.Starter(Program.RuntimeVersion));
        Console.WriteLine($"Created {options.Path}");
        return options.Edit ? await ServeAsync(options with { Path = path }) : 0;
    }

    public static async Task<int> RunEditAsync(string[] args)
    {
        if (Parse("edit", args) is not { } options) return 2;
        if (Directory.Exists(options.Path))
        {
            Console.Error.WriteLine("error: opening a folder isn't supported yet. Pass a notebook file: reverb edit <file.cs>");
            return 2;
        }
        if (!File.Exists(options.Path))
        {
            Console.Error.WriteLine($"error: '{options.Path}' doesn't exist. Create it with 'reverb new {options.Path}'.");
            return 1;
        }
        return await ServeAsync(options);
    }

    static Options? Parse(string command, string[] args)
    {
        string? path = null;
        var port = 0;
        var open = true;
        var edit = true;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p) && p is >= 0 and <= 65535:
                    port = p;
                    i++;
                    break;
                case "--no-browser":
                    open = false;
                    break;
                case "--no-edit" when command == "new":
                    edit = false;
                    break;
                case var a when a.StartsWith('-'):
                    Console.Error.WriteLine($"error: unknown option '{a}' for 'reverb {command}'.");
                    return null;
                default:
                    if (path is not null)
                    {
                        Console.Error.WriteLine($"error: 'reverb {command}' takes one notebook file.");
                        return null;
                    }
                    path = args[i];
                    break;
            }
        }
        if (path is null)
        {
            Console.Error.WriteLine($"Usage: reverb {command} <file.cs> [--port <n>] [--no-browser]{(command == "new" ? " [--no-edit]" : "")}");
            return null;
        }
        return new Options(path, port, open, edit);
    }

    static async Task<int> ServeAsync(Options options)
    {
        foreach (var finding in AmbientBuildFiles.Find(options.Path))
            Console.Error.WriteLine($"warning RVB200: {finding.Path}: {finding.Why}");

        EditorServer server;
        try
        {
            server = await EditorServer.StartAsync(new EditorOptions
            {
                NotebookPath = options.Path,
                KernelEntryAssembly = typeof(Program).Assembly.Location,
                RuntimeVersion = Program.RuntimeVersion,
                Port = options.Port,
                Log = Console.Error,
            });
        }
        catch (InvalidDataException e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
        catch (IOException e) when (e.InnerException is System.Net.Sockets.SocketException || e.Message.Contains("address", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"error: couldn't listen on port {options.Port}: {e.Message} Try another port with --port, or leave it out to pick a free one.");
            return 1;
        }

        await using (server)
        {
            Console.WriteLine($"Editing {System.IO.Path.GetFileName(options.Path)}");
            Console.WriteLine($"  {server.Url}");
            Console.WriteLine();
            Console.WriteLine("Press Ctrl+C to stop.");
            Console.Out.Flush();
            if (options.OpenBrowser) OpenBrowser(server.Url);

            var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Stop(PosixSignalContext ctx)
            {
                ctx.Cancel = true;
                stop.TrySetResult();
            }
            using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
            await stop.Task;
            Console.WriteLine("Stopping. Saving the notebook and shutting down the kernel.");
        }
        return 0;
    }

    static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            Console.WriteLine("Couldn't open a browser. Open the address above yourself.");
        }
    }
}
