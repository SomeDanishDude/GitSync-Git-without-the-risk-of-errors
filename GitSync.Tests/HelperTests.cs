using Xunit;
using GitSync;
using System.IO;

namespace GitSync.Tests
{
    public class UrlHelperTests
    {
        [Theory]
        [InlineData("https://github.com/user/repo.git", "repo")]
        [InlineData("https://github.com/user/repo", "repo")]
        [InlineData("https://github.com/user/repo/", "repo")]
        [InlineData("git@github.com:user/my-repo.git", "my-repo")]
        [InlineData("ssh://git@github.com/user/proj.git", "proj")]
        [InlineData(@"C:\repos\shared\proj.git", "proj")]
        [InlineData("https://dev.azure.com/org/project/_git/repo", "repo")]
        public void GetRepoNameFromUrl_ExtractsFolderName(string url, string expected)
        {
            Assert.Equal(expected, Program.GetRepoNameFromUrl(url));
        }

        [Theory]
        [InlineData("https://github.com/user/repo.git", "https://github.com/user/repo")]
        [InlineData("git@github.com:user/repo.git", "https://github.com/user/repo")]
        [InlineData("ssh://git@github.com:22/user/repo.git", "https://github.com/user/repo")]
        [InlineData("https://ghp_secret123@github.com/user/repo.git", "https://github.com/user/repo")]
        [InlineData(@"C:\repos\remote.git", null)]
        public void ToWebUrl_ConvertsRemoteToBrowserUrl(string remote, string? expected)
        {
            Assert.Equal(expected, Program.ToWebUrl(remote));
        }

        [Theory]
        [InlineData("https://github.com/user/repo.git", true)]
        [InlineData("http://gitea.local/user/repo.git", true)]
        [InlineData("git@github.com:user/repo.git", true)]
        [InlineData("ssh://git@github.com/user/repo.git", true)]
        [InlineData("github.com/user/repo", false)]
        [InlineData("not a url", false)]
        [InlineData("", false)]
        public void LooksLikeGitUrl_AcceptsCommonForms(string url, bool expected)
        {
            Assert.Equal(expected, Program.LooksLikeGitUrl(url));
        }

        [Fact]
        public void LooksLikeGitUrl_AcceptsAnExistingLocalFolder()
        {
            Assert.True(Program.LooksLikeGitUrl(Path.GetTempPath()));
        }

        [Theory]
        [InlineData("github.com/user/repo", "https://github.com/user/repo")]
        [InlineData("www.github.com/user/repo.git", "https://github.com/user/repo.git")]
        [InlineData("https://github.com/user/repo/tree/main", "https://github.com/user/repo")]
        [InlineData("https://github.com/user/repo/blob/main/README.md", "https://github.com/user/repo")]
        [InlineData("\"https://github.com/user/repo.git\"", "https://github.com/user/repo.git")]
        [InlineData("git@github.com:user/repo.git", "git@github.com:user/repo.git")]
        [InlineData("jeg har ikke en", null)]
        public void NormalizeRemoteUrl_AcceptsWhatPeoplePaste(string input, string? expected)
        {
            Assert.Equal(expected, Program.NormalizeRemoteUrl(input));
        }

        [Theory]
        [InlineData("y", true)]
        [InlineData("Yes", true)]
        [InlineData("ja", true)]
        [InlineData("ok", true)]
        [InlineData("feature/login", false)]
        [InlineData("", false)]
        public void IsYesWord_MeansKeepTheDefault(string input, bool expected)
        {
            Assert.Equal(expected, Program.IsYesWord(input));
        }

        [Theory]
        [InlineData("https://github.com/u/r.git", "git@github.com:u/r.git", true)]
        [InlineData("https://github.com/u/r", "https://token@github.com/u/r.git", true)]
        [InlineData("https://github.com/u/r.git", "https://github.com/u/other.git", false)]
        [InlineData(@"C:\repos\shared\proj.git", @"C:\repos\shared\proj.git\", true)]
        [InlineData(@"C:\repos\shared\proj.git", @"C:\repos\shared\other.git", false)]
        public void SameRepository_SeesThroughDifferentFormsOfAnAddress(string a, string b, bool expected)
        {
            Assert.Equal(expected, Program.SameRepository(a, b));
        }

        [Fact]
        public void MaskUrl_HidesEmbeddedCredentials()
        {
            Assert.Equal("https://***@github.com/u/r.git", Program.MaskUrl("https://user:ghp_abc@github.com/u/r.git"));
            Assert.Equal("git@github.com:u/r.git", Program.MaskUrl("git@github.com:u/r.git"));
        }
    }

    public class GitHintTests
    {
        [Theory]
        [InlineData("error: src refspec main does not match any", "doesn't exist on this computer")]
        [InlineData("error: The following untracked working tree files would be overwritten by merge:\n\tREADME.md", "same names")]
        [InlineData(" ! [rejected]        main -> main (non-fast-forward)\nerror: failed to push some refs", "commits that you don't have")]
        [InlineData(" ! [rejected]        main -> main (fetch first)", "commits that you don't have")]
        [InlineData("remote: Invalid username or password.\nfatal: Authentication failed for 'https://github.com/u/r.git/'", "login was rejected")]
        [InlineData("remote: Repository not found.\nfatal: repository 'https://github.com/u/r.git/' not found", "wasn't found")]
        [InlineData("Host key verification failed.\nfatal: Could not read from remote repository.", "SSH doesn't know")]
        [InlineData("git@github.com: Permission denied (publickey).", "SSH key")]
        [InlineData("fatal: detected dubious ownership in repository at 'E:/proj'", "another Windows user")]
        [InlineData("remote: error: GH001: Large files detected.", "100 MB")]
        [InlineData("fatal: unable to access 'https://github.com/u/r.git/': Could not resolve host: github.com", "Couldn't reach")]
        public void GetFriendlyHint_ExplainsCommonFailures(string gitOutput, string expectedFragment)
        {
            var hint = Program.GetFriendlyHint(gitOutput);
            Assert.NotNull(hint);
            Assert.Contains(expectedFragment, hint);
        }

        [Theory]
        [InlineData("git@github.com:user/repo.git", "git@github.com: Permission denied (publickey).", "https://github.com/user/repo.git")]
        [InlineData("ssh://git@github.com/user/repo.git", "Host key verification failed.", "https://github.com/user/repo.git")]
        [InlineData("git@github.com:user/repo.git", "ERROR: Repository not found.", null)]
        [InlineData("https://github.com/user/repo.git", "Permission denied (publickey)", null)]
        [InlineData("git@github-work:me/proj.git", "git@github-work: Permission denied (publickey).", null)] // a ~/.ssh/config alias
        [InlineData("git@ssh.dev.azure.com:v3/org/proj/repo", "Permission denied (publickey).", null)]
        public void HttpsAlternativeFor_OnlyOffersHttpsForSshKeyProblems(string url, string gitError, string? expected)
        {
            Assert.Equal(expected, Program.HttpsAlternativeFor(new GitResult { ExitCode = 128, Error = gitError }, url));
        }

        [Theory]
        [InlineData(" M src/app.js", "modified  src/app.js")]
        [InlineData("?? notes.txt", "new       notes.txt")]
        [InlineData(" D old.txt", "deleted   old.txt")]
        [InlineData("UU README.md", "conflict  README.md")]
        [InlineData("A  added.cs", "added     added.cs")]
        public void DescribeStatusLine_TranslatesPorcelainCodes(string line, string expected)
        {
            Assert.Equal(expected, Program.DescribeStatusLine(line));
        }

        [Fact]
        public void GetFriendlyHint_IgnoresFileNamesInLists()
        {
            // A conflicted file that happens to be called "Repository not found.txt" must not trigger the remote hint
            string output = "Auto-merging Repository not found.txt\nCONFLICT (content): Merge conflict in Repository not found.txt\n\tRepository not found.txt";
            Assert.Null(Program.GetFriendlyHint(output));
        }

        [Fact]
        public void GetFriendlyHint_TellsBranchSwitchFromIncomingChanges()
        {
            Assert.Contains("Switching branches", Program.GetFriendlyHint("error: Your local changes to the following files would be overwritten by checkout:\n\tnotes.txt"));
            Assert.Contains("incoming changes", Program.GetFriendlyHint("error: Your local changes to the following files would be overwritten by merge:\n\tnotes.txt"));
        }

        [Fact]
        public void GetFriendlyHint_ReturnsNullForUnknownOutput()
        {
            Assert.Null(Program.GetFriendlyHint("Everything up-to-date"));
        }

        [Theory]
        [InlineData("3\t5\n", 3, 5)]
        [InlineData("0\t0", 0, 0)]
        [InlineData("", 0, 0)]
        [InlineData("garbage", 0, 0)]
        public void ParseAheadBehind_ReadsRevListCounts(string text, int ahead, int behind)
        {
            Assert.Equal((ahead, behind), Program.ParseAheadBehind(text));
        }
    }

    public class SetupHelperTests
    {
        [Theory]
        [InlineData("big/video.mp4", "/big/video.mp4")]
        [InlineData(@"assets\model [final].fbx", @"/assets/model \[final].fbx")]
        [InlineData("#notes.txt", "/#notes.txt")]
        [InlineData("!important.bin", "/!important.bin")]
        [InlineData("what?.dat", @"/what\?.dat")]
        public void ToGitIgnoreEntry_MatchesExactlyThatFile(string relativePath, string expected)
        {
            Assert.Equal(expected, Program.ToGitIgnoreEntry(relativePath));
        }

        [Theory]
        [InlineData("-----BEGIN RSA PRIVATE KEY-----", true)]
        [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----", true)]
        [InlineData("aws_key = AKIAIOSFODNN7EXAMPLE", true)]
        [InlineData("token: ghp_abcdefghijklmnopqrstuvwxyzABCDEFGHIJ", true)]
        [InlineData("const password = \"hunter2hunter2\";", true)]
        [InlineData("\"api_key\": \"9f8e7d6c5b4a39281706\"", true)]
        [InlineData("Server=db;User Id=sa;Password=S3cr3tP4ss;", true)]
        [InlineData("const password = \"your_password_here\";", false)]
        [InlineData("API_KEY = \"${API_KEY}\"", false)]
        [InlineData("string password = GetUserInput(\"Password: \");", false)]
        [InlineData("if (line.Contains(\"SECRET\")) return;", false)]
        public void FindSecrets_FlagsRealLookingSecretsOnly(string line, bool expected)
        {
            Assert.Equal(expected, Program.FindSecrets(line).Count > 0);
        }

        [Theory]
        [InlineData(".env", true)]
        [InlineData(".env.production", true)]
        [InlineData(".env.example", false)]
        [InlineData("id_rsa", true)]
        [InlineData("id_rsa.pub", false)]
        [InlineData("server.pem", true)]
        [InlineData("cert.pfx", true)]
        [InlineData("README.md", false)]
        public void IsSensitiveFileName_RecognisesSecretFiles(string name, bool expected)
        {
            Assert.Equal(expected, Program.IsSensitiveFileName(name));
        }

        [Theory]
        [InlineData("x\n<<<<<<< HEAD\nmine\n=======\ntheirs\n>>>>>>> origin/main\n", true)]
        [InlineData("x\r\n<<<<<<< HEAD\r\nmine\r\n=======\r\ntheirs\r\n>>>>>>> origin/main\r\n", true)]
        [InlineData("Install\n=======\n\nrun it\n", false)]           // a Markdown heading underline
        [InlineData("Git writes lines like:\n<<<<<<< HEAD\n", false)] // a document quoting one marker
        public void HasConflictMarkers_NeedsARealConflictBlock(string content, bool expected)
        {
            string file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                File.WriteAllText(file, content);
                Assert.Equal(expected, Program.HasConflictMarkers(file));
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public void MitLicense_ContainsTheFullText()
        {
            string text = Program.MitLicense("2026", "Jane Doe");
            Assert.StartsWith("MIT License", text);
            Assert.Contains("Copyright (c) 2026 Jane Doe", text);
            Assert.Contains("THE SOFTWARE IS PROVIDED \"AS IS\"", text);
            Assert.DoesNotContain("...", text);
        }

        [Fact]
        public void DetectProjectTypes_SkipsDependencyFoldersAndNeedsARealUnityProject()
        {
            string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "node_modules", "node-gyp"));
                File.WriteAllText(Path.Combine(dir, "package.json"), "{}");
                File.WriteAllText(Path.Combine(dir, "node_modules", "node-gyp", "gyp.py"), ""); // must NOT make it a Python project
                Directory.CreateDirectory(Path.Combine(dir, "Assets"));                           // "Assets" alone is not Unity
                File.WriteAllText(Path.Combine(dir, "App.csproj"), "<Project />");                 // .csproj without a .sln is still .NET
                Directory.CreateDirectory(Path.Combine(dir, "ios", "Runner.xcodeproj"));           // a folder, not a file

                var types = Program.DetectProjectTypes(dir);

                Assert.Contains("Node.js", types);
                Assert.Contains("VisualStudio", types);
                Assert.Contains("Swift", types);
                Assert.DoesNotContain("Python", types);
                Assert.DoesNotContain("Unity", types);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
