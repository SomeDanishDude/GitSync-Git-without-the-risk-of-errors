using Xunit;
using GitSync;
using System.Diagnostics;
using System.IO;

namespace GitSync.Tests
{
    [CollectionDefinition("Git", DisableParallelization = true)]
    public class GitCollection { }

    // Runs real git against throwaway repositories. These tests change the process working directory and
    // Console.In, so they never run in parallel with anything else.
    [Collection("Git")]
    public class GitIntegrationTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "GitSyncTests", Path.GetRandomFileName());
        private readonly string originalDirectory = Directory.GetCurrentDirectory();
        private readonly TextReader originalIn = Console.In;
        private readonly TextWriter originalOut = Console.Out;

        public GitIntegrationTests()
        {
            Directory.CreateDirectory(root);
            Console.SetOut(new StringWriter());
            Program.ResetSession();
        }

        public void Dispose()
        {
            Program.ResetSession();
            Directory.SetCurrentDirectory(originalDirectory);
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
            try
            {
                // git makes its object files read-only
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
            catch { }
        }

        [Fact]
        public void GetCurrentBranch_WorksBeforeTheFirstCommit()
        {
            // The original push bug: "rev-parse --abbrev-ref HEAD" fails in a repository without commits,
            // so "master" was never renamed and "git push origin main" failed with "src refspec main does not match any"
            Directory.SetCurrentDirectory(NewRepo("fresh"));
            Assert.Equal("main", Program.GetCurrentBranch());
        }

        [Fact]
        public void CheckRemoteBranch_TellsMissingFromExistingFromUnreachable()
        {
            string remote = NewRemoteWithReadme("remote", "# hi\n");
            string local = NewRepo("local");
            Git(local, "remote", "add", "origin", remote);
            Directory.SetCurrentDirectory(local);

            Assert.Equal(RemoteBranchState.Exists, Program.CheckRemoteBranch("main", out _));
            Assert.Equal(RemoteBranchState.Missing, Program.CheckRemoteBranch("feature", out _));

            // The original SafetyPull took this error output for "branch exists"
            Git(local, "remote", "set-url", "origin", Path.Combine(root, "does-not-exist.git"));
            Assert.Equal(RemoteBranchState.Error, Program.CheckRemoteBranch("main", out _));
        }

        [Fact]
        public void IntegrateRemote_JoinsARemoteThatWasCreatedWithAReadme()
        {
            string remote = NewRemoteWithReadme("remote", "# from GitHub\n");
            string local = NewRepo("local");
            Commit(local, "main.py", "print('hi')\n", "Initial commit");
            Git(local, "remote", "add", "origin", remote);
            Directory.SetCurrentDirectory(local);

            string branch = "main";
            Assert.True(Program.IntegrateRemote(ref branch, out int pulled, out bool remoteBranchExists));

            Assert.True(remoteBranchExists);
            Assert.Equal(1, pulled);
            Assert.True(File.Exists(Path.Combine(local, "README.md")));
            Git(local, "push", "-q", "origin", "HEAD:refs/heads/main"); // a fast-forward now, so this must succeed
        }

        [Theory]
        [InlineData("1", "# mine\n")]
        [InlineData("2", "# from GitHub\n")]
        public void IntegrateRemote_ResolvesAReadmeThatExistsOnBothSides(string answer, string expectedReadme)
        {
            string remote = NewRemoteWithReadme("remote", "# from GitHub\n");
            string local = NewRepo("local");
            Commit(local, "README.md", "# mine\n", "Initial commit");
            Git(local, "remote", "add", "origin", remote);
            Directory.SetCurrentDirectory(local);
            Console.SetIn(new StringReader(answer + "\n"));

            string branch = "main";
            Assert.True(Program.IntegrateRemote(ref branch, out _, out _));

            Assert.Equal(expectedReadme, File.ReadAllText(Path.Combine(local, "README.md")).Replace("\r\n", "\n"));
            Assert.False(File.Exists(Path.Combine(local, ".git", "MERGE_HEAD")));
            Git(local, "push", "-q", "origin", "HEAD:refs/heads/main");
        }

        [Fact]
        public void IntegrateRemote_AsksBeforeJoiningAnUnrelatedProject()
        {
            // Pasting the URL of a different, existing project must not silently merge the two
            string remote = Path.Combine(root, "other-project.git");
            Git(root, "init", "-q", "--bare", remote);
            string other = NewRepo("other-project");
            Commit(other, "game.gd", "extends Node\n", "Start game");
            Commit(other, "player.gd", "extends Node2D\n", "Add player");
            Git(other, "push", "-q", remote, "HEAD:refs/heads/main");

            string local = NewRepo("local");
            Commit(local, "main.py", "print('hi')\n", "Initial commit");
            Git(local, "remote", "add", "origin", remote);
            Directory.SetCurrentDirectory(local);
            Console.SetIn(new StringReader("3\n")); // [3] Cancel

            string branch = "main";
            Assert.False(Program.IntegrateRemote(ref branch, out _, out _));

            Assert.False(File.Exists(Path.Combine(local, "game.gd")));
            Assert.False(File.Exists(Path.Combine(local, ".git", "MERGE_HEAD")));
        }

        [Fact]
        public void IntegrateRemote_FinishesAMergeWhoseIncomingReadmeHasAHeadingUnderline()
        {
            // "History" underlined with "=======" is a Markdown heading, not a leftover conflict marker
            var (remote, a, b) = TwoComputers("notes.txt", "v1\n");
            Commit(b, "notes.txt", "changed on B\n", "B edit");
            Commit(b, "README.md", "History\n=======\n\nAll of it.\n", "Add readme");
            Git(b, "push", "-q", "origin", "main");
            Commit(a, "notes.txt", "changed on A\n", "A edit");
            Directory.SetCurrentDirectory(a);
            Console.SetIn(new StringReader("1\n")); // keep MY version of notes.txt

            string branch = "main";
            Assert.True(Program.IntegrateRemote(ref branch, out _, out _));

            Assert.False(File.Exists(Path.Combine(a, ".git", "MERGE_HEAD")));
            Assert.Contains("=======", File.ReadAllText(Path.Combine(a, "README.md")));
            Git(a, "push", "-q", "origin", "HEAD:refs/heads/main");
        }

        [Fact]
        public void IntegrateRemote_LeaveItKeepsTheUnfinishedMergeThroughCleanup()
        {
            // "[2] Leave it - I'll sort it out myself" must not be undone by the cleanup at the end of the mode
            var (remote, a, b) = TwoComputers("README.md", "# App\n");
            Commit(b, "README.md", "# App from B\n", "B edit");
            Commit(b, "other.txt", "o\n", "B adds a file");
            Git(b, "push", "-q", "origin", "main");
            Commit(a, "README.md", "Install\nrun it\n", "A edit");
            Program.OpenSessionFolder(a);
            string readme = Path.Combine(a, "README.md");
            // [4] fix it myself: the user removes <<<<<<< and >>>>>>> but forgets the ======= line, then "Leave it"
            Console.SetIn(new ScriptedInput(("4", null), ("", () => File.WriteAllText(readme, "Install\nrun it\n=======\n# App from B\n")), ("2", null)));

            string branch = "main";
            Assert.False(Program.IntegrateRemote(ref branch, out _, out _));
            Program.PerformCleanup();

            Assert.True(File.Exists(Path.Combine(a, ".git", "MERGE_HEAD")));
            Assert.Contains("# App from B", File.ReadAllText(readme)); // the user's half-done resolution is still there
        }

        [Fact]
        public void IntegrateRemote_KeepsALocalEnvThatTheRemoteTurnedIntoAnExample()
        {
            // The usual way to turn .env into a template is a rename - which hid the deletion from the "keep it" check
            var (remote, a, b) = TwoComputers(".env", "DB_USER=app\nDB_PASSWORD=Sup3rS3cretPw!\nDB_HOST=localhost\nDB_PORT=5432\nMODE=dev\nDEBUG=1\nNAME=app\n");
            Git(a, "mv", ".env", ".env.example");
            File.WriteAllText(Path.Combine(a, ".env.example"), "DB_USER=app\nDB_PASSWORD=\nDB_HOST=localhost\nDB_PORT=5432\nMODE=dev\nDEBUG=1\nNAME=app\n");
            File.WriteAllText(Path.Combine(a, ".gitignore"), ".env\n");
            Git(a, "add", "-A");
            Git(a, "commit", "-q", "-m", "Turn .env into a template");
            Git(a, "push", "-q", "origin", "main");
            Directory.SetCurrentDirectory(b);

            string branch = "main";
            Assert.True(Program.IntegrateRemote(ref branch, out _, out _));

            Assert.True(File.Exists(Path.Combine(b, ".env.example")));
            Assert.Contains("Sup3rS3cretPw!", File.ReadAllText(Path.Combine(b, ".env")));
        }

        [Fact]
        public void IntegrateRemote_IsNotBlockedByCaseTwinsTheProjectAlreadyHas()
        {
            // A repository made on Linux can hold README.md and readme.md; the merge doesn't add them, so it mustn't stop
            string remote = NewBare("remote");
            string seed = NewRepo("seed");
            string upper = GitOutWithInput(seed, "# Upper\n", "hash-object", "-w", "--stdin");
            string lower = GitOutWithInput(seed, "# lower\n", "hash-object", "-w", "--stdin");
            Git(seed, "update-index", "--add", "--cacheinfo", $"100644,{upper},README.md");
            Git(seed, "update-index", "--add", "--cacheinfo", $"100644,{lower},readme.md");
            Commit(seed, "app.txt", "app\n", "Made on Linux");
            Git(seed, "push", "-q", remote, "HEAD:refs/heads/main");
            string local = Path.Combine(root, "windows");
            Git(root, "clone", "-q", remote, local);
            Commit(seed, "app.txt", "app v2\n", "Change the app only");
            Git(seed, "push", "-q", remote, "HEAD:refs/heads/main");
            Directory.SetCurrentDirectory(local);

            string branch = "main";
            Assert.True(Program.IntegrateRemote(ref branch, out int pulled, out _));

            Assert.Equal(1, pulled);
            Assert.Equal("app v2\n", File.ReadAllText(Path.Combine(local, "app.txt")).Replace("\r\n", "\n"));
        }

        [Fact]
        public void PublishedBase_IsWhereTheBranchLeftAnotherPublishedBranch()
        {
            // A feature branch made from origin/develop: develop's published commits must stay out of a rebuild
            string remote = NewRemoteWithReadme("remote", "# M0\n");
            string seed = Path.Combine(root, "remote-seed");
            Git(seed, "checkout", "-q", "-b", "develop");
            Commit(seed, "lib.txt", "lib\n", "D1");
            Commit(seed, "lib2.txt", "lib2\n", "D2");
            string d2 = GitOut(seed, "rev-parse", "HEAD");
            Git(seed, "push", "-q", remote, "HEAD:refs/heads/develop");
            string local = Path.Combine(root, "local");
            Git(root, "clone", "-q", remote, local);
            Git(local, "checkout", "-q", "-b", "feature", "origin/develop");
            Commit(local, "feature.txt", "f\n", "F1");
            Directory.SetCurrentDirectory(local);

            Assert.True(Program.TryGetPublishedBase(out string? baseCommit));
            Assert.Equal(d2, baseCommit);
        }

        [Fact]
        public void PublishedBase_NeedsNoRemoteHead()
        {
            // "git init --bare" points HEAD at a "master" that doesn't exist while the team pushes "main"
            string remote = NewBare("shared", "master");
            string seed = NewRepo("seed");
            Commit(seed, "a.txt", "a\n", "M0");
            Commit(seed, "b.txt", "b\n", "M1");
            string m1 = GitOut(seed, "rev-parse", "HEAD");
            Git(seed, "push", "-q", remote, "HEAD:refs/heads/main");
            string local = Path.Combine(root, "local");
            Git(root, "clone", "-q", remote, local);
            Git(local, "checkout", "-q", "-B", "main", "origin/main");
            Git(local, "checkout", "-q", "-b", "video");
            Commit(local, "c.txt", "c\n", "F1");
            Directory.SetCurrentDirectory(local);

            Assert.True(Program.TryGetPublishedBase(out string? baseCommit));
            Assert.Equal(m1, baseCommit); // not null: that would turn the branch into a parentless root commit
        }

        [Fact]
        public void Squash_SitsOnTheRemoteTipItMerged_NotOnOneThatMovedSince()
        {
            // A teammate's push that arrives (and is fetched) while GitSync waits at a menu must never be undone
            var (remote, a, b) = TwoComputers("a.txt", "a\n");
            Commit(a, ".env", "TOKEN=abcdef123456\n", "Add env");
            Commit(b, "t1.txt", "t1\n", "T1");
            Git(b, "push", "-q", "origin", "main");
            string t1 = GitOut(b, "rev-parse", "HEAD");
            Git(a, "fetch", "-q", "origin");
            Git(a, "merge", "-q", "--no-edit", "origin/main"); // GitSync's safety merge
            Commit(b, "t2.txt", "t2\n", "T2");
            Git(b, "push", "-q", "origin", "main");
            string t2 = GitOut(b, "rev-parse", "HEAD");
            Git(a, "fetch", "-q", "origin"); // e.g. VS Code's auto-fetch, or git-lfs migrate
            Git(a, "rm", "-q", "--cached", ".env");
            File.WriteAllText(Path.Combine(a, ".gitignore"), "/.env\n"); // what "Leave them out" does
            Git(a, "add", ".gitignore");
            Directory.SetCurrentDirectory(a);

            Assert.True(Program.SquashUnpushedCommits());

            Assert.Equal(t1, GitOut(a, "rev-parse", "HEAD^"));
            Assert.False(GitSucceeds(a, "merge-base", "--is-ancestor", t2, "HEAD")); // so the push is rejected, not a fast-forward
            Assert.DoesNotContain(".env", GitOut(a, "ls-tree", "--name-only", "HEAD").Split('\n'));
            Assert.True(File.Exists(Path.Combine(a, ".env")));
        }

        [Fact]
        public void Squash_WithNothingLeftPutsTheBranchBackOnTheRemote()
        {
            var (remote, a, _) = TwoComputers("a.txt", "a\n");
            string published = GitOut(a, "rev-parse", "HEAD");
            Commit(a, ".env", "TOKEN=abcdef123456\n", "Add env");
            Git(a, "rm", "-q", "--cached", ".env");
            Directory.SetCurrentDirectory(a);

            Assert.True(Program.SquashUnpushedCommits());

            Assert.Equal(published, GitOut(a, "rev-parse", "HEAD"));
            Assert.True(File.Exists(Path.Combine(a, ".env")));
        }

        [Fact]
        public void SaveRemote_ForgetsBranchesOfTheRepositoryItReplaces()
        {
            // Branches fetched from a wrongly pasted URL mustn't count as "already on the remote" for the right one
            string wrong = NewRemoteWithReadme("other-project", "# someone else\n");
            string right = NewBare("right");
            string local = NewRepo("local");
            Commit(local, "main.py", "print(1)\n", "L1");
            Git(local, "remote", "add", "origin", wrong);
            Git(local, "fetch", "-q", "origin");
            Directory.SetCurrentDirectory(local);

            Assert.True(Program.TryGetPublishedBase(out string? baseCommit));
            Assert.Null(baseCommit); // nothing of this project is on that remote
            Program.SaveRemote(right);

            Assert.Equal("", GitOut(local, "for-each-ref", "refs/remotes/origin/"));
        }

        [Fact]
        public void UnpushedSecrets_AreFoundInCommitsMadeOutsideGitSync()
        {
            string local = NewRepo("local");
            File.WriteAllText(Path.Combine(local, ".env"), "API_TOKEN=abc123realtoken\n");
            File.WriteAllText(Path.Combine(local, "config.js"), "const password = \"hunter2hunter2\";\n");
            File.WriteAllText(Path.Combine(local, "app.txt"), "app\n");
            Git(local, "add", "-A");
            Git(local, "commit", "-q", "-m", "Committed in VS Code");
            Git(local, "rm", "-q", "config.js");
            Git(local, "commit", "-q", "-m", "Remove the key again");
            Directory.SetCurrentDirectory(local);

            var files = Program.FindSecretsInUnpushedCommits().Select(f => f.File).ToList();

            Assert.Contains(".env", files);
            Assert.Contains("config.js", files); // deleted since, but the earlier commit still holds it
            Assert.DoesNotContain("app.txt", files);
        }

        [Fact]
        public void UnpushedSecrets_IgnoreWhatTheRemoteAlreadyHas()
        {
            // Stopping to track an already published .env must not be reported as "a secret in unpushed commits"
            var (remote, a, _) = TwoComputers(".env", "TOKEN=abcdef123456\n");
            Commit(a, "feature.txt", "x\n", "Add feature X");
            Git(a, "rm", "-q", "--cached", ".env");
            Commit(a, ".gitignore", "/.env\n", "Stop tracking env");
            Directory.SetCurrentDirectory(a);

            Assert.Empty(Program.FindSecretsInUnpushedCommits());
        }

        [Fact]
        public void CountCommitsToPush_ComparesWithTheTargetBranch()
        {
            // main fast-forwarded to an already pushed feature branch: every commit is on *a* remote branch, not on main
            var (remote, a, _) = TwoComputers("a.txt", "a\n");
            Git(a, "checkout", "-q", "-b", "feature");
            Commit(a, "f.txt", "f\n", "F1");
            Git(a, "push", "-q", "-u", "origin", "feature");
            Git(a, "checkout", "-q", "main");
            Git(a, "merge", "-q", "--ff-only", "feature");
            Directory.SetCurrentDirectory(a);

            Assert.Equal(1, Program.CountCommitsToPush("main", remoteBranchExists: true));
        }

        [Fact]
        public void CountCommitsToPush_IgnoresATrackingRefLeftFromBefore()
        {
            // The repository was deleted and created again (empty) on GitHub: origin/main is a leftover
            var (remote, a, _) = TwoComputers("a.txt", "a\n");
            foreach (var file in Directory.EnumerateFiles(remote, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(remote, true);
            Git(root, "init", "-q", "--bare", remote);
            Directory.SetCurrentDirectory(a);

            Assert.Equal(1, Program.CountCommitsToPush("main", remoteBranchExists: false));
        }

        [Fact]
        public void GetWorkingTreeChanges_ReadsARenameInTheWorkingTree()
        {
            string local = NewRepo("local");
            Commit(local, "original-name.txt", "same content\n", "Add file");
            File.Move(Path.Combine(local, "original-name.txt"), Path.Combine(local, "renamed-in-worktree.txt"));
            Git(local, "add", "-N", "renamed-in-worktree.txt");
            Directory.SetCurrentDirectory(local);

            var changes = Program.GetWorkingTreeChanges();

            Assert.Single(changes);
            Assert.EndsWith("renamed-in-worktree.txt", changes[0]);
        }

        [Fact]
        public void MatchExistingBranchSpelling_UsesTheBranchThatDiffersOnlyInCase()
        {
            string local = NewRepo("local");
            Commit(local, "a.txt", "a\n", "A");
            Directory.SetCurrentDirectory(local);

            Assert.Equal("main", Program.MatchExistingBranchSpelling("Main"));
            Assert.Equal("feature", Program.MatchExistingBranchSpelling("feature"));
        }

        [Fact]
        public void FindConflictBlocksInStagedFiles_IgnoresHeadingUnderlines()
        {
            string local = NewRepo("local");
            File.WriteAllText(Path.Combine(local, "README.md"), "License\n=======\n\nMIT\n");
            File.WriteAllText(Path.Combine(local, "notes.txt"), "x\n<<<<<<< HEAD\nmine\n=======\ntheirs\n>>>>>>> origin/main\n");
            File.WriteAllText(Path.Combine(local, "quote.md"), "Git writes lines like this:\n<<<<<<< HEAD\n");
            Git(local, "add", "-A");
            Directory.SetCurrentDirectory(local);

            Assert.Equal(new[] { "notes.txt" }, Program.FindConflictBlocksInStagedFiles());
        }

        [Fact]
        public void RemoveOwnAdditions_KeepsWhatTheUserAddedInBetween()
        {
            // GitSync appends, the user adds a line while GitSync waits at a question, GitSync appends again, then cancel
            string file = Path.Combine(root, ".gitignore");
            File.WriteAllText(file, "node_modules/\n");
            Program.AppendToFile(file, "# Python\n__pycache__/\n");
            File.AppendAllText(file, "my-private-notes.txt\n");
            Program.AppendToFile(file, "/.env\n");

            Assert.Equal((2, 0), Program.RemoveOwnAdditions(file));
            Assert.Equal("node_modules/\nmy-private-notes.txt\n", File.ReadAllText(file));
        }

        [Fact]
        public void RemoveOwnAdditions_NeverCutsIntoTheUsersOwnLines()
        {
            // GitSync's "/.env" must not be cut off the end of the user's "config/.env" (that would leave "config")
            string file = Path.Combine(root, ".gitignore");
            File.WriteAllText(file, "*.log\n");
            Program.AppendToFile(file, "/.env\n");
            File.AppendAllText(file, "config/.env\n");

            Program.RemoveOwnAdditions(file);

            Assert.Equal("*.log\nconfig/.env\n", File.ReadAllText(file));
        }

        [Fact]
        public void RemoveOwnAdditions_LeavesBytesOfOtherEncodingsAlone()
        {
            // A .gitignore saved as Windows-1252 ("ø" = 0xF8) must come back byte for byte, not re-encoded as UTF-8
            string file = Path.Combine(root, ".gitignore");
            byte[] original = { (byte)'#', (byte)' ', (byte)'K', 0xF8, (byte)'b', (byte)'\n' };
            File.WriteAllBytes(file, original);
            Program.AppendToFile(file, "/.env\n");
            File.AppendAllText(file, "mine.txt\n");

            Program.RemoveOwnAdditions(file);

            Assert.Equal(original.Concat("mine.txt\n"u8.ToArray()).ToArray(), File.ReadAllBytes(file));
        }

        [Fact]
        public void UnpushedSecrets_ReportEveryFlaggedVersionOfAFile()
        {
            // Accepting today's version of config.js must not hide an older version with a real key in it
            string local = NewRepo("local");
            Commit(local, "config.js", "const token = \"ghp_abcdefghijklmnopqrstuvwxyzABCDEFGHIJ\";\n", "Real token");
            Commit(local, "config.js", "const password = \"devpass123\";\n", "Dev password");
            Directory.SetCurrentDirectory(local);

            var hits = Program.FindSecretsInUnpushedCommits().Where(h => h.File == "config.js").ToList();

            Assert.Equal(2, hits.Select(h => h.Blob).Distinct().Count());
        }

        [Fact]
        public void HandleSecrets_KeepsAPublishedFileWhoseCurrentVersionIsClean()
        {
            // A key committed and removed again: the rebuild drops the old version, but deploy.sh stays in the project
            var (remote, a, _) = TwoComputers("deploy.sh", "#!/bin/sh\naws s3 sync ./site s3://bucket\n");
            Commit(a, "deploy.sh", "#!/bin/sh\nexport AWS_ACCESS_KEY_ID=AKIAIOSFODNN7EXAMPLE\n", "Deploy with key");
            Commit(a, "deploy.sh", "#!/bin/sh\nexport AWS_ACCESS_KEY_ID=$AWS_KEY_FROM_VAULT\n", "Read key from vault");
            Program.OpenSessionFolder(a);
            Console.SetIn(new StringReader("1\n"));

            Assert.True(Program.HandleSecretsInUnpushedCommits());

            Assert.Contains("deploy.sh", GitOut(a, "ls-tree", "--name-only", "HEAD").Split('\n'));
            Assert.DoesNotContain("AKIA", GitOut(a, "log", "-p", "origin/main..HEAD"));
            Assert.False(File.Exists(Path.Combine(a, ".gitignore")));
        }

        [Fact]
        public void IntegrateRemote_LeaveItKeepsAnEnvTheRemoteStoppedTracking()
        {
            // The merge deletes the tracked .env; "Leave it" must not leave this computer without its copy
            var (remote, a, b) = TwoComputers(".env", "DB_PASSWORD=Sup3rS3cretPw!\n");
            Commit(a, "README.md", "# App\n", "Readme");
            Git(a, "push", "-q", "origin", "main");
            Git(b, "pull", "-q", "--no-rebase", "origin", "main");
            Git(a, "rm", "-q", "--cached", ".env");
            File.WriteAllText(Path.Combine(a, ".gitignore"), ".env\n");
            File.WriteAllText(Path.Combine(a, "README.md"), "# App from A\n");
            Git(a, "add", "-A");
            Git(a, "commit", "-q", "-m", "Stop tracking env");
            Git(a, "push", "-q", "origin", "main");
            Commit(b, "README.md", "Install\nrun it\n", "B edit");
            Program.OpenSessionFolder(b);
            string readme = Path.Combine(b, "README.md");
            Console.SetIn(new ScriptedInput(("4", null), ("", () => File.WriteAllText(readme, "Install\nrun it\n=======\n# App from A\n")), ("2", null)));

            string branch = "main";
            Assert.False(Program.IntegrateRemote(ref branch, out _, out _));
            Program.PerformCleanup();

            Assert.True(File.Exists(Path.Combine(b, ".git", "MERGE_HEAD")));
            Assert.Contains("Sup3rS3cretPw!", File.ReadAllText(Path.Combine(b, ".env")));
            // ...and a second merge is never started on top of the one the user kept
            Assert.False(Program.IntegrateRemote(ref branch, out _, out _));
            Assert.True(File.Exists(Path.Combine(b, ".git", "MERGE_HEAD")));
        }

        [Fact]
        public void IntegrateRemote_KeepMineOfAReadmeWithAHeadingUnderlineAsksNothingMore()
        {
            // The file is exactly my committed version - a "=======" line in it can't be a leftover marker
            var (remote, a, b) = TwoComputers("README.md", "# App\n");
            Commit(b, "README.md", "# App from B\n", "B edit");
            Git(b, "push", "-q", "origin", "main");
            Commit(a, "README.md", "# App\n\nLicense\n=======\nMIT\n", "A edit");
            Directory.SetCurrentDirectory(a);
            Console.SetIn(new StringReader("1\n")); // keep MY version - a second question would hit the end of input

            string branch = "main";
            Assert.True(Program.IntegrateRemote(ref branch, out _, out _));
            Assert.False(File.Exists(Path.Combine(a, ".git", "MERGE_HEAD")));
        }

        [Fact]
        public void ReconcileBranch_RenamesOnlyTheMasterBranchItselfAndForgetsTheOldTarget()
        {
            // Local master pushed as main earlier; the stale "master" record must not bring the question back
            string remote = NewRemoteWithReadme("remote", "# hi\n");
            string local = Path.Combine(root, "local");
            Git(root, "clone", "-q", remote, local);
            Git(local, "branch", "-m", "main", "master");
            Git(local, "config", "branch.master.gitsynctarget", "master");
            Directory.SetCurrentDirectory(local);
            Console.SetIn(new StringReader("1\n"));

            Assert.Equal("main", Program.ReconcileBranchWithRemote("master"));

            Assert.Equal("main", Program.GetCurrentBranch());
            Assert.False(GitSucceeds(local, "config", "--get", "branch.main.gitsynctarget"));
        }

        [Fact]
        public void ReconcileBranch_KeepsAWorkBranchNameAndRecordsWhereItGoes()
        {
            // 'laptop' syncs with a 'master' the remote renamed to 'main': laptop keeps its name, it just goes to main now
            string remote = NewRemoteWithReadme("remote", "# hi\n");
            string local = Path.Combine(root, "local");
            Git(root, "clone", "-q", remote, local);
            Git(local, "checkout", "-q", "-b", "laptop");
            Git(local, "config", "branch.laptop.gitsynctarget", "master");
            Directory.SetCurrentDirectory(local);
            Console.SetIn(new StringReader("1\n"));

            Assert.Equal("main", Program.ReconcileBranchWithRemote("master"));

            Assert.Equal("laptop", Program.GetCurrentBranch());
            Assert.Equal("main", GitOut(local, "config", "--get", "branch.laptop.gitsynctarget"));
            Assert.True(GitSucceeds(local, "rev-parse", "--verify", "--quiet", "refs/heads/main"));
        }

        // Answers for GitSync's prompts; each can first change something, the way the user does while GitSync waits
        private sealed class ScriptedInput : TextReader
        {
            private readonly Queue<(string Line, Action? Before)> answers;

            public ScriptedInput(params (string Line, Action? Before)[] answers) => this.answers = new Queue<(string Line, Action? Before)>(answers);

            public override string? ReadLine()
            {
                if (answers.Count == 0) return null;
                var (line, before) = answers.Dequeue();
                before?.Invoke();
                return line;
            }
        }

        // A bare remote with notes on main, cloned to two computers that both track origin/main
        private (string Remote, string A, string B) TwoComputers(string file, string content)
        {
            string remote = NewBare("remote");
            string a = Path.Combine(root, "pc-a");
            Git(root, "clone", "-q", remote, a);
            Git(a, "symbolic-ref", "HEAD", "refs/heads/main");
            Commit(a, file, content, "Initial commit");
            Git(a, "push", "-q", "-u", "origin", "main");
            string b = Path.Combine(root, "pc-b");
            Git(root, "clone", "-q", remote, b);
            return (remote, a, b);
        }

        private string NewRepo(string name)
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            Git(dir, "init", "-q");
            Git(dir, "symbolic-ref", "HEAD", "refs/heads/main");
            return dir;
        }

        private string NewBare(string name, string headBranch = "main")
        {
            string remote = Path.Combine(root, name + ".git");
            Git(root, "init", "-q", "--bare", remote);
            Git(remote, "symbolic-ref", "HEAD", "refs/heads/" + headBranch);
            return remote;
        }

        // A remote the way GitHub creates it when "Add a README file" is ticked
        private string NewRemoteWithReadme(string name, string readme)
        {
            string remote = NewBare(name);
            string seed = NewRepo(name + "-seed");
            Commit(seed, "README.md", readme, "Initial commit");
            Git(seed, "push", "-q", remote, "HEAD:refs/heads/main");
            return remote;
        }

        private static void Commit(string dir, string file, string content, string message)
        {
            File.WriteAllText(Path.Combine(dir, file), content);
            Git(dir, "add", "--", file);
            Git(dir, "commit", "-q", "-m", message);
        }

        private static void Git(string dir, params string[] args) => RunGitIn(dir, null, args, throwOnError: true);

        private static string GitOut(string dir, params string[] args) => RunGitIn(dir, null, args, throwOnError: true).Output.Trim();

        private static string GitOutWithInput(string dir, string input, params string[] args) => RunGitIn(dir, input, args, throwOnError: true).Output.Trim();

        private static bool GitSucceeds(string dir, params string[] args) => RunGitIn(dir, null, args, throwOnError: false).ExitCode == 0;

        private static (int ExitCode, string Output) RunGitIn(string dir, string? input, string[] args, bool throwOnError)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("core.autocrlf=false"); // test files keep exactly the line endings written
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using (var p = Process.Start(psi)!)
            {
                if (input != null) p.StandardInput.Write(input);
                p.StandardInput.Close();
                var output = p.StandardOutput.ReadToEndAsync();
                string error = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (throwOnError && p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(" ", args)} failed: {error}");
                return (p.ExitCode, output.Result);
            }
        }
    }
}
