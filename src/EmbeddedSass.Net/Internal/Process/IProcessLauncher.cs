using EmbeddedSass.Internal.Protocol;

namespace EmbeddedSass.Internal.Process;

internal interface IProcessLauncher
{
    ICompilerProcess Launch(CompilerOptionsSnapshot options);
}
