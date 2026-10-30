using System.Runtime.CompilerServices;

namespace GitSync.Tests
{
    internal static class TestSetup
    {
#pragma warning disable CA2255 // a module initializer is exactly what a test assembly wants here
        [ModuleInitializer]
#pragma warning restore CA2255
        internal static void Init()
        {
            // Keep test runs out of the real %LOCALAPPDATA%\GitSync log and recent-folder list
            Environment.SetEnvironmentVariable("GITSYNC_DATA_DIR", Path.Combine(Path.GetTempPath(), "GitSyncTests", "data"));

            // Commits made by the integration tests need an identity, whatever this machine's git config says
            Environment.SetEnvironmentVariable("GIT_AUTHOR_NAME", "GitSync Tests");
            Environment.SetEnvironmentVariable("GIT_AUTHOR_EMAIL", "tests@example.com");
            Environment.SetEnvironmentVariable("GIT_COMMITTER_NAME", "GitSync Tests");
            Environment.SetEnvironmentVariable("GIT_COMMITTER_EMAIL", "tests@example.com");
            Environment.SetEnvironmentVariable("GIT_TERMINAL_PROMPT", "0");

            // "[4] I'll fix them myself" and friends must not open Explorer windows during a test run
            Environment.SetEnvironmentVariable("GITSYNC_NO_WINDOWS", "1");
        }
    }
}
