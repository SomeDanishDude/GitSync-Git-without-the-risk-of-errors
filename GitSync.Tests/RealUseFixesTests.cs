using Xunit;
using GitSync;
using System.Diagnostics;

namespace GitSync.Tests
{
    // From a real upload: "master" typed at the branch question while the newest work was on "phase6", a summary that said
    // "1 file" for a whole project, false password alarms on code - and the gaps found while looking into it
    [Collection("Git")]
    public class RealUseFixesTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "GitSyncTests", Path.GetRandomFileName());
        private readonly string originalDirectory = Directory.GetCurrentDirectory();
        private readonly TextReader originalIn = Console.In;
        private readonly TextWriter originalOut = Console.Out;
        private readonly StringWriter output = new StringWriter();
        private readonly Func<string, GitHubRepoResult> originalCreate = Program.CreateGitHubRepository;
        private readonly Func<string?> originalAccount = Program.GitHubAccountName;

        public RealUseFixesTests()
        {
            Directory.CreateDirectory(root);
            Console.SetOut(output);
            Program.ResetSession();
        }

        public void Dispose()
        {
            Program.CreateGitHubRepository = originalCreate;
            Program.GitHubAccountName = originalAccount;
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

        // ---- The branch question: a branch typed in that doesn't have the newest work ----

        // master: Phase 5. phase6: master + Phase 6 and Phase 7, checked out - like the spell checker was
        private string PhaseRepo()
        {
            string repo = Path.Combine(root, "spell-checker");
            Directory.CreateDirectory(repo);
            Git(repo, "init", "-q", "-b", "master");
            Write(repo, "engine.cpp", "// phase 5\n");
            Commit(repo, "Phase 5");
            Git(repo, "checkout", "-q", "-b", "phase6");
            Write(repo, "engine.cpp", "// phase 6\n");
            Commit(repo, "Phase 6");
            Write(repo, "SettingsWindow.cpp", "// phase 7\n");
            Commit(repo, "Phase 7");
            Program.OpenSessionFolder(repo);
            return repo;
        }

        [Fact]
        public void TypingABranchThatIsBehindPutsYourNewestWorkOnIt()
        {
            string repo = PhaseRepo();
            string newest = Rev(repo, "phase6");
            Console.SetIn(new StringReader("\n")); // Enter = yes

            string branch = "phase6";
            Assert.True(Program.SwitchBranch("master", ref branch));

            Assert.Equal("master", branch);
            Assert.Equal("master", Git(repo, "symbolic-ref", "--short", "HEAD").Trim());
            Assert.Equal(newest, Rev(repo, "master"));
            Assert.Equal("// phase 6", File.ReadAllText(Path.Combine(repo, "engine.cpp")).Trim()); // nothing in the folder changed
            Assert.True(File.Exists(Path.Combine(repo, "SettingsWindow.cpp")));
        }

        [Fact]
        public void SayingNoSwitchesToTheBranchAsItIsAndKeepsTheWorkWhereItWas()
        {
            string repo = PhaseRepo();
            string newest = Rev(repo, "phase6");
            Console.SetIn(new StringReader("n\n"));

            string branch = "phase6";
            Assert.True(Program.SwitchBranch("master", ref branch));

            Assert.Equal("master", branch);
            Assert.Equal("// phase 5", File.ReadAllText(Path.Combine(repo, "engine.cpp")).Trim());
            Assert.Equal(newest, Rev(repo, "phase6"));
            Assert.Contains("aren't part of this upload", output.ToString());
        }

        [Fact]
        public void BranchesThatWentTheirOwnWayAreOnlySwitchedWhenAsked()
        {
            string repo = PhaseRepo();
            Git(repo, "checkout", "-q", "master");
            Write(repo, ".gitignore", "out/\n");
            Commit(repo, "Init Spell Checker Repo"); // master's own commit, like the .gitignore one in the real case
            Git(repo, "checkout", "-q", "phase6");

            Console.SetIn(new StringReader("1\n")); // stay
            string branch = "phase6";
            Assert.True(Program.SwitchBranch("master", ref branch));
            Assert.Equal("phase6", branch);
            Assert.Equal("phase6", Git(repo, "symbolic-ref", "--short", "HEAD").Trim());

            Console.SetIn(new StringReader("2\n")); // switch anyway
            Assert.True(Program.SwitchBranch("master", ref branch));
            Assert.Equal("master", branch);
        }

        [Fact]
        public void CancelPutsTheMovedBranchBack()
        {
            string repo = PhaseRepo();
            string before = Rev(repo, "master");
            Console.SetIn(new StringReader("\n"));
            string branch = "phase6";
            Program.SwitchBranch("master", ref branch);

            Program.PerformCleanup();

            Assert.Equal("phase6", Git(repo, "symbolic-ref", "--short", "HEAD").Trim());
            Assert.Equal(before, Rev(repo, "master"));
        }

        [Fact]
        public void CancelKeepsACommitMadeOnTheMovedBranch()
        {
            string repo = PhaseRepo();
            Console.SetIn(new StringReader("\n"));
            string branch = "phase6";
            Program.SwitchBranch("master", ref branch);
            Write(repo, "notes.txt", "more\n");
            Commit(repo, "More");
            string committed = Rev(repo, "master");

            Program.PerformCleanup();

            Assert.Equal(committed, Rev(repo, "master"));
        }

        // ---- Status and the summary ----

        [Fact]
        public void OtherBranchesWithWorkThatIsOnlyHereAreFound()
        {
            string repo = PhaseRepo();
            string bare = Bare();
            Git(repo, "remote", "add", "origin", bare);
            Git(repo, "push", "-q", "origin", "master");

            Assert.Equal(("phase6", 2), Assert.Single(Program.BranchesNotOnRemote("master")));

            Git(repo, "push", "-q", "origin", "phase6");
            Assert.Empty(Program.BranchesNotOnRemote("master"));
        }

        [Fact]
        public void TheSummaryCountsTheWholeProjectOnItsFirstUpload()
        {
            string repo = PhaseRepo(); // engine.cpp, SettingsWindow.cpp
            string bare = Bare();
            Git(repo, "remote", "add", "origin", bare);
            Write(repo, ".gitignore", "out/\n");
            Git(repo, "add", ".gitignore");

            Program.PrintSummary(bare, "phase6", "Init", new List<string> { "added    .gitignore" });

            string text = output.ToString();
            Assert.Contains("Files to Push:  3 (all of them - 'phase6' is new on the remote)", text);
            Assert.Contains("Changed now:    1", text);
        }

        [Fact]
        public void ADailySyncStillShowsJustTheChangedFiles()
        {
            string repo = PhaseRepo();
            string bare = Bare();
            Git(repo, "remote", "add", "origin", bare);
            Git(repo, "push", "-q", "origin", "phase6");
            Write(repo, "engine.cpp", "// phase 8\n");
            Git(repo, "add", "engine.cpp");

            Program.PrintSummary(bare, "phase6", "Phase 8", new List<string> { "modified engine.cpp" });

            string text = output.ToString();
            Assert.Contains("Files to Push:  1", text);
            Assert.DoesNotContain("Changed now", text);
        }

        // ---- Secrets: code that talks about passwords isn't one, the tokens hobby projects leak are ----

        [Theory]
        [InlineData("BOOL password = FALSE;")]
        [InlineData("bool password = false;")]
        [InlineData("h.caret.password = true;  // focus moved to a password field")]
        [InlineData("int pwd = GetLength(text);")]
        [InlineData("const char* url = \"postgres://postgres:postgres@localhost:5432/app\";")]
        [InlineData("MONGO_URL=mongodb://user:password@localhost:27017/db")]
        [InlineData("amqp://guest:guest@localhost:5672/")]
        [InlineData("REDIS_URL=redis://:${REDIS_PASSWORD}@cache:6379")]
        [InlineData("//registry.npmjs.org/:_authToken=${NPM_TOKEN}")]
        public void CodeThatOnlyTalksAboutPasswordsIsNotASecret(string line)
        {
            Assert.Empty(Program.FindSecrets(line));
        }

        [Fact]
        public void TheTokensHobbyProjectsLeakAreFound()
        {
            // Put together here, so this file itself holds nothing that looks like a real token
            var leaks = new[]
            {
                "DISCORD_TOKEN = '" + "MTIzNDU2Nzg5MDEyMzQ1Njc4" + "." + "GaBcDe" + "." + new string('x', 30) + "'",
                "client.run(\"" + "OTk5ODg4Nzc3NjY2NTU1NDQ0" + "." + "Xy-z_1" + "." + new string('Q', 37) + "-\")",
                "WEBHOOK = 'https://discord.com/api/webhooks/" + "123456789012345678/" + new string('f', 68) + "'",
                "bot = Bot('" + "1234567890" + ":" + "AAH" + new string('b', 32) + "')",
                "const uri = 'mongodb+srv://app:" + "Hunter2pass" + "@cluster0.example.net/app';",
                "DATABASE_URL = \"postgres://ants:" + "S3cretPw" + "@localhost:5432/ants\"",
                "//registry.npmjs.org/:_authToken=" + "npm" + "_" + new string('c', 36),
                "token = \"" + "hf" + "_" + new string('d', 34) + "\"",
                "GITLAB_TOKEN=" + "glpat" + "-" + new string('e', 20),
                "SENDGRID_API_KEY=" + "SG" + "." + new string('a', 22) + "." + new string('b', 43),
                "Data Source=.;Initial Catalog=Shop;User ID=shop;Password=" + "shop1234",
            };
            foreach (var leak in leaks) Assert.True(Program.FindSecrets(leak).Count > 0, $"not found: {leak}");
        }

        // ---- Project types: templates and the files a project needs ----

        [Fact]
        public void TheUnrealTemplateKeepsTheGamesIconAndPakLists()
        {
            string repo = PlainRepo("unreal-template", Program.GitIgnoreTemplates["Unreal"]);
            Assert.False(Ignored(repo, "Build/Windows/Application.ico"));
            Assert.False(Ignored(repo, "Build/Windows/PakBlacklist-Shipping.txt"));
            Assert.True(Ignored(repo, "Build/Windows/FileOpenOrder/GameOpenOrder.log"));
            Assert.True(Ignored(repo, "Binaries/Win64/Game.exe"));
        }

        [Fact]
        public void TheGradleTemplateKeepsTheWrapperThatBuildsTheProject()
        {
            // Java's "*.jar" first, as in a project that has both
            string repo = PlainRepo("gradle-template", Program.GitIgnoreTemplates["Java"].Concat(Program.GitIgnoreTemplates["Gradle"]));
            Assert.False(Ignored(repo, "gradle/wrapper/gradle-wrapper.jar"));
            Assert.False(Ignored(repo, "app/src/main/java/com/game/build/Builder.java"));
            Assert.True(Ignored(repo, "app/build/outputs/apk/app.apk"));
            Assert.True(Ignored(repo, "local.properties"));
        }

        [Fact]
        public void TheGoTemplateLeavesACommittedVendorFolderAlone()
        {
            Assert.DoesNotContain(Program.GitIgnoreTemplates["Go"], line => line.Contains("vendor"));
        }

        [Theory]
        [InlineData("build.gradle", "Gradle")]
        [InlineData("settings.gradle.kts", "Gradle")]
        [InlineData("composer.json", "PHP")]
        [InlineData("analysis.ipynb", "Jupyter")]
        [InlineData("platformio.ini", "PlatformIO")]
        [InlineData("thesis.tex", "LaTeX")]
        public void MoreKindsOfProjectsAreRecognised(string file, string type)
        {
            string dir = Path.Combine(root, "detect-" + Path.GetRandomFileName());
            Write(dir, file, "x\n");
            Assert.Contains(type, Program.DetectProjectTypes(dir));
        }

        [Fact]
        public void APaperInADocsFolderDoesNotMakeALatexProject()
        {
            string dir = Path.Combine(root, "docs-paper");
            Write(dir, "docs/paper.tex", "\\documentclass{article}\n");
            Write(dir, "main.py", "print(1)\n");
            Assert.DoesNotContain("LaTeX", Program.DetectProjectTypes(dir));
        }

        [Theory]
        [InlineData("gradle/wrapper/gradle-wrapper.jar")]
        [InlineData("gradlew.bat")]
        [InlineData("kernels/blur.cu")]
        [InlineData("sketch/sketch.ino")]
        [InlineData("notebooks/train.ipynb")]
        public void FilesTheseProjectsNeedAreKnown(string path)
        {
            Assert.NotNull(Program.CoreFileReason(new Program.ProjectShape { Root = root }, path));
        }

        [Fact]
        public void CopiesToolsMakeAreNotNeeded()
        {
            Assert.Null(Program.CoreFileReason(new Program.ProjectShape { Root = root }, "notebooks/.ipynb_checkpoints/train-checkpoint.ipynb"));
            Assert.Null(Program.CoreFileReason(new Program.ProjectShape { Root = root }, ".pio/build/uno/src/main.ino.cpp"));
        }

        [Fact]
        public void AnUnrealGamesIconInBuildIsPartOfTheProject()
        {
            var unreal = new Program.ProjectShape { Root = root, Unreal = true };
            Assert.NotNull(Program.CoreFileReason(unreal, "Build/Windows/Application.ico"));
            Assert.Null(Program.CoreFileReason(unreal, "Build/Windows/FileOpenOrder/GameOpenOrder.log"));
            Assert.Null(Program.CoreFileReason(new Program.ProjectShape { Root = root }, "Build/Windows/Application.ico")); // only in an Unreal project
        }

        // ---- A repository that was deleted on GitHub ----

        [Fact]
        public void YourOwnDeletedRepositoryCanBeCreatedAgain()
        {
            string? asked = null;
            Program.GitHubAccountName = () => "me";
            Program.CreateGitHubRepository = name => { asked = name; return new GitHubRepoResult { Url = $"https://github.com/me/{name}.git" }; };
            Console.SetIn(new StringReader("y\n"));
            var notFound = new GitResult { ExitCode = 128, Error = "remote: Repository not found.\nfatal: repository 'https://github.com/me/Space-Sim.git/' not found" };

            Assert.Equal("https://github.com/me/Space-Sim.git", Program.OfferToRecreateGitHubRepository(notFound, "https://github.com/me/Space-Sim.git"));
            Assert.Equal("Space-Sim", asked);
        }

        [Fact]
        public void SomeoneElsesRepositoryIsNeverCreatedOnYourAccount()
        {
            bool created = false;
            Program.GitHubAccountName = () => "me";
            Program.CreateGitHubRepository = _ => { created = true; return new GitHubRepoResult { Error = "no" }; };
            var notFound = new GitResult { ExitCode = 128, Error = "remote: Repository not found." };
            Console.SetIn(new StringReader("")); // nothing may be asked

            Assert.Null(Program.OfferToRecreateGitHubRepository(notFound, "https://github.com/a-friend/their-game.git"));
            Assert.Null(Program.OfferToRecreateGitHubRepository(notFound, "https://gitlab.com/me/project.git"));
            Assert.Null(Program.OfferToRecreateGitHubRepository(new GitResult { ExitCode = 128, Error = "fatal: Authentication failed" }, "https://github.com/me/game.git"));
            Assert.False(created);
        }

        [Fact]
        public void CommitsOnlyTheGoneRepositoryHadAreKeptOnABranch()
        {
            string bare = Bare();
            string other = Path.Combine(root, "other-computer");
            Directory.CreateDirectory(other);
            Git(other, "init", "-q", "-b", "main");
            Write(other, "a.txt", "a\n");
            Commit(other, "A");
            Git(other, "push", "-q", bare, "main");
            Git(other, "checkout", "-q", "-b", "theirs");
            Write(other, "b.txt", "b\n");
            Commit(other, "B");
            Git(other, "push", "-q", bare, "theirs");
            string repo = Path.Combine(root, "here");
            Git(root, "clone", "-q", bare, repo);
            Program.OpenSessionFolder(repo);

            Program.KeepCommitsOnlyInRemoteBranches();

            Assert.Equal(Rev(repo, "refs/remotes/origin/theirs"), Rev(repo, "refs/heads/theirs"));
            Assert.Equal("", Git(repo, "branch", "--list", "main-from-github").Trim()); // main is here already: nothing to keep
        }

        // ---- Helpers ----

        private string Bare()
        {
            string bare = Path.Combine(root, "github-" + Path.GetRandomFileName() + ".git");
            Git(root, "init", "-q", "--bare", "-b", "main", bare);
            return bare;
        }

        private string PlainRepo(string name, IEnumerable<string> gitignore)
        {
            string repo = Path.Combine(root, name);
            Directory.CreateDirectory(repo);
            Git(repo, "init", "-q");
            File.WriteAllText(Path.Combine(repo, ".gitignore"), string.Join("\n", gitignore) + "\n");
            return repo;
        }

        private static bool Ignored(string repo, string path)
        {
            Write(repo, path, "x\n");
            var psi = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "check-ignore", "-q", "--no-index", path }) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }

        private static void Commit(string dir, string message)
        {
            Git(dir, "add", "-A");
            Git(dir, "commit", "-q", "-m", message);
        }

        private static string Rev(string dir, string rev) => Git(dir, "rev-parse", rev).Trim();

        private static void Write(string dir, string relPath, string content)
        {
            string full = Path.Combine(dir, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        private static string Git(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            string error = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(" ", args)} failed: {error}");
            return stdout.Result;
        }
    }
}
