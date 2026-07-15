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

    private sealed class CompilerProcess : ICompilerProcess
    {
        private readonly System.Diagnostics.Process _process;

        public CompilerProcess(System.Diagnostics.Process process)
        {
            _process = process;
        }

        public Stream StandardInput => _process.StandardInput.BaseStream;

        public Stream StandardOutput => _process.StandardOutput.BaseStream;

        public Stream StandardError => _process.StandardError.BaseStream;

        public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
            _process.WaitForExitAsync(cancellationToken);

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited between the check and the kill request.
            }
        }

        public ValueTask DisposeAsync()
        {
            _process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
