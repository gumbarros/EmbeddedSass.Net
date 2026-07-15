using System.ComponentModel;
using System.Diagnostics;
using EmbeddedSass.Net.Diagnostics;
using EmbeddedSass.Net.Internal.Protocol;

namespace EmbeddedSass.Net.Internal.Process;

internal sealed class ProcessLauncher : IProcessLauncher
{
    public ICompilerProcess Launch(CompilerOptionsSnapshot options)
    {
        if (!File.Exists(options.CompilerPath))
        {
            throw new SassCompilerException(
                $"The embedded Sass compiler does not exist at '{options.CompilerPath}'.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = options.CompilerPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in options.CompilerArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--embedded");

        var process = new System.Diagnostics.Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new SassCompilerException(
                    $"The embedded Sass compiler '{options.CompilerPath}' did not start.");
            }

            return new CompilerProcess(process);
        }
        catch (SassCompilerException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or IOException)
        {
            process.Dispose();
            throw new SassCompilerException(
                $"Could not start the embedded Sass compiler '{options.CompilerPath}'.",
                innerException: exception);
        }
    }

    private sealed class CompilerProcess(System.Diagnostics.Process process) : ICompilerProcess
    {
        public Stream StandardInput => process.StandardInput.BaseStream;

        public Stream StandardOutput => process.StandardOutput.BaseStream;

        public Stream StandardError => process.StandardError.BaseStream;

        public int? ExitCode => process.HasExited ? process.ExitCode : null;

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
            process.WaitForExitAsync(cancellationToken);

        public void Kill()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited between the check and the kill request.
            }
        }

        public ValueTask DisposeAsync()
        {
            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
