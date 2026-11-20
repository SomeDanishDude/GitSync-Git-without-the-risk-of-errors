using Xunit;
using GitSync;
using System.Diagnostics;
using System.Security.Cryptography;

namespace GitSync.Tests
{
    // The "Safe to delete?" check. A wrong SAFE means someone deletes the only copy of their work, so every case that has
    // something only on this computer must come out NOT SAFE - and the check itself must never change anything.
    [Collection("Git")]
    public class SafeToDeleteTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "GitSyncTests", Path.GetRandomFileName());
        private readonly string originalDirectory = Directory.GetCurrentDirectory();
        private readonly TextReader originalIn = Console.In;
        private readonly TextWriter originalOut = Console.Out;
        private readonly string remote;
        private const string AppV1 = "print('version 1')\n";
        private const string AppV2 = "print('version 2')\n";

        public SafeToDeleteTests()
        {
            Directory.CreateDirectory(root);
            Console.SetOut(new StringWriter());
            Program.ResetSession();
            // Kept copies go here, not into the real Documents folder
            Environment.SetEnvironmentVariable("GITSYNC_SAVE_DIR", Path.Combine(root, "saved"));

            // GitHub: a README, ignore rules, and src/app.py in two versions
            remote = Path.Combine(root, "remote.git");
            Git(root, "init", "-q", "--bare", remote);
            Git(remote, "symbolic-ref", "HEAD", "refs/heads/main");
            string seed = Path.Combine(root, "seed");
            Directory.CreateDirectory(seed);
            Git(seed, "init", "-q");
            Git(seed, "symbolic-ref", "HEAD", "refs/heads/main");
            Write(seed, "README.md", "# Project\n");
            Write(seed, ".gitignore", "bin/\n.env\n");
            Write(seed, "src/app.py", AppV1);
            Git(seed, "add", "-A");
            Git(seed, "commit", "-q", "-m", "First");
            Write(seed, "src/app.py", AppV2);
            Git(seed, "commit", "-q", "-am", "Second");
            Git(seed, "push", "-q", remote, "HEAD:refs/heads/main");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("GITSYNC_SAVE_DIR", null);
            Program.DeleteCheckTempRepos();
            Program.ResetSession();
            Directory.SetCurrentDirectory(originalDirectory);
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
            catch { }
        }

        // ---- A clone that is fully on GitHub, then one change at a time ----

        [Fact]
        public void AnUntouchedCloneIsSafe()
        {
            Assert.True(CheckClone(Clone()).Safe);
        }

        [Theory]
        [InlineData("src/app.py", "print('edited here')\n", "Different")]
        [InlineData("notes.txt", "my notes\n", "OnlyHere")]
        [InlineData(".env", "API_KEY=secret\n", "OnlyHere")]         // ignored, so Sync wouldn't upload it either
        [InlineData("build/plan.txt", "not ignored\n", "OnlyHere")]   // "build" alone doesn't make it build output
        [InlineData("bin/.env", "API_KEY=secret\n", "OnlyHere")]     // a key file is never build output
        public void AFileThatIsOnlyHereMakesItNotSafe(string path, string content, string expected)
        {
            string clone = Clone();
            Write(clone, path, content);

            var check = CheckClone(clone);

            Assert.False(check.Safe);
            Assert.Equal(expected, check.Files.Single(f => f.Path == path).Kind.ToString());
        }

        [Fact]
        public void AnEditThatHappensToMatchAnotherFileIsStillAnEdit()
        {
            // app.py now reads exactly like README.md - but GitHub's app.py never did, so the edit would be lost
            string clone = Clone();
            Write(clone, "src/app.py", "# Project\n");

            var check = CheckClone(clone);

            Assert.False(check.Safe);
            Assert.Equal(FileKind.Different, check.Files.Single(f => f.Path == "src/app.py").Kind);
        }

        [Fact]
        public void TheSameFileOnAnotherBranchOfGitHubCounts()
        {
            string dev = Clone();
            Git(dev, "checkout", "-q", "-b", "dev");
            Write(dev, "src/app.py", "print('dev version')\n");
            Git(dev, "commit", "-q", "-am", "Dev");
            Git(dev, "push", "-q", "origin", "dev");
            string clone = Clone();
            Write(clone, "src/app.py", "print('dev version')\n");

            var check = CheckClone(clone);

            Assert.True(check.Safe);
            Assert.Equal(FileKind.Elsewhere, check.Files.Single(f => f.Path == "src/app.py").Kind);
        }

        [Fact]
        public void ALargeFileWhoseOnlyCopyIsInTheLocalLfsStoreIsNotSafe()
        {
            // The folder holds Git LFS's small "pointer"; the file itself is only in .git\lfs - and was never uploaded
            if (!GitLfsInstalled()) return;
            string bare = Path.Combine(root, "lfs-remote.git");
            Git(root, "init", "-q", "--bare", bare);
            string repo = Path.Combine(root, "lfs-repo");
            Directory.CreateDirectory(repo);
            Git(repo, "init", "-q");
            Git(repo, "symbolic-ref", "HEAD", "refs/heads/main");
            Git(repo, "lfs", "track", "*.bin");
            File.WriteAllBytes(Path.Combine(repo, "big.bin"), Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray());
            Git(repo, "add", "-A");
            Git(repo, "commit", "-q", "-m", "Big file");
            Git(repo, "remote", "add", "origin", bare);
            RunGit(repo, new Dictionary<string, string> { ["GIT_LFS_SKIP_PUSH"] = "1" }, "push", "-q", "-u", "origin", "main");
            File.Delete(Path.Combine(repo, "big.bin"));
            RunGit(repo, new Dictionary<string, string> { ["GIT_LFS_SKIP_SMUDGE"] = "1" }, "checkout", "--", "big.bin");

            var check = Program.CheckFolder(repo, bare, repo, "");

            Assert.False(check.Safe);
            Assert.Equal(FileKind.LfsUnconfirmed, check.Files.Single(f => f.Path == "big.bin").Kind);
        }

        [Fact]
        public void IgnoredBuildOutputDoesNotCount()
        {
            string clone = Clone();
            Write(clone, "bin/app.dll", "\0binary");
            Write(clone, "node_modules/pkg/index.js", "module.exports = 1;\n");

            var check = CheckClone(clone);

            Assert.True(check.Safe);
            Assert.Equal(FileKind.Generated, check.Files.Single(f => f.Path == "bin/app.dll").Kind);
        }

        [Fact]
        public void VcpkgAndCMakeWorkingFoldersCountAsRecreatable()
        {
            // A real C++ project came out NOT SAFE over 7374 files in vcpkg_installed - which "vcpkg install" brings back
            string clone = Clone();
            Write(clone, "AntColony/vcpkg_installed/vcpkg/blds/brotli/config-x64-windows-dbg-out.log", "build log\n");
            Write(clone, "AntColony/vcpkg_installed/x64-windows/include/brotli/decode.h", "#pragma once\n");
            Write(clone, "AntColony/cmake-build-debug/main.obj", "\0obj");
            Write(clone, "AntColony/build/CMakeFiles/CMakeOutput.log", "cmake\n");
            Write(clone, "AntColony/ipch/AntColony.ipch", "\0pch");

            var check = CheckClone(clone);

            Assert.True(check.Safe, string.Join(", ", check.Files.Where(f => f.Blocks).Select(f => f.Path)));
            Assert.All(check.Files.Where(f => f.Path.StartsWith("AntColony/")), f => Assert.Equal(FileKind.Generated, f.Kind));
        }

        [Fact]
        public void ALongListIsShownByFolderButSecretsKeepTheirOwnLine()
        {
            var files = Enumerable.Range(0, 100).Select(i => new CheckedFile { Path = $"data/raw/file{i}.csv", Kind = FileKind.OnlyHere, Ignored = true })
                .Append(new CheckedFile { Path = ".env", Kind = FileKind.OnlyHere, Ignored = true, Detail = "holds passwords or keys" })
                .Append(new CheckedFile { Path = "notes.txt", Kind = FileKind.OnlyHere })
                .ToList();

            var lines = Program.DescribeFiles(files, 15);

            Assert.Contains("    data/raw/  (100 files, left out by .gitignore)", lines);
            Assert.Contains(lines, l => l.StartsWith("    .env"));
            Assert.Contains("    notes.txt", lines);
            Assert.True(lines.Count <= 5, string.Join("\n", lines));
            // A short list still names every file
            Assert.Equal(2, Program.DescribeFiles(files.Skip(100).ToList(), 15).Count);
        }

        [Fact]
        public void AnUnpushedCommitOnAnotherBranchMakesItNotSafe()
        {
            string clone = Clone();
            Git(clone, "checkout", "-q", "-b", "experiment");
            Write(clone, "idea.txt", "an idea\n");
            Git(clone, "add", "idea.txt");
            Git(clone, "commit", "-q", "-m", "Idea");
            Git(clone, "checkout", "-q", "main");

            var check = CheckClone(clone);

            Assert.False(check.Safe);
            Assert.Contains(check.Problems, p => p.Text.Contains("'experiment'"));
        }

        [Fact]
        public void OtherBranchesAndTagsAreUploadedAndThenItIsSafe()
        {
            string clone = Clone();
            Git(clone, "checkout", "-q", "-b", "experiment");
            Write(clone, "idea.txt", "an idea\n");
            Git(clone, "add", "idea.txt");
            Git(clone, "commit", "-q", "-m", "Idea");
            Git(clone, "checkout", "-q", "main");
            Git(clone, "tag", "v1.0");

            var check = CheckClone(clone);
            Assert.False(check.Safe);
            Assert.Equal(2, check.Uploads.Count);

            Program.UploadBranchesAndTags(check);

            Assert.True(CheckClone(clone).Safe);
            Assert.Contains("experiment", RunGit(remote, "branch", "--list", "experiment"));
        }

        [Fact]
        public void ABranchWithAPasswordFileIsNeverUploadedDirectly()
        {
            string clone = Clone();
            Git(clone, "checkout", "-q", "-b", "keys");
            Write(clone, "server.pem", "-----BEGIN RSA PRIVATE KEY-----\nabc\n-----END RSA PRIVATE KEY-----\n");
            Git(clone, "add", "server.pem");
            Git(clone, "commit", "-q", "-m", "Key");
            Git(clone, "checkout", "-q", "main");

            Program.UploadBranchesAndTags(CheckClone(clone));

            Assert.False(CheckClone(clone).Safe);
            Assert.Equal("", RunGit(remote, "branch", "--list", "keys").Trim());
        }

        [Fact]
        public void ABranchWhosePasswordLookalikeIsFineCanStillBeUploaded()
        {
            string clone = Clone();
            Git(clone, "checkout", "-q", "-b", "settings");
            Write(clone, "db.cs", "var cs = \"Server=db;User Id=sa;Password=" + "S3cr3tP4ss" + ";\";\n");
            Git(clone, "add", "db.cs");
            Git(clone, "commit", "-q", "-m", "Settings");
            Git(clone, "checkout", "-q", "main");

            Console.SetIn(new StringReader("n\n"));
            Program.UploadBranchesAndTags(CheckClone(clone));
            Assert.Equal("", RunGit(remote, "branch", "--list", "settings").Trim());

            Console.SetIn(new StringReader("y\n"));
            Program.UploadBranchesAndTags(CheckClone(clone));
            Assert.Contains("settings", RunGit(remote, "branch", "--list", "settings"));
        }

        // ---- What a build or Visual Studio leaves next to the project ----

        [Fact]
        public void CopiesABuildPutInItsOutputFolderAreBuildOutput()
        {
            string clone = Clone();
            Write(clone, "bin/Debug/app.py", AppV2); // GitHub has this as src/app.py; bin/ is ignored

            var check = CheckClone(clone);

            Assert.Equal(FileKind.Generated, Assert.Single(check.Files, f => f.Path == "bin/Debug/app.py").Kind);
            Assert.True(check.Safe);
        }

        [Fact]
        public void AnEmptyVisualStudioUserFileDoesNotBlock()
        {
            string clone = Clone();
            File.AppendAllText(Path.Combine(clone, ".git", "info", "exclude"), "*.user\n");
            Write(clone, "app/Game.vcxproj.user", "﻿<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Project ToolsVersion=\"Current\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n  <PropertyGroup />\n</Project>");

            var check = CheckClone(clone);

            Assert.Equal(FileKind.Generated, Assert.Single(check.Files, f => f.Path == "app/Game.vcxproj.user").Kind);
            Assert.True(check.Safe);
        }

        [Fact]
        public void VisualStudioUserSettingsAreSaidWhatTheyAre()
        {
            string clone = Clone();
            File.AppendAllText(Path.Combine(clone, ".git", "info", "exclude"), "*.user\n");
            Write(clone, "app/Game.vcxproj.user", "<Project>\n  <PropertyGroup>\n    <LocalDebuggerCommandArguments>--dev</LocalDebuggerCommandArguments>\n  </PropertyGroup>\n</Project>\n");

            var check = CheckClone(clone);

            Assert.Contains("Visual Studio settings", Assert.Single(check.Files, f => f.Path == "app/Game.vcxproj.user").Detail);
            Assert.False(check.Safe);
        }

        [Fact]
        public void AnUnrealIconLeftOutByBuildIsFoundAsNeeded()
        {
            string repo = NewPlainRepo("unreal-icon", "Build/\n");
            Write(repo, "Game.uproject", "{}\n");
            Write(repo, "Build/Windows/Application.ico", "icon\n");
            Write(repo, "Build/Windows/FileOpenOrder/GameOpenOrder.log", "log\n");

            Assert.Equal("Build/Windows/Application.ico", Assert.Single(Program.FindIgnoredCoreFiles(repo)).Path);
        }

        [Fact]
        public void ABranchThatWentAnotherWayOnGitHubIsLeftToTheUser()
        {
            string other = Clone();
            Git(other, "checkout", "-q", "-b", "feature");
            Write(other, "a.txt", "theirs\n");
            Git(other, "add", "a.txt");
            Git(other, "commit", "-q", "-m", "Theirs");
            Git(other, "push", "-q", "origin", "feature");
            string clone = Clone();
            Git(clone, "checkout", "-q", "-b", "feature", "origin/main");
            Write(clone, "b.txt", "mine\n");
            Git(clone, "add", "b.txt");
            Git(clone, "commit", "-q", "-m", "Mine");
            Git(clone, "checkout", "-q", "main");

            var check = CheckClone(clone);

            Assert.False(check.Safe);
            Assert.Empty(check.Uploads);
            Assert.Contains(check.Problems, p => p.Text.Contains("'feature'") && p.Fix == ProblemFix.User);
        }

        [Fact]
        public void YourOwnGitHookScriptsCountButInstalledOnesDoNot()
        {
            string clone = Clone();
            string hooks = Path.Combine(clone, ".git", "hooks");
            File.WriteAllText(Path.Combine(hooks, "pre-push"), "#!/bin/sh\ncommand -v git-lfs >/dev/null 2>&1 || exit 2\ngit lfs pre-push \"$@\"\n");
            Assert.True(CheckClone(clone).Safe);

            File.WriteAllText(Path.Combine(hooks, "pre-commit"), "#!/bin/sh\necho checking my code\n");
            var check = CheckClone(clone);

            // A file like any other that never goes to GitHub: listed, and a copy can be kept
            Assert.False(check.Safe);
            Assert.Equal(FileKind.OnlyHere, Kind(check, ".git/hooks/pre-commit"));
            Console.SetIn(new StringReader("\n"));
            Program.OfferNextStep(check);
            Assert.True(check.Safe);
        }

        [Fact]
        public void AnotherWorkFolderCountsOnlyWhileItExists()
        {
            string clone = Clone();
            string worktree = Path.Combine(root, "second-folder");
            Git(clone, "worktree", "add", "-q", worktree, "-b", "second");
            Assert.Contains(CheckClone(clone).Problems, p => p.Text.Contains("another work folder"));

            Directory.Delete(worktree, true);

            Assert.True(CheckClone(clone).Safe);
        }

        [Fact]
        public void ABareRepositoryInsideIsReportedAsOneThing()
        {
            string clone = Clone();
            Git(clone, "init", "-q", "--bare", Path.Combine(clone, "backup.git"));

            var check = CheckClone(clone);

            Assert.False(check.Safe);
            Assert.Contains(check.Problems, p => p.Text.Contains("backup.git/"));
            Assert.DoesNotContain(check.Files, f => f.Path.StartsWith("backup.git/"));
        }

        [Fact]
        public void AStashMakesItNotSafe()
        {
            string clone = Clone();
            Write(clone, "README.md", "# Project, half-edited\n");
            Git(clone, "stash", "-q");

            var check = CheckClone(clone);

            Assert.False(check.Safe);
            Assert.Contains(check.Problems, p => p.Text.Contains("git stash") && p.Text.Contains("Source Control")); // and what to do about it
        }

        [Fact]
        public void ATagThatIsOnlyHereMakesItNotSafe()
        {
            string clone = Clone();
            Git(clone, "tag", "v1.0");

            var check = CheckClone(clone);

            Assert.False(check.Safe);
            Assert.Contains(check.Problems, p => p.Text.Contains("v1.0"));
        }

        [Fact]
        public void ANestedRepositoryWithoutGitHubMakesItNotSafe()
        {
            string clone = Clone();
            string nested = Path.Combine(clone, "tools");
            Directory.CreateDirectory(nested);
            Git(nested, "init", "-q");
            Write(nested, "tool.py", "print('tool')\n");

            var check = CheckClone(clone);

            Assert.False(check.Safe);
            Assert.Single(check.Nested);
        }

        [Fact]
        public void AFileThatCantBeReadMakesItNotSafe()
        {
            string clone = Clone();
            using (new FileStream(Path.Combine(clone, "README.md"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var check = CheckClone(clone);

                Assert.False(check.Safe);
                Assert.Equal(FileKind.Unreadable, check.Files.Single(f => f.Path == "README.md").Kind);
            }
        }

        [Fact]
        public void AGitHubThatCantBeReachedIsNeverSafe()
        {
            string clone = Clone();

            var check = Program.CheckFolder(clone, Path.Combine(root, "missing.git"), clone, "");

            Assert.False(check.Safe);
            Assert.NotNull(check.ReadError);
        }

        // ---- Plain folders (no .git): copies, ZIP downloads, old versions ----

        [Fact]
        public void AZipDownloadWithWindowsLineEndingsIsSafe()
        {
            string copy = Path.Combine(root, "Downloads", "project");
            Write(copy, "project-main/README.md", "# Project\r\n");
            Write(copy, "project-main/.gitignore", "bin/\r\n.env\r\n");
            Write(copy, "project-main/src/app.py", AppV2.Replace("\n", "\r\n"));

            var check = Program.CheckFolder(copy, remote, null, "");

            Assert.True(check.Safe, string.Join(", ", check.Files.Select(f => $"{f.Path}={f.Kind}")));
            Assert.Equal("project-main", check.LocalBase);
        }

        [Fact]
        public void AnOlderCopyIsSafeBecauseGitHubIsNewer()
        {
            string copy = Path.Combine(root, "old-copy");
            Write(copy, "README.md", "# Project\n");
            Write(copy, "src/app.py", AppV1);

            var check = Program.CheckFolder(copy, remote, null, "");

            Assert.True(check.Safe);
            Assert.Equal(FileKind.NewerOnGitHub, check.Files.Single(f => f.Path == "src/app.py").Kind);
        }

        [Fact]
        public void ACopyWithAnEditIsNotSafe()
        {
            string copy = Path.Combine(root, "edited-copy");
            Write(copy, "README.md", "# Project\n");
            Write(copy, "src/app.py", "print('newer, only here')\n");

            var check = Program.CheckFolder(copy, remote, null, "");

            Assert.False(check.Safe);
            Assert.Equal(FileKind.Different, check.Files.Single(f => f.Path == "src/app.py").Kind);
        }

        // ---- Which situation it is: asked of git, not worked out from paths ----

        [Fact]
        public void ARepositoryOpenedThroughALinkIsStillTheRepositoryItself()
        {
            // Git reports the real path; comparing paths took the repository for one of its own subfolders and skipped
            // every check of its history - so an unpushed branch came out SAFE
            string clone = Clone();
            Git(clone, "checkout", "-q", "-b", "experiment");
            Write(clone, "idea.txt", "an idea\n");
            Git(clone, "add", "idea.txt");
            Git(clone, "commit", "-q", "-m", "Idea");
            Git(clone, "checkout", "-q", "main");
            string link = Path.Combine(root, "via-link");
            using (var mklink = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{clone}\"") { UseShellExecute = false, RedirectStandardOutput = true })!)
            {
                mklink.WaitForExit();
            }
            Assert.True(Directory.Exists(Path.Combine(link, ".git")));

            var (repoRoot, prefix) = Program.AssessFolder(link);
            Assert.Equal("", prefix);
            Assert.NotNull(repoRoot);

            var check = Program.CheckFolder(link, remote, repoRoot, prefix);
            Assert.False(check.Safe);
            Assert.Contains(check.Problems, p => p.Text.Contains("'experiment'"));
        }

        [Fact]
        public void ASubfolderIsFoundAsPartOfItsRepository()
        {
            string clone = Clone();

            var (repoRoot, prefix) = Program.AssessFolder(Path.Combine(clone, "src"));

            Assert.Equal("src/", prefix);
            Assert.NotNull(repoRoot);
        }

        // ---- Only part of a project ----

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void ASubfolderOfARepositoryIsComparedWithThatFolderOnGitHub(bool edit, bool safe)
        {
            string clone = Clone();
            if (edit) Write(clone, "src/app.py", "print('edited here')\n");

            var check = Program.CheckFolder(Path.Combine(clone, "src"), remote, clone, "src/");

            Assert.Equal(safe, check.Safe);
            Assert.Equal(edit ? FileKind.Different : FileKind.Same, check.Files.Single(f => f.Path == "app.py").Kind);
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void ACopyOfOneFolderOfTheProjectIsFoundThere(bool edit, bool safe)
        {
            string kept = Path.Combine(root, "kept-src");
            Write(kept, "app.py", edit ? "print('edited here')\n" : AppV2);

            var check = Program.CheckFolder(kept, remote, null, "");

            Assert.Equal(safe, check.Safe);
            Assert.Equal("src/", check.Prefix);
            Assert.Equal(edit ? FileKind.Different : FileKind.Same, check.Files.Single(f => f.Path == "app.py").Kind);
        }

        [Fact]
        public void AFolderWithNoGitHubAddressIsNeverSafe()
        {
            string copy = Path.Combine(root, "unknown");
            Write(copy, "README.md", "# Project\n");

            Assert.False(Program.CheckFolder(copy, "", null, "").Safe);
        }

        // ---- A .gitignore that leaves out files the project needs ----

        [Fact]
        public void ATooBroadIgnoreRuleIsCaughtButBuildFoldersAreNot()
        {
            string repo = NewPlainRepo("broad-rules", "*.txt\nsrc/\nmybuild/\n");
            Write(repo, "CMakeLists.txt", "project(Ants)\n");      // "*.txt" takes it along
            Write(repo, "notes.txt", "my notes\n");                // a text file - not something the project needs
            Write(repo, "src/main.cpp", "int main() {}\n");        // "src/" hides the code
            Write(repo, "mybuild/CMakeCache.txt", "# cache\n");    // a CMake build folder: generated copies only
            Write(repo, "mybuild/Makefile", "all:\n");
            Write(repo, "mybuild/generated/version.cpp", "int v;\n");

            var core = Program.FindIgnoredCoreFiles(repo);

            Assert.Equal(new[] { "CMakeLists.txt", "src/main.cpp" }, core.Select(c => c.Path).OrderBy(p => p, StringComparer.Ordinal));
        }

        [Fact]
        public void AUnityProjectsAssetsAreCaughtButWhatUnityRecreatesIsNot()
        {
            string repo = NewPlainRepo("unity", "x64/\n*.csproj\n*.sln\nAssets/AssetStoreTools*\n");
            Write(repo, "ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.0.1f1\n");
            Write(repo, "Assets/Plugins/x64/native.dll", "\0dll");          // a plugin a stray "x64/" rule hides
            Write(repo, "Assets/AssetStoreTools/tool.cs", "class T {}\n");    // left out on purpose by Unity's template
            Write(repo, "Game.csproj", "<Project />\n");                       // Unity writes these itself
            Write(repo, "Game.sln", "\n");

            var core = Program.FindIgnoredCoreFiles(repo);

            Assert.Equal("Assets/Plugins/x64/native.dll", Assert.Single(core).Path);
        }

        [Theory]
        [InlineData("y", true)]
        [InlineData("n", false)]
        public void SyncUploadsThemWhenTheUserSaysSo(string answer, bool added)
        {
            string repo = NewPlainRepo("sync-core", "*.txt\n");
            Write(repo, "CMakeLists.txt", "project(Ants)\n");
            Console.SetIn(new StringReader(answer + "\n"));

            Program.IncludeIgnoredCoreFiles(repo);

            Assert.Equal(added, RunGit(repo, "ls-files", "--cached").Contains("CMakeLists.txt"));
        }

        [Fact]
        public void TheCheckNamesTheRuleThatLeavesOutAProjectFile()
        {
            string clone = Clone();
            File.AppendAllText(Path.Combine(clone, ".gitignore"), "*.txt\n");
            Write(clone, "CMakeLists.txt", "project(Ants)\n");

            var check = CheckClone(clone);

            var file = check.Files.Single(f => f.Path == "CMakeLists.txt");
            Assert.False(check.Safe);
            Assert.True(file.Core);
            Assert.Contains("\"*.txt\"", file.Detail);
        }

        [Fact]
        public void APythonEnvironmentIsNeverMistakenForTheProjectsCode()
        {
            // Named "env", which no folder list knows - its pyvenv.cfg gives it away
            string repo = NewPlainRepo("venv", "env/\n");
            Write(repo, "env/pyvenv.cfg", "home = C:\\Python312\n");
            Write(repo, "env/Lib/site-packages/pip/__init__.py", "__version__ = '24.0'\n");
            Assert.Empty(Program.FindIgnoredCoreFiles(repo));

            string clone = Clone();
            Write(clone, "env/pyvenv.cfg", "home = C:\\Python312\n");
            Write(clone, "env/Lib/site-packages/pip/__init__.py", "__version__ = '24.0'\n");
            var check = CheckClone(clone);
            Assert.True(check.Safe);
            Assert.All(check.Files.Where(f => f.Path.StartsWith("env/")), f => Assert.Equal(FileKind.Generated, f.Kind));
        }

        [Fact]
        public void AnInSourceCMakeBuildsOutputIsNotFlaggedButAHandWrittenModuleIs()
        {
            string repo = NewPlainRepo("in-source", "*.cmake\nMake*\nCMakeCache.txt\n");
            Write(repo, "CMakeLists.txt", "project(Ants)\n");
            Write(repo, "CMakeCache.txt", "# cache\n");              // CMake built right here
            Write(repo, "Makefile", "all:\n");
            Write(repo, "cmake_install.cmake", "# install\n");
            Write(repo, "src/Makefile", "all:\n");
            Write(repo, "cmake/FindFoo.cmake", "# written by hand\n"); // "*.cmake" takes this one along by mistake

            var core = Program.FindIgnoredCoreFiles(repo);

            Assert.Equal("cmake/FindFoo.cmake", Assert.Single(core).Path);
        }

        [Fact]
        public void ARuleForOneExactFileIsRespected()
        {
            string repo = NewPlainRepo("exact-rule", "config_local.h\n*.txt\n");
            Write(repo, "config_local.h", "#define DEBUG 1\n");       // named on purpose
            Write(repo, "src/config_local.h", "#define DEBUG 1\n");
            Write(repo, "CMakeLists.txt", "project(Ants)\n");          // taken along by "*.txt"

            var core = Program.FindIgnoredCoreFiles(repo);

            Assert.Equal("CMakeLists.txt", Assert.Single(core).Path);
        }

        [Fact]
        public void ANoIsRememberedAndOnlyNewFilesAreAskedAbout()
        {
            string repo = NewPlainRepo("keep-out", "*.txt\n");
            Write(repo, "CMakeLists.txt", "project(Ants)\n");
            Console.SetIn(new StringReader("n\n"));
            Program.IncludeIgnoredCoreFiles(repo);
            Assert.Empty(Program.FindIgnoredCoreFiles(repo));

            Write(repo, "tools/CMakeLists.txt", "add_executable(tool)\n");

            Assert.Equal("tools/CMakeLists.txt", Assert.Single(Program.FindIgnoredCoreFiles(repo)).Path);
        }

        private string NewPlainRepo(string name, string gitignore)
        {
            string repo = Path.Combine(root, name);
            Directory.CreateDirectory(repo);
            Git(repo, "init", "-q");
            Write(repo, ".gitignore", gitignore);
            return repo;
        }

        // ---- Files no upload takes: one question, then the verdict ----

        [Fact]
        public void CachesTempFilesAndLogsLeftOutByGitignoreAreLeftovers()
        {
            string clone = Clone();
            IgnoreHere(clone, "graphify-out/\n*.log\n.tmp/\n");
            Write(clone, "graphify-out/cache/abc.json", "{}\n");
            Write(clone, "graphify-out/graph.json", "{\"nodes\":[]}\n");
            Write(clone, "debug.log", "started\n");
            Write(clone, ".tmp/scratch.bin", "x\n");

            var check = CheckClone(clone);

            Assert.Equal(FileKind.Generated, Kind(check, "graphify-out/cache/abc.json"));
            Assert.Equal(FileKind.Generated, Kind(check, "debug.log"));
            Assert.Equal(FileKind.Generated, Kind(check, ".tmp/scratch.bin"));
            Assert.Equal(FileKind.OnlyHere, Kind(check, "graphify-out/graph.json")); // a tool's output is asked about, never guessed
        }

        [Fact]
        public void KeepingACopyMakesItSafeWithoutChangingTheFolder()
        {
            string clone = Clone();
            IgnoreHere(clone, "graphify-out/\n");
            Write(clone, "graphify-out/graph.json", "{\"nodes\":[1,2,3]}\n");
            Write(clone, "graphify-out/GRAPH_REPORT.md", "# Report\n");
            string before = Fingerprint(clone);
            var check = CheckClone(clone);
            Assert.False(check.Safe);

            Console.SetIn(new StringReader("\n")); // Enter = keep a copy
            Program.OfferNextStep(check);

            Assert.True(check.Safe);
            string zip = Assert.Single(Directory.GetFiles(Path.Combine(root, "saved"), "*.zip"));
            using (var archive = System.IO.Compression.ZipFile.OpenRead(zip))
            {
                Assert.Equal(new[] { "graphify-out/GRAPH_REPORT.md", "graphify-out/graph.json" }, archive.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal));
            }
            Program.DeleteCheckTempRepos();
            Assert.Equal(before, Fingerprint(clone)); // the copy went elsewhere: nothing in the folder changed
            Assert.True(CheckClone(clone).Safe);      // and the next check knows about it

            Write(clone, "graphify-out/graph.json", "{\"nodes\":[1,2,3,4]}\n");
            Assert.False(CheckClone(clone).Safe);     // changed since the copy was made: this version isn't in it
        }

        [Fact]
        public void NotNeededIsRememberedUntilSomethingChanges()
        {
            string clone = Clone();
            IgnoreHere(clone, "renders/\n");
            Write(clone, "renders/intro.txt", "frame data\n");
            var check = CheckClone(clone);

            Console.SetIn(new StringReader("n\n"));
            Program.OfferNextStep(check);

            Assert.True(check.Safe);
            Assert.False(Directory.Exists(Path.Combine(root, "saved")) && Directory.GetFiles(Path.Combine(root, "saved")).Length > 0);
            Assert.True(CheckClone(clone).Safe); // still safe on the next run

            Write(clone, "renders/outro.txt", "new frames\n");
            Assert.False(CheckClone(clone).Safe); // something new there: asked again
        }

        [Fact]
        public void PasswordFilesAreNeverJustNotNeeded()
        {
            string clone = Clone(); // GitHub's .gitignore leaves out .env
            IgnoreHere(clone, "renders/\n");
            Write(clone, ".env", "API_KEY=" + "abc123secret\n");
            Write(clone, "renders/intro.txt", "frame data\n");
            var check = CheckClone(clone);

            Console.SetIn(new StringReader("n\n"));
            Program.OfferNextStep(check);

            Assert.Equal(FileKind.NotNeeded, Kind(check, "renders/intro.txt"));
            Assert.Equal(FileKind.OnlyHere, Kind(check, ".env"));
            Assert.False(check.Safe);
            Assert.False(CheckClone(clone).Safe);
        }

        [Fact]
        public void APasswordFileCanBeKeptInTheCopy()
        {
            string clone = Clone();
            Write(clone, ".env", "API_KEY=" + "abc123secret\n");
            var check = CheckClone(clone);

            Console.SetIn(new StringReader("\n"));
            Program.OfferNextStep(check);

            Assert.True(check.Safe);
            Assert.Equal(FileKind.Saved, Kind(check, ".env"));
        }

        [Fact]
        public void ACopyIsNeverPutInsideTheFolderBeingChecked()
        {
            string clone = Clone();
            Environment.SetEnvironmentVariable("GITSYNC_SAVE_DIR", Path.Combine(clone, "backup"));
            IgnoreHere(clone, "renders/\n");
            Write(clone, "renders/intro.txt", "frame data\n");
            var check = CheckClone(clone);
            Console.SetIn(new StringReader("")); // nothing may be asked

            Program.OfferNextStep(check);

            Assert.False(check.Safe);
            Assert.False(Directory.Exists(Path.Combine(clone, "backup")));
        }

        [Fact]
        public void AFolderInsideAnotherIsCountedWithIt()
        {
            var files = Enumerable.Range(0, 58).Select(i => new CheckedFile { Path = $"graphify-out/cache/{i}.json", Kind = FileKind.OnlyHere, Ignored = true, Size = 1024 })
                .Concat(Enumerable.Range(0, 8).Select(i => new CheckedFile { Path = $"graphify-out/f{i}.json", Kind = FileKind.OnlyHere, Ignored = true, Size = 1024 }))
                .ToList();

            Assert.Equal("    graphify-out/  (66 files, 66 KB, left out by .gitignore)", Assert.Single(Program.DescribeFiles(files, 15)));
        }

        private static void IgnoreHere(string clone, string rules) => File.AppendAllText(Path.Combine(clone, ".git", "info", "exclude"), rules);

        private static FileKind Kind(FolderCheck check, string path) => Assert.Single(check.Files, f => f.Path == path).Kind;

        // ---- The check only reads ----

        [Fact]
        public void TheCheckChangesNothingInTheFolderOrItsRepository()
        {
            string clone = Clone();
            Write(clone, "src/app.py", "print('edited here')\n");
            Write(clone, ".env", "API_KEY=secret\n");
            Git(clone, "tag", "v1.0");
            string before = Fingerprint(clone);

            CheckClone(clone);
            Program.DeleteCheckTempRepos();

            Assert.Equal(before, Fingerprint(clone));
            Assert.DoesNotContain(Directory.GetDirectories(Path.GetTempPath(), "GitSync-check-*"), d => Directory.GetCreationTime(d) > DateTime.Now.AddMinutes(-5));
        }

        [Fact]
        public void GitBlobIdMatchesGit()
        {
            string path = Path.Combine(root, "blob.txt");
            File.WriteAllBytes(path, new byte[] { (byte)'h', (byte)'i', (byte)'\r', (byte)'\n' });
            string expected = RunGit(root, "hash-object", "--no-filters", path).Trim();
            Assert.Equal(expected, Program.GitBlobId(File.ReadAllBytes(path), sha256: false));
        }

        // ---- Helpers ----

        private string Clone()
        {
            string clone = Path.Combine(root, "clone-" + Path.GetRandomFileName());
            Git(root, "clone", "-q", remote, clone);
            return clone;
        }

        private FolderCheck CheckClone(string clone) => Program.CheckFolder(clone, remote, clone, "");

        // Every file's path, size and content hash - .git included
        private static string Fingerprint(string dir)
        {
            using var sha = SHA256.Create();
            return string.Join("\n", Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => $"{Path.GetRelativePath(dir, f)} {Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(f)))}"));
        }

        private static void Write(string dir, string relPath, string content)
        {
            string full = Path.Combine(dir, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        private static void Git(string dir, params string[] args) => RunGit(dir, args);

        private static string RunGit(string dir, params string[] args) => RunGit(dir, new Dictionary<string, string>(), args);

        private static bool GitLfsInstalled()
        {
            try { return RunGit(Path.GetTempPath(), "lfs", "version").Contains("git-lfs"); } catch { return false; }
        }

        private static string RunGit(string dir, Dictionary<string, string> environment, params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false
            };
            foreach (var (name, value) in environment) psi.Environment[name] = value;
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("core.autocrlf=false");
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEndAsync();
            string error = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(" ", args)} failed: {error}");
            return output.Result;
        }
    }
}
