using Xunit;
using GitSync;
using System.Diagnostics;

namespace GitSync.Tests
{
    // Creating the GitHub repository from Sync, uploading a folder of projects, the large-file question, updates
    [Collection("Git")]
    public class GitHubAndBatchTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "GitSyncTests", Path.GetRandomFileName());
        private readonly string originalDirectory = Directory.GetCurrentDirectory();
        private readonly TextReader originalIn = Console.In;
        private readonly TextWriter originalOut = Console.Out;
        private readonly Func<string, GitHubRepoResult> originalCreate = Program.CreateGitHubRepository;

        public GitHubAndBatchTests()
        {
            Directory.CreateDirectory(root);
            Console.SetOut(new StringWriter());
            Program.ResetSession();
        }

        public void Dispose()
        {
            Program.CreateGitHubRepository = originalCreate;
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

        // ---- Creating the repository on GitHub ----

        [Theory]
        [InlineData("Some kind of Space Sim", "Some-kind-of-Space-Sim")]
        [InlineData("Redumper CD.DVD.Blu-Ray.Optical-Discs Archiving", "Redumper-CD.DVD.Blu-Ray.Optical-Discs-Archiving")]
        [InlineData("Project-Neural-Spell-Checker", "Project-Neural-Spell-Checker")]
        [InlineData("  ..weird // name!! ", "weird-name")]
        [InlineData("æøå", "my-project")]
        public void RepositoryNamesComeFromTheFolderName(string folder, string expected)
        {
            Assert.Equal(expected, Program.SuggestRepositoryName(folder));
        }

        [Fact]
        public void SyncCreatesTheRepositoryWhenTheProjectIsntOnGitHubYet()
        {
            string bare = Bare("created.git");
            string project = Project("My Game");
            string? askedFor = null;
            Program.CreateGitHubRepository = name => { askedFor = name; return new GitHubRepoResult { Url = bare }; };
            Console.SetIn(new StringReader("y\n"));

            string url = Program.SetupRemote();

            Assert.Equal("My-Game", askedFor);
            Assert.Equal(bare, url);
            Assert.Equal(bare, Git(project, "remote", "get-url", "origin").Trim());
        }

        [Fact]
        public void AnExistingRepositoryOfTheSameNameCanBeUsed()
        {
            string bare = Bare("existing.git");
            string project = Project("Existing");
            Program.CreateGitHubRepository = _ => new GitHubRepoResult { AlreadyExists = true, Url = bare };
            Console.SetIn(new StringReader("y\ny\n")); // create? yes - use the existing one? yes

            Assert.Equal(bare, Program.SetupRemote());
        }

        [Fact]
        public void WhenGitHubSaysNoTheAddressCanStillBePasted()
        {
            string bare = Bare("pasted.git");
            Project("Refused");
            Program.CreateGitHubRepository = _ => new GitHubRepoResult { Error = "no sign-in" };
            Console.SetIn(new StringReader("y\n" + bare + "\n"));

            Assert.Equal(bare, Program.SetupRemote());
        }

        // ---- A folder that holds several projects ----

        [Fact]
        public void TheProjectsInAFolderLikeVsProjectsAreFound()
        {
            string projects = Path.Combine(root, "VS Projects");
            Directory.CreateDirectory(Path.Combine(projects, "Neural", ".git"));                    // a repository
            Write(Path.Combine(projects, "Ants", "CMakeLists.txt"), "project(Ants)\n");            // a project file
            Write(Path.Combine(projects, "Redumper", "disc-archive.ps1"), "Write-Host hi\n");      // code of its own
            Write(Path.Combine(projects, "Notes", "ideas.txt"), "ideas\n");                         // not a project
            Write(Path.Combine(projects, "src", "main.cpp"), "int main() {}\n");                  // a part, not a project

            var found = Program.FindProjectsInFolder(projects).Select(Path.GetFileName).ToList();

            Assert.Equal(new[] { "Ants", "Neural", "Redumper" }, found);
        }

        [Fact]
        public void AProjectWithSrcAndTestsFoldersIsOneProject()
        {
            string app = Path.Combine(root, "MyApp");
            Write(Path.Combine(app, "README.md"), "# App\n");
            Write(Path.Combine(app, "src", "app.py"), "print('hi')\n");
            Write(Path.Combine(app, "tests", "test_app.py"), "def test(): pass\n");
            Assert.Empty(Program.FindProjectsInFolder(app));

            string solution = Path.Combine(root, "Solution");
            Write(Path.Combine(solution, "Game.sln"), "\n");
            Directory.CreateDirectory(Path.Combine(solution, "Client", ".git"));
            Directory.CreateDirectory(Path.Combine(solution, "Server", ".git"));
            Assert.Empty(Program.FindProjectsInFolder(solution)); // the folder is a project itself
        }

        [Fact]
        public void AProjectMadeOfPartsIsNotSplitIntoRepositories()
        {
            // frontend/ and backend/ each look like a project, but the README next to them says they're one
            string web = Path.Combine(root, "WebShop");
            Write(Path.Combine(web, "README.md"), "# Web shop\n");
            Write(Path.Combine(web, "frontend", "package.json"), "{}\n");
            Write(Path.Combine(web, "backend", "package.json"), "{}\n");

            Assert.Empty(Program.FindProjectsInFolder(web));
        }

        // ---- Large files: only GitHub has the limit, and it's one question ----

        [Theory]
        [InlineData("https://github.com/you/project.git", true)]
        [InlineData("git@github.com:you/project.git", true)]
        [InlineData("https://gitlab.com/you/project.git", false)]
        public void OnlyAGitHubRemoteHasTheFileSizeLimit(string url, bool limited)
        {
            string project = Project("Remote kind");
            Git(project, "remote", "add", "origin", url);

            Assert.Equal(limited, Program.RemoteIsGitHub());
        }

        [Fact]
        public void AFolderRemoteIsNeverAskedAboutLargeFiles()
        {
            string project = Project("Folder remote");
            Git(project, "remote", "add", "origin", Bare("nas.git"));
            Console.SetIn(new StringReader("")); // a question would hit the end of the input

            Program.CheckLargeFiles(project);
        }

        [Theory]
        [InlineData("", "lfs")]
        [InlineData("s", "ignore")]
        public void LargeFilesAreOneQuestionWithLfsAsTheDefault(string answer, string expected)
        {
            if (expected == "lfs" && !Git(root, "lfs", "version").Contains("git-lfs")) return;
            string project = Project("Big files");
            Git(project, "remote", "add", "origin", "https://github.com/you/big-files.git");
            foreach (var name in new[] { "intro.mp4", "outro.mp4" })
            {
                using var file = new FileStream(Path.Combine(project, name), FileMode.Create);
                file.SetLength(101L * 1024 * 1024);
            }
            Console.SetIn(new StringReader(answer + "\n")); // one answer for both files

            Program.CheckLargeFiles(project);

            if (expected == "lfs")
            {
                Assert.Contains("filter: lfs", Git(project, "check-attr", "filter", "--", "intro.mp4"));
                Assert.Contains("filter: lfs", Git(project, "check-attr", "filter", "--", "outro.mp4"));
            }
            else
            {
                string ignore = File.ReadAllText(Path.Combine(project, ".gitignore"));
                Assert.Contains("/intro.mp4", ignore);
                Assert.Contains("/outro.mp4", ignore);
            }
        }

        // ---- Telling friends about a newer GitSync ----

        [Theory]
        [InlineData("v1.2.0", "1.2.0.0")]
        [InlineData("1.3", "1.3.0.0")]
        [InlineData("V2.0.1", "2.0.1.0")]
        public void ReleaseTagsAreReadAsVersions(string tag, string expected)
        {
            Assert.Equal(Version.Parse(expected), Program.ParseReleaseVersion(tag));
        }

        [Fact]
        public void OnlyANewerReleaseIsMentioned()
        {
            Assert.Null(Program.ParseReleaseVersion("beta"));
            Assert.True(Program.IsNewerThanThis(new Version(999, 0)));
            Assert.False(Program.IsNewerThanThis(new Version(0, 1)));
            Assert.False(Program.IsNewerThanThis(Program.CurrentVersion));
        }

        // ---- Helpers ----

        private string Project(string name)
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            Git(dir, "init", "-q");
            Program.OpenSessionFolder(dir);
            return dir;
        }

        private string Bare(string name)
        {
            string dir = Path.Combine(root, name);
            Git(root, "init", "-q", "--bare", dir);
            return dir;
        }

        private static void Write(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        private static string Git(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return output.Result;
        }
    }
}
