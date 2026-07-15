using EmbeddedSass.Net.Internal.Protocol;

namespace EmbeddedSass.Net.Internal.Process;

internal interface IProcessLauncher
{
    ICompilerProcess Launch(CompilerOptionsSnapshot options);
}
