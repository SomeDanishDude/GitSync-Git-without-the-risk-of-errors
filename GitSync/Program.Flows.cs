using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GitSync
{
    public partial class Program
    {
        // =================================================================================
        // [1] Sync Local Folder
        // =================================================================================

        static void HandleLocalSync()
        {
            // 1. Select Folder
            string? selectedFolder = PickFolder("Select a folder to turn into a Git repository (or a repository to sync)");
            if (selectedFolder == null)
            {
                Console.WriteLine("No folder selected.");
                return;
            }

            OpenSessionFolder(selectedFolder);
            WriteLineColor(ConsoleColor.Cyan, $"\nSelected Folder: {selectedFolder}");
            if (!ConfirmBroadFolder(selectedFolder)) return;
            if (!EnsureFolderTrusted(selectedFolder)) return;
            if (RunGit(true, "rev-parse", "--is-bare-repository").Output.Trim() == "true")
            {
                WriteLineColor(ConsoleColor.Red, "\nThat is a bare repository (the storage folder of a remote), not a project folder. Pick the folder with your files instead.");
                return;
            }

            // 2. Identity & Credentials
            CheckGitIdentity();
            CheckCredentialHelper();

            // 3. Git Init (new folder) or prepare the existing repository
            bool isNewRepo = !Directory.Exists(".git") && !File.Exists(".git");
            if (!isNewRepo && !RunGit(true, "rev-parse", "--git-dir").Success)
            {
                // e.g. left half-deleted by an interrupted run. Never delete it: it may still hold history worth rescuing.
                string aside = $".git-broken-{DateTime.Now:yyyyMMdd-HHmmss}";
                WriteLineColor(ConsoleColor.Yellow, "\nThis folder has a .git folder that Git can't read (an earlier attempt was probably interrupted).");
                if (!AskYesNo($"Move it aside (renamed to {aside}) and start a fresh repository?", true)) return;
                if (File.Exists(".git")) File.Move(".git", aside); // a worktree/submodule pointer file
                else Directory.Move(".git", aside);
                isNewRepo = true;
            }
            if (isNewRepo && IsInsideRepository())
            {
                string parentRepo = GetRepositoryRoot();
                WriteLineColor(ConsoleColor.Yellow, $"\nThis folder is inside another Git repository: {parentRepo}");
                Console.WriteLine("[1] Create a separate repository just for this folder");
                Console.WriteLine("[2] Sync that whole repository instead");
                Console.WriteLine("[3] Cancel");
                string where = AskChoice("Choice: ", "1", "2", "3");
                if (where == "3") return;
                if (where == "2")
                {
                    if (!ConfirmBroadFolder(parentRepo)) return;
                    OpenSessionFolder(parentRepo);
                    isNewRepo = false;
                }
            }

            string branchName;
            if (isNewRepo)
            {
                branchName = AskBranchName("\nBranch name for the new repository (Enter = main): ", "main");
                if (!InitRepository(branchName)) return;
            }
            else
            {
                LogVerbose("Git repository already exists.");
                if (!PrepareExistingRepository(out branchName, allowBranchChange: true)) return;
                BackupIndex();
            }
            UseLongPaths();
            ExcludeLocalOnlyFiles();
            bool hadCommits = HasCommits();

            // 4. Remote URL - checked first, so login or URL problems show up before anything is committed, and so the
            //    next steps know what the remote already has (its main branch, its README/LICENSE)
            string remoteUrl = SetupRemote();
            string remoteBranch = ReconcileBranchWithRemote(ResolveRemoteBranch(branchName));
            var remoteFiles = GetRemoteTopLevelFiles(remoteBranch);

            // 5. Project Setup (README/LICENSE only while nothing is committed yet, and not when the remote has its own)
            detectedProjectTypes = SuggestGitIgnore(sessionFolder!);
            if (!hadCommits)
            {
                if (remoteFiles.Any(IsLicenseFileName)) LogVerbose("The remote already has a LICENSE - keeping that one.");
                else SuggestLicense(sessionFolder!);
                if (remoteFiles.Any(IsReadmeFileName)) LogVerbose("The remote already has a README - keeping that one.");
                else SuggestReadme(sessionFolder!, detectedProjectTypes);
            }

            // 6. Nested repositories, Large Files & Secrets (only files that will actually be committed; in a project that
            //    is on the remote already, only what changed - commits that aren't pushed yet are checked before the push)
            bool published = hadCommits && HasPublishedHistory();
            CheckEmbeddedRepositories();
            CheckLargeFiles(sessionFolder!);
            ScanSecrets(sessionFolder!, onlyChangedFiles: published);

            // 7. Quality Gate
            if (!CheckQualityGates(sessionFolder!, onlyChangedFiles: published)) return;

            // 8. Stage everything
            if (!StageAll()) return;
            var changes = GetStagedChanges();
            if (changes.Count == 0 && !hadCommits)
            {
                WriteLineColor(ConsoleColor.Yellow, "\nThere is nothing to commit: the folder is empty or everything in it is ignored.");
                return;
            }
            if (changes.Count == 0)
            {
                SyncWithoutNewChanges(remoteBranch);
                return;
            }

            // 9. Commit Message
            string suggested = hadCommits ? SuggestCommitMessage(changes) : "Initial commit";
            string commitMsg = GetUserInput($"\nEnter Commit Message (Enter = \"{suggested}\"): ", freeText: true);
            if (string.IsNullOrEmpty(commitMsg)) commitMsg = suggested;

            // 10. Summary
            PrintSummary(remoteUrl, remoteBranch, commitMsg, changes);
            if (!AskYesNo("Ready to Push?"))
            {
                Log("Aborted", remoteUrl, remoteBranch);
                WriteLineColor(ConsoleColor.Yellow, "Cancelled - nothing was committed or uploaded.");
                return; // cleanup puts the folder back the way it was
            }

            // 11. Commit
            if (!Commit(commitMsg)) return;

            // 12. Safety Pull - after committing, so incoming files can never overwrite uncommitted work
            if (!IntegrateRemote(ref remoteBranch, out _, out _))
            {
                WriteLineColor(ConsoleColor.Yellow, "\nNothing was pushed. Your commit is saved locally - run Sync again when you're ready.");
                Log("Pull failed", remoteUrl, remoteBranch);
                return;
            }

            // 13. Push
            PerformPush(remoteBranch);
        }

        // Nothing new to commit: bring in what's new on the remote, then upload whatever is still waiting
        static void SyncWithoutNewChanges(string branch)
        {
            WriteLineColor(ConsoleColor.Cyan, "\nNo new changes to commit.");
            if (!IntegrateRemote(ref branch, out int pulled, out bool remoteBranchExists)) return;
            // Nothing below is undone by a cancel: a branch the user switched to (or created) stays
            keepChanges = true;

            if (!remoteBranchExists)
            {
                // A new branch, or a remote that was emptied/recreated: pushing (re)creates it there
                WriteLineColor(ConsoleColor.Yellow, $"The branch '{branch}' isn't on the remote yet.");
                if (AskYesNo("Publish it now?", true)) PerformPush(branch);
                return;
            }

            int waiting = CountCommitsToPush(branch, remoteBranchExists: true);
            if (waiting == 0)
            {
                WriteLineColor(ConsoleColor.Green, pulled > 0
                    ? $"Pulled {pulled} new commit(s) - everything is up to date now."
                    : "Everything is already up to date - nothing to push.");
                return;
            }

            WriteLineColor(ConsoleColor.Yellow, $"{waiting} commit(s) are waiting to be uploaded.");
            if (AskYesNo("Push them now?", true)) PerformPush(branch);
        }

        static bool InitRepository(string branchName)
        {
            LogVerbose("Initializing new Git repository...");

            // "init -b" starts on the chosen branch in one step, and also works in folders Git doesn't trust yet.
            // (The old code renamed "master" afterwards, which silently failed in a repository with no commits,
            // so pushing "main" failed with "src refspec main does not match any".)
            var init = RunGit(true, "init", "-b", branchName);
            if (!init.Success && init.AllText.Contains("unknown switch"))
            {
                init = RunGit(true, "init"); // Git older than 2.28
                if (init.Success) RunGit(true, "symbolic-ref", "HEAD", "refs/heads/" + branchName);
            }
            if (!init.Success)
            {
                ExplainGitFailure(init, "Creating the repository");
                return false;
            }

            gitInitialized = true;
            return EnsureFolderTrusted(sessionFolder!);
        }

        // Paths longer than 260 characters (deep node_modules, Asset Store packages) are skipped by Git for Windows otherwise
        static void UseLongPaths()
        {
            if (RunGit(true, "config", "core.longpaths").Output.Trim() != "true") RunGit(true, "config", "core.longpaths", "true");
        }

        static bool StageAll()
        {
            while (true)
            {
                LogVerbose("Staging all files (this may take a while)...");
                var add = RunGitWithSpinner("add", "-A");
                if (add.Success) return true;
                ExplainGitFailure(add, "Staging the files");
                if (!AskYesNo("Try again?", true)) return false;
            }
        }

        static bool ConfirmBroadFolder(string folder)
        {
            if (!IsTooBroadFolder(folder)) return true;
            WriteLineColor(ConsoleColor.Yellow, "\nThis is a whole drive or one of your main folders (user folder, Desktop, Documents, Downloads, OneDrive...).");
            Console.WriteLine("Everything in it would be uploaded. Usually you want to pick the project's own folder instead.");
            return AskYesNo("Continue with this folder anyway?", false);
        }

        static bool IsTooBroadFolder(string folder)
        {
            string full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var broad = new[]
            {
                profile,
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                Path.Combine(profile, "Downloads"),
                Environment.GetEnvironmentVariable("OneDrive") ?? "",
                Environment.GetEnvironmentVariable("OneDriveConsumer") ?? "",
                Environment.GetEnvironmentVariable("OneDriveCommercial") ?? "",
            };
            return Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar) == full
                || broad.Any(b => b.Length > 0 && string.Equals(full, b.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
        }

        // Things that must never be uploaded with the project (.git/info/exclude is local-only): a GitSync.exe copied into
        // it, and old repository folders GitSync moved aside (.git-broken-..., a kept LFS backup, a half-deleted .git)
        static void ExcludeLocalOnlyFiles()
        {
            try
            {
                if (sessionFolder == null) return;
                var entries = new List<string> { "/.git-broken-*/", "/.git-gitsync-backup-*/", "/.git.gitsync-delete-*/" };
                string? exe = Environment.ProcessPath;
                string rel = exe != null ? Path.GetRelativePath(sessionFolder, exe) : "..";
                bool exeInside = !rel.StartsWith("..") && !Path.IsPathRooted(rel);
                if (exeInside) entries.Add(ToGitIgnoreEntry(rel));

                string exclude = GetGitPath("info/exclude");
                var existing = File.Exists(exclude) ? File.ReadAllLines(exclude).Select(l => l.Trim()).ToHashSet() : new HashSet<string>();
                var missing = entries.Where(e => !existing.Contains(e)).ToList();
                if (missing.Count == 0) return;
                Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
                File.AppendAllText(exclude, Environment.NewLine + string.Join(Environment.NewLine, missing) + Environment.NewLine);
                if (exeInside && missing.Contains(ToGitIgnoreEntry(rel))) LogVerbose($"{rel} is GitSync itself, so it won't be uploaded.");
            }
            catch { }
        }

        // Full path of a file inside the repository's git folder (info/exclude, info/attributes) - also right for worktrees
        static string GetGitPath(string name)
        {
            var result = RunGit(true, "rev-parse", "--git-path", name);
            return Path.GetFullPath(result.Success ? result.Output.Trim() : Path.Combine(".git", name));
        }

        static string AskBranchName(string prompt, string defaultName)
        {
            while (true)
            {
                string name = GetUserInput(prompt);
                // "y"/"yes" at a "Press Enter to use it" question means "use it", not "a branch called yes"
                if (name == "" || IsYesWord(name)) return defaultName;
                if (IsValidBranchName(name)) return name;
                Console.WriteLine("That isn't a valid branch name (use letters, numbers, '-', '_' or '/', and no spaces).");
            }
        }

        // Gets an existing repository into a state we can work with: no half-finished rebase/merge, and on a real branch
        static bool PrepareExistingRepository(out string branch, bool allowBranchChange)
        {
            branch = "";
            if (IsRebaseInProgress())
            {
                WriteLineColor(ConsoleColor.Yellow, "\nThis repository is in the middle of a rebase (an earlier 'pull --rebase' or 'rebase' stopped half-way).");
                var dirty = GetWorkingTreeChanges();
                if (dirty.Count > 0)
                {
                    // Cancelling would throw these away, so leave the decision to a tool that shows the whole picture
                    Console.WriteLine("Cancelling it now would throw away these unsaved changes:");
                    foreach (var change in dirty.Take(10)) Console.WriteLine($"    {DescribeStatusLine(change)}");
                    Console.WriteLine("Finish or cancel the rebase in your Git tool (for example VS Code's Source Control view), then run GitSync again.");
                    return false;
                }

                Console.WriteLine("[1] Leave it - I'll finish it in my Git tool");
                Console.WriteLine("[2] Cancel the rebase (a backup branch of where you are now is made first)");
                if (AskChoice("Choice: ", "1", "2") == "1") return false;

                string rescue = $"gitsync-rescue-{DateTime.Now:yyyyMMdd-HHmmss}";
                RunGit(true, "branch", rescue, "HEAD");
                var abortRebase = RunGit(true, "rebase", "--abort");
                if (!abortRebase.Success)
                {
                    ExplainGitFailure(abortRebase, "Cancelling the rebase");
                    return false;
                }
                LogVerbose($"Rebase cancelled. Anything done during it is saved on the branch '{rescue}'.");
            }

            if (IsMergeInProgress())
            {
                WriteLineColor(ConsoleColor.Yellow, "\nA merge was left unfinished in this repository.");
                if (!ResolveConflictsAndCommit(startedHere: false)) return false;
            }
            else if (GetConflictedFiles().Count > 0)
            {
                // Left by a stash, cherry-pick or revert: "keep mine / keep remote" wouldn't mean anything here
                WriteLineColor(ConsoleColor.Yellow, "\nThese files still have unresolved conflicts (from an earlier stash, cherry-pick or revert):");
                foreach (var f in GetConflictedFiles().Take(15)) Console.WriteLine($"    {f}");
                Console.WriteLine("Open them, keep the text you want between the <<<<<<< and >>>>>>> lines, delete those lines, save, and run GitSync again.");
                return false;
            }

            branch = GetCurrentBranch();
            if (branch == "")
            {
                WriteLineColor(ConsoleColor.Yellow, "\nYou're not on any branch right now (\"detached HEAD\"), so there's nothing to push to.");
                while (true)
                {
                    string name = AskBranchName("Name a new branch to keep this work on (Enter = my-work): ", "my-work");
                    // Also "Main" next to "main": the same branch file on Windows, a second branch on GitHub
                    string? taken = BranchExists(name) ? name : FindBranchSpelledLike(name);
                    if (taken != null)
                    {
                        Console.WriteLine($"A branch called '{taken}' already exists - pick another name.");
                        continue;
                    }
                    var create = RunGit(true, "checkout", "-b", name);
                    if (!create.Success)
                    {
                        ExplainGitFailure(create, "Creating the branch");
                        return false;
                    }
                    branch = name;
                    break;
                }
            }

            string upstream = GetUpstream();
            WriteLineColor(ConsoleColor.Cyan, upstream != "" ? $"Current branch: {branch} (synced with {upstream})" : $"Current branch: {branch}");

            // Daily use: a branch that already has its place on the remote isn't asked about again
            if (!allowBranchChange || upstream != "") return true;

            string other = MatchExistingBranchSpelling(AskBranchName("Press Enter to use it, or type another branch name: ", branch));
            return other == branch || SwitchBranch(other, ref branch);
        }

        // Switches to (or creates) a branch. A branch that only exists on the remote is checked out from there - not
        // re-created from the current branch, which would push this branch's commits into it.
        static bool SwitchBranch(string target, ref string branch)
        {
            string original = branch;
            GitResult result;
            if (BranchExists(target))
            {
                // --no-overwrite-ignore: never let the other branch's files replace ignored local files such as .env
                result = RunGit(true, "checkout", "--no-overwrite-ignore", target);
            }
            else if (HasRemote("origin") && CheckRemoteBranch(target, out _) == RemoteBranchState.Exists && FetchBranch(target).Success)
            {
                result = RunGit(true, "checkout", "--no-overwrite-ignore", "-b", target, "--track", $"origin/{target}");
            }
            else
            {
                if (!AskYesNo($"Create a new branch '{target}' starting from '{original}'?", false)) return true;
                result = RunGit(true, "checkout", "-b", target);
                if (result.Success) branchCreatedThisSession = target;
            }

            if (!result.Success)
            {
                ExplainGitFailure(result, $"Switching to '{target}'");
                return false;
            }
            branchToRestore ??= original;
            branch = target;
            LogVerbose($"Now on branch '{branch}'.");
            return true;
        }

        // Lets cleanup undo our staging if the user cancels (the files themselves are never touched)
        static void BackupIndex()
        {
            if (indexBackup != null) return;
            string indexPath = GetGitPath("index");
            indexBackup = (indexPath, File.Exists(indexPath) ? File.ReadAllBytes(indexPath) : null);
            headAtIndexBackup = CurrentHead();
        }

        static bool Commit(string message)
        {
            // Never commit files that still contain a <<<<<<< ... ======= ... >>>>>>> conflict block (a heading underlined
            // with "=======" isn't one - "git diff --check" would call that a marker too)
            var markers = FindConflictBlocksInStagedFiles();
            if (markers.Count > 0)
            {
                WriteLineColor(ConsoleColor.Red, "\nThese files still contain conflict markers (<<<<<<< ... >>>>>>>):");
                foreach (var file in markers.Take(10)) Console.WriteLine($"    {file}");
                Console.WriteLine("[1] Cancel - I'll open them and keep the text I want (delete the marker lines)");
                Console.WriteLine("[2] Commit anyway (those lines are supposed to be there)");
                if (AskChoice("Choice: ", "1", "2") == "1") return false;
            }

            LogVerbose("Committing changes...");
            var commit = RunGitLive("commit", "--quiet", "-m", message);
            if (!commit.Success)
            {
                ExplainGitFailure(commit, "Committing", outputAlreadyShown: true);
                return false;
            }

            // From here on the work lives in a commit: never throw the repository away automatically
            keepChanges = true;
            WriteLineColor(ConsoleColor.Green, $"Committed: {RunGit(true, "log", "-1", "--format=%h %s").Output.Trim()}");
            return true;
        }

        static string SuggestCommitMessage(List<string> changes)
        {
            if (changes.Count > 3) return $"Update {changes.Count} files";
            var names = changes.Select(c => c.Substring(9).Split(" -> ").Last()).Select(p => p.Contains('/') ? p.Substring(p.LastIndexOf('/') + 1) : p);
            return "Update " + string.Join(", ", names);
        }

        static void PrintSummary(string remote, string branch, string msg, List<string> changes)
        {
            string local = GetCurrentBranch();
            bool onRemote = RunGit(true, "show-ref", "--verify", "--quiet", $"refs/remotes/origin/{branch}").Success;
            WriteLineColor(ConsoleColor.Cyan, "\n==========================================");
            WriteLineColor(ConsoleColor.Cyan, "           FINAL SUMMARY");
            WriteLineColor(ConsoleColor.Cyan, "==========================================");
            Console.WriteLine($"Folder:         {sessionFolder}");
            Console.WriteLine($"Project Type:   {(detectedProjectTypes.Count > 0 ? string.Join(", ", detectedProjectTypes) : "Unknown")}");
            Console.WriteLine($"Remote URL:     {MaskUrl(remote)}");
            Console.WriteLine(local != "" && local != branch ? $"Target Branch:  {branch} (from your branch '{local}')" : $"Target Branch:  {branch}");
            Console.WriteLine($"Commit Message: {msg}");
            Console.WriteLine("------------------------------------------");
            Console.WriteLine($"Files Ignored:  {filesIgnoredCount}");
            Console.WriteLine($"LFS Tracked:    {lfsTrackedCount}");
            Console.WriteLine($"Secrets Check:  {secretsStatus}");
            Console.WriteLine($"Commits to Push: {(HasCommits() ? CountCommitsToPush(branch, onRemote) : 0) + 1}");
            Console.WriteLine($"Files to Push:  {changes.Count}");
            foreach (var change in changes.Take(15)) Console.WriteLine($"    {change}");
            if (changes.Count > 15)
            {
                Console.WriteLine($"    ... and {changes.Count - 15} more");
                // Per-folder counts make an accidental node_modules/ or bin/ stand out before it's uploaded
                Console.WriteLine("By folder:");
                var folders = changes.Select(c => c.Substring(9))
                    .GroupBy(p => p.Contains('/') ? p.Substring(0, p.IndexOf('/') + 1) : "(top level)")
                    .OrderByDescending(g => g.Count())
                    .Take(8);
                foreach (var folder in folders) Console.WriteLine($"    {folder.Count(),6}  {folder.Key}");
            }

            int incoming = CountIncomingCommits(branch);
            if (incoming > 0) WriteLineColor(ConsoleColor.Yellow, $"The remote has {incoming} commit(s) you don't have yet - they'll be merged in before pushing.");
            WriteLineColor(ConsoleColor.Cyan, "==========================================");
        }

        static int CountIncomingCommits(string branch)
        {
            string remoteRef = $"refs/remotes/origin/{branch}";
            if (!RunGit(true, "show-ref", "--verify", "--quiet", remoteRef).Success) return 0;
            if (HasCommits()) return GetAheadBehind(remoteRef).Behind;
            return int.TryParse(RunGit(true, "rev-list", "--count", remoteRef).Output.Trim(), out int count) ? count : 0;
        }

        // =================================================================================
        // Remote
        // =================================================================================

        // askForNewUrl: the user asked to change the URL (e.g. "Use a different remote URL"), so the current one isn't offered
        static string SetupRemote(bool askForNewUrl = false)
        {
            if (HasRemote("origin"))
            {
                string existing = GetRemoteUrl();
                WriteLineColor(ConsoleColor.Cyan, $"\nRemote 'origin': {MaskUrl(existing)}");

                if (askForNewUrl)
                {
                    Console.WriteLine("Paste the address of the right repository below.");
                }
                else if (GetUpstream() != "")
                {
                    // Daily use: a branch already synced with origin doesn't need the question
                    string? working = VerifyRemoteAccess(existing, quiet: true);
                    if (working != null) return SaveRemote(working);
                }
                else
                {
                    while (true)
                    {
                        string answer = GetUserInput("Press Enter to keep it, or paste a new URL to replace it: ").Trim('"');
                        if (answer == "" || IsYesWord(answer))
                        {
                            string? working = VerifyRemoteAccess(existing);
                            if (working != null) return SaveRemote(working);
                            break;
                        }

                        string? url = NormalizeRemoteUrl(answer);
                        if (url == null)
                        {
                            Console.WriteLine("That doesn't look like a Git URL. Press Enter to keep the current one.");
                            continue;
                        }
                        string? verified = VerifyRemoteAccess(url);
                        if (verified != null) return SaveRemote(verified);
                        break;
                    }
                }
            }

            while (true)
            {
                string answer = GetUserInput("\nEnter Git Remote URL (e.g. https://github.com/you/project.git), or press Enter for help: ").Trim('"');
                if (answer == "")
                {
                    ShowCreateRepositoryHelp();
                    continue;
                }

                string? url = NormalizeRemoteUrl(answer);
                if (url == null)
                {
                    Console.WriteLine("Invalid URL format. On GitHub, copy it from the green 'Code' button (HTTPS tab). Type cancel to stop.");
                    continue;
                }
                string? verified = VerifyRemoteAccess(url);
                if (verified != null) return SaveRemote(verified);
            }
        }

        // Only called with a URL that has just been reached, so a typo never replaces a working remote
        internal static string SaveRemote(string url)
        {
            if (!HasRemote("origin"))
            {
                LogVerbose("Adding origin remote...");
                RunGit(true, "remote", "add", "origin", url);
            }
            else if (GetRemoteUrl() != url)
            {
                LogVerbose("Replacing origin remote...");
                bool otherRepository = !SameRepository(GetRemoteUrl(), url);
                RunGit(true, "remote", "set-url", "origin", url);
                // Branches fetched from a different repository say nothing about this one - left in place, they would
                // count as "already on the remote" and end up under a rebuilt commit
                if (otherRepository) ForgetRemoteBranches();
            }
            return url;
        }

        static void ShowCreateRepositoryHelp()
        {
            WriteLineColor(ConsoleColor.Cyan, "\nNo repository on GitHub yet? Create one like this:");
            Console.WriteLine("  1. Open https://github.com/new and sign in.");
            Console.WriteLine("  2. Give it a name. Leave 'Add a README', '.gitignore' and 'license' unticked - GitSync takes care of those.");
            Console.WriteLine("  3. Click 'Create repository' and copy the HTTPS address it shows (https://github.com/you/name.git).");
            if (AskYesNo("Open github.com/new in your browser now?", true)) OpenBrowser("https://github.com/new");
            Console.WriteLine("(Type cancel at any question to stop.)");
        }

        // Accepts what people paste: "github.com/you/project" or a browser address like ".../project/tree/main"
        internal static string? NormalizeRemoteUrl(string input)
        {
            string url = input.Trim().Trim('"');
            var bareHost = Regex.Match(url, @"^(?:www\.)?((?:github\.com|gitlab\.com|bitbucket\.org|codeberg\.org)/.+)$", RegexOptions.IgnoreCase);
            if (bareHost.Success) url = "https://" + bareHost.Groups[1].Value;
            url = Regex.Replace(url, @"^https://www\.github\.com/", "https://github.com/", RegexOptions.IgnoreCase);
            var browse = Regex.Match(url, @"^(https://github\.com/[^/\s]+/[^/\s]+?)(?:\.git)?/(?:tree|blob|commits?|issues|pulls)(?:/.*)?$", RegexOptions.IgnoreCase);
            if (browse.Success) url = browse.Groups[1].Value;
            return LooksLikeGitUrl(url) ? url : null;
        }

        static bool HasRemote(string name) => RunGit(true, "remote", "get-url", name).Success;

        static void WarnIfUrlContainsLogin(string url)
        {
            if (IsHttpUrl(url) && Regex.IsMatch(url, @"://[^/@\s]+@"))
            {
                WriteLineColor(ConsoleColor.Yellow, "Note: this URL has a login or token in it, and Git stores it as plain text in .git\\config.");
                Console.WriteLine("It's safer to use the plain URL (https://github.com/you/project.git) and sign in through the browser window.");
            }
        }

        // Talks to the URL itself (not to 'origin'), so a wrong URL or a login problem shows up before anything is saved.
        // Returns the URL that works (it may have been switched from SSH to HTTPS), or null to try a different one.
        static string? VerifyRemoteAccess(string url, bool quiet = false)
        {
            WarnIfUrlContainsLogin(url);
            if (!IsUsableLocalRemote(url, quiet)) return null;
            while (true)
            {
                if (!quiet) Console.WriteLine($"Connecting to {MaskUrl(url)} (a sign-in window may pop up)...");
                var probe = RunGit(true, "ls-remote", "--heads", url);
                if (probe.Success)
                {
                    if (!quiet) LogVerbose("Remote is reachable.");
                    return url;
                }

                quiet = false;
                ExplainGitFailure(probe, "Connecting to the remote");
                if (OfferSignInAgain(probe, url)) continue;
                if (OfferSafeDirectoryFor(probe)) continue;
                string? https = HttpsAlternativeFor(probe, url);
                if (https != null && AskYesNo($"Use the HTTPS address ({https}) and sign in through the browser instead?", true))
                {
                    url = https;
                    continue;
                }

                Console.WriteLine("[1] Try again");
                Console.WriteLine("[2] Use a different URL");
                Console.WriteLine("[3] Cancel");
                string choice = AskChoice("Choice: ", "1", "2", "3");
                if (choice == "1") continue;
                if (choice == "2") return null;
                throw new UserAbortException();
            }
        }

        // A folder as the remote: never this project's own repository (pushing there "works" and uploads nothing), and
        // preferably a bare repository - a normal project folder refuses pushes to the branch it has checked out
        // quiet: the daily re-check of a remote that has been in use - only the "own folder" mistake is worth stopping for
        static bool IsUsableLocalRemote(string url, bool quiet)
        {
            string path = url;
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(url, UriKind.Absolute, out var fileUri)) path = fileUri.LocalPath;
            if (!IsExistingFolderPath(path)) return true;
            var info = RunGit(true, "-C", path, "rev-parse", "--absolute-git-dir", "--is-bare-repository");
            if (!info.Success) return true; // not a repository (or not trusted): ls-remote explains that
            var lines = info.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
            if (lines.Count < 2) return true;

            string ownGitDir = GetGitDir();
            if (ownGitDir != "" && string.Equals(Path.GetFullPath(lines[0]).TrimEnd('\\'), ownGitDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                WriteLineColor(ConsoleColor.Red, "That's this project's own folder. The remote has to be somewhere else - a repository on GitHub, or a bare repository on a shared drive.");
                return false;
            }
            if (lines[1] != "true" && !quiet)
            {
                WriteLineColor(ConsoleColor.Yellow, "That folder is a normal project folder, not a remote repository (a \"bare\" one, made with 'git init --bare').");
                Console.WriteLine("Pushing into it fails while it has that branch checked out.");
                return AskYesNo("Use it anyway?", false);
            }
            return true;
        }

        // After a remote problem: [1] Try again [2] Change the remote URL [3] Cancel - so a moved or mistyped remote
        // is never a dead end. Returns true when the step should be retried; branch follows a new URL's main branch.
        static bool HandleRemoteProblem(GitResult result, ref string branch)
        {
            string url = GetRemoteUrl();
            if (OfferSignInAgain(result, url)) return true;
            if (OfferSafeDirectoryFor(result)) return true;
            if (OfferHttpsInsteadOfSsh(result, url)) return true;

            Console.WriteLine("[1] Try again");
            Console.WriteLine("[2] Change the remote URL");
            Console.WriteLine("[3] Cancel");
            string choice = AskChoice("Choice: ", "1", "2", "3");
            if (choice == "3") return false;
            if (choice == "2")
            {
                SetupRemote(askForNewUrl: true);
                branch = ReconcileBranchWithRemote(branch);
            }
            return true;
        }

        // A local "master" next to GitHub's "main" (or the other way round) would be pushed as a second branch that
        // GitHub doesn't show by default - so the files seem to vanish. Puts the work where the remote expects it.
        // Returns the branch name to use on the remote.
        internal static string ReconcileBranchWithRemote(string branch)
        {
            if (CheckRemoteBranch(branch, out _) != RemoteBranchState.Missing) return branch;
            string? remoteDefault = GetRemoteDefaultBranch();
            if (remoteDefault == null || remoteDefault == branch) return branch;

            bool masterMainPair = (branch == "master" || branch == "main") && (remoteDefault == "master" || remoteDefault == "main");
            bool related = HasCommits() && FetchBranch(remoteDefault, quiet: true).Success
                && RunGit(true, "merge-base", "HEAD", $"refs/remotes/origin/{remoteDefault}").Success;
            // A feature branch made from the remote's main branch stays a branch of its own
            if (related && !masterMainPair) return branch;

            // 'branch' is the name on the remote side; the branch checked out here may be called something else ('laptop')
            string local = GetCurrentBranch();
            WriteLineColor(ConsoleColor.Yellow, local == branch || local == ""
                ? $"\nThe remote's main branch is '{remoteDefault}', but you're on '{branch}'."
                : $"\nYour branch '{local}' syncs with '{branch}', which the remote doesn't have - its main branch is '{remoteDefault}'.");
            Console.WriteLine($"[1] Put my work on '{remoteDefault}' (recommended - that's the branch GitHub shows)");
            Console.WriteLine($"[2] Create a separate '{branch}' branch on the remote");
            if (AskChoice("Choice: ", "1", "2") == "2") return branch;

            // '{branch}' isn't on the remote (any more): a tracking ref left from before would keep Pull and Status on it
            RunGit(true, "update-ref", "-d", $"refs/remotes/origin/{branch}");
            if (local == branch && !BranchExists(remoteDefault))
            {
                // The local master/main itself: rename it too, so plain git and other tools agree with GitSync
                var rename = HasCommits()
                    ? RunGit(true, "branch", "-m", remoteDefault)
                    : RunGit(true, "symbolic-ref", "HEAD", "refs/heads/" + remoteDefault);
                if (rename.Success)
                {
                    branchToRestore = null; // a deliberate rename isn't undone by Cancel
                    local = remoteDefault;
                    LogVerbose($"Renamed your branch '{branch}' to '{remoteDefault}'.");
                }
            }
            else if (local != "")
            {
                LogVerbose($"Your branch '{local}' will be uploaded into '{remoteDefault}' on the remote.");
            }

            // Remembered, so the next Sync doesn't ask again - and a record the rename carried over no longer names the old
            // branch. (The link to the remote branch itself is made by the push, once it has worked.)
            if (local != "")
            {
                if (local == remoteDefault) RunGit(true, "config", "--unset", $"branch.{local}.gitsynctarget");
                else RunGit(true, "config", $"branch.{local}.gitsynctarget", remoteDefault);
            }
            return remoteDefault;
        }

        // Which branch on the remote this local branch goes to: the one chosen before (git config branch.<name>.gitsynctarget),
        // else the branch of the same name. A differently named upstream (made with "git checkout -b work origin/main") is
        // only followed after asking once - so work in progress never lands on main because of a default.
        static string ResolveRemoteBranch(string localBranch)
        {
            if (localBranch == "") return localBranch;
            string recorded = RunGit(true, "config", "--get", $"branch.{localBranch}.gitsynctarget").Output.Trim();
            if (recorded != "") return recorded;
            string? upstream = GetUpstreamBranchName();
            if (upstream == null || upstream == localBranch) return localBranch;

            WriteLineColor(ConsoleColor.Yellow, $"\nYour branch '{localBranch}' is linked to '{upstream}' on the remote.");
            Console.WriteLine($"[1] Sync it as its own branch '{localBranch}' on the remote (recommended for work in progress)");
            Console.WriteLine($"[2] Sync it with '{upstream}' itself (your commits go straight into '{upstream}')");
            string target = AskChoice("Choice: ", "1", "2") == "2" ? upstream : localBranch;
            RunGit(true, "config", $"branch.{localBranch}.gitsynctarget", target);
            return target;
        }

        // Top-level file names on the remote branch (fetched first) - e.g. to see whether it already has a README
        static List<string> GetRemoteTopLevelFiles(string branch)
        {
            if (CheckRemoteBranch(branch, out _) != RemoteBranchState.Exists || !FetchBranch(branch, quiet: true).Success) return new List<string>();
            return RunGit(true, "ls-tree", "--name-only", $"refs/remotes/origin/{branch}").Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim())
                .ToList();
        }

        static bool IsReadmeFileName(string name) => Regex.IsMatch(name, @"^readme(\.\w+)?$", RegexOptions.IgnoreCase);
        static bool IsLicenseFileName(string name) => Regex.IsMatch(name, @"^(license|licence|copying)(\.\w+)?$", RegexOptions.IgnoreCase);

        // The "Safety Pull": merges commits that only exist on the remote branch into the current branch.
        // Returns false if that couldn't be done. pulled = commits that came in; remoteBranchExists = the branch is on the remote.
        // branch can change: when the user switches to another remote URL, it follows that remote's main branch.
        internal static bool IntegrateRemote(ref string branch, out int pulled, out bool remoteBranchExists)
        {
            pulled = 0;
            remoteBranchExists = false;
            if (IsMergeInProgress())
            {
                // The user chose to finish a merge themselves ("Leave it"): never start another one on top of it - or let
                // it be cancelled along with a new one
                WriteLineColor(ConsoleColor.Yellow, "\nThere's still an unfinished merge here, which you chose to sort out yourself.");
                Console.WriteLine("Fix the files, save them, and run Sync or Pull again - GitSync finishes it then.");
                return false;
            }
            while (true)
            {
                Console.WriteLine("\nChecking for remote updates...");
                var state = CheckRemoteBranch(branch, out var probe);
                if (state == RemoteBranchState.Missing)
                {
                    LogVerbose($"Branch '{branch}' doesn't exist on the remote yet - it will be created when you push.");
                    return true;
                }
                if (state == RemoteBranchState.Error)
                {
                    ExplainGitFailure(probe, "Checking the remote");
                    if (!HandleRemoteProblem(probe, ref branch)) return false;
                    continue;
                }

                var fetch = FetchBranch(branch);
                if (fetch.Success) break;
                ExplainGitFailure(fetch, "Downloading the remote changes", outputAlreadyShown: true);
                if (!HandleRemoteProblem(fetch, ref branch)) return false;
            }
            remoteBranchExists = true;

            string remoteRef = $"refs/remotes/origin/{branch}";
            if (RunGit(true, "merge-base", "--is-ancestor", remoteRef, "HEAD").Success)
            {
                LogVerbose("The remote has nothing new.");
                return true;
            }

            bool unrelated = !RunGit(true, "merge-base", "HEAD", remoteRef).Success;
            int incoming = GetAheadBehind(remoteRef).Behind;
            WriteLineColor(ConsoleColor.Yellow, $"Remote branch found with {incoming} commit(s) you don't have yet.");
            ShowIncomingCommits(remoteRef);

            if (unrelated && !LooksLikeFreshRemote(remoteRef))
            {
                // Not just GitHub's starter README/LICENSE: most likely the URL of a different project was pasted
                int remoteFiles = RunGit(true, "ls-tree", "-r", "--name-only", remoteRef).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                WriteLineColor(ConsoleColor.Yellow, $"\nThe remote already holds a different project ({remoteFiles} file(s)) that has nothing in common with this folder.");
                Console.WriteLine("[1] Merge the two projects into one");
                Console.WriteLine("[2] Use a different remote URL");
                Console.WriteLine("[3] Cancel");
                string choice = AskChoice("Choice: ", "1", "2", "3");
                if (choice == "3") return false;
                if (choice == "2")
                {
                    SetupRemote(askForNewUrl: true);
                    branch = ReconcileBranchWithRemote(branch); // the right repository may call its main branch differently
                    return IntegrateRemote(ref branch, out pulled, out remoteBranchExists);
                }
            }

            if (!ResolveCaseOnlyNameClashes(remoteRef, unrelated)) return false;

            // Both sides' .gitignore rules are combined line by line instead of becoming a conflict
            EnsureUnionMergeForGitignore();
            // Files the remote deleted (e.g. it stopped tracking .env): kept on this computer if they're ignored now.
            // --no-renames: ".env" turned into ".env.example" is a deletion of .env here, not a rename.
            var deletedByRemote = unrelated
                ? new List<string>()
                : SplitNul(RunGit(true, "diff", "--no-renames", "--name-only", "--diff-filter=D", "-z", $"HEAD...{remoteRef}").Output);

            LogVerbose(unrelated
                ? "The remote repository was started separately (e.g. with a README on GitHub), so the two histories are joined."
                : "Merging them in...");

            // Merge explicitly instead of "git pull", so the result doesn't depend on pull.rebase/pull.ff settings.
            // --no-overwrite-ignore: never let incoming files silently replace ignored local files such as .env
            var args = new List<string> { "merge", "--no-edit", "--no-stat", "--no-overwrite-ignore" };
            if (unrelated) args.Add("--allow-unrelated-histories");
            args.Add(remoteRef);
            sessionMergeStarted = true;
            var merge = RunGitLive(args.ToArray());
            // Right away - also when the merge stops at a conflict and the user leaves it unfinished
            RestoreIgnoredFilesDeletedByRemote(deletedByRemote);

            if (!merge.Success)
            {
                if (GetConflictedFiles().Count == 0)
                {
                    sessionMergeStarted = IsMergeInProgress();
                    ExplainGitFailure(merge, "Merging the remote changes", outputAlreadyShown: true);
                    return false;
                }
                if (!ResolveConflictsAndCommit(startedHere: true)) return false;
            }
            sessionMergeStarted = false;

            remoteMerged = true;
            keepChanges = true;
            pulled = incoming;
            WriteLineColor(ConsoleColor.Green, $"Merged {incoming} commit(s) from the remote.");
            return true;
        }

        // On Windows, README.md and readme.md are the same file: a merge that contains both silently replaces one with
        // the other. Computes the merge result first (without touching the folder) and offers to align the names.
        static bool ResolveCaseOnlyNameClashes(string remoteRef, bool unrelated)
        {
            var args = new List<string> { "merge-tree", "--write-tree" };
            if (unrelated) args.Add("--allow-unrelated-histories");
            args.Add("HEAD");
            args.Add(remoteRef);
            string tree = RunGit(true, args.ToArray()).Output.Split('\n')[0].Trim();
            if (!Regex.IsMatch(tree, "^[0-9a-f]{40,64}$")) return true; // Git older than 2.38: no way to check

            var mine = new HashSet<string>(SplitNul(RunGit(true, "ls-tree", "-r", "--name-only", "-z", "HEAD").Output));
            var allClashes = SplitNul(RunGit(true, "ls-tree", "-r", "--name-only", "-z", tree).Output)
                .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .ToList();
            // Pairs this project already has (made on Linux or a Mac) aren't the merge's doing - they mustn't block it
            var clashes = allClashes.Where(g => g.Any(p => !mine.Contains(p))).ToList();
            foreach (var g in allClashes.Except(clashes))
            {
                WriteLineColor(ConsoleColor.DarkGray, $"Note: this project already has {string.Join(" and ", g)} - Windows can only keep one of them in the folder.");
            }
            if (clashes.Count == 0) return true;

            var renames = clashes
                .Select(g => (Local: g.FirstOrDefault(mine.Contains), Remote: g.FirstOrDefault(p => !mine.Contains(p))))
                .Where(r => r.Local != null && r.Remote != null)
                .ToList();

            WriteLineColor(ConsoleColor.Yellow, "\nThe remote has files whose names differ from yours only in upper/lower case:");
            foreach (var g in clashes) Console.WriteLine($"    {string.Join("  vs  ", g)}");
            Console.WriteLine("Windows treats those as the same file, so merging them would silently replace one with the other.");
            if (renames.Count == 0)
            {
                Console.WriteLine("Rename one of them on the remote (or here), then run GitSync again.");
                return false;
            }
            Console.WriteLine("[1] Rename my files to the remote's spelling (then you choose which content to keep)");
            Console.WriteLine("[2] Cancel");
            if (AskChoice("Choice: ", "1", "2") == "2") return false;

            foreach (var (local, remote) in renames) RunGit(true, "--literal-pathspecs", "mv", "-f", local!, remote!);
            return Commit("Match the remote's spelling of file names");
        }

        static void EnsureUnionMergeForGitignore()
        {
            try
            {
                string attributes = GetGitPath("info/attributes");
                const string rule = ".gitignore merge=union";
                if (File.Exists(attributes) && File.ReadAllLines(attributes).Any(l => l.Trim() == rule)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(attributes)!);
                File.AppendAllText(attributes, rule + Environment.NewLine);
            }
            catch { }
        }

        // When the other side stopped tracking a file and now ignores it (typically .env), the merge deletes this
        // computer's copy. Put it back - it's ignored, so it stays local and isn't committed again.
        static void RestoreIgnoredFilesDeletedByRemote(List<string> deletedByRemote)
        {
            // While the merge is unfinished, HEAD is still this computer's last commit; once it's done, ORIG_HEAD is
            string source = IsMergeInProgress() ? "HEAD" : "ORIG_HEAD";
            foreach (var path in deletedByRemote)
            {
                if (File.Exists(path) || !RunGit(true, "check-ignore", "-q", "--", path).Success) continue;
                if (RunGit(true, "--literal-pathspecs", "restore", "--source=" + source, "--worktree", "--", path).Success)
                {
                    LogVerbose($"Kept your local copy of {path} (the remote stopped tracking it).");
                }
            }
        }

        // Walks the user through files that were changed both here and on the remote, then finishes the merge.
        // Returns false if the merge was cancelled (everything goes back to how it was before the merge started).
        static bool ResolveConflictsAndCommit(bool startedHere)
        {
            try
            {
                return ResolveConflictsLoop(startedHere);
            }
            catch (UserAbortException) when (startedHere)
            {
                // Leaving the menu (cancel, end of input) must never leave a half-finished merge with <<<<<<< markers behind
                AbortMerge();
                throw;
            }
        }

        static bool ResolveConflictsLoop(bool startedHere)
        {
            // Only files that had conflicts can hold leftover markers - an incoming README with a heading underlined by
            // "=======" isn't one - and not files resolved by taking one side's version whole. A merge someone else left
            // behind may have had its conflicts resolved by hand already: there, the files changed on both sides count.
            var conflicted = new HashSet<string>(GetConflictedFiles());
            var takenWhole = new HashSet<string>();
            if (!startedHere)
            {
                conflicted.UnionWith(ChangedOnBothSides());
                // That merge may have deleted ignored local files (such as .env) that the other side stopped tracking
                RestoreIgnoredFilesDeletedByRemote(SplitNul(RunGit(true, "diff", "--no-renames", "--name-only", "--diff-filter=D", "-z", "HEAD...MERGE_HEAD").Output));
            }
            while (true)
            {
                var files = GetConflictedFiles();
                conflicted.UnionWith(files);
                if (files.Count == 0)
                {
                    // The staged version is what gets committed. A file fixed in the folder after it was staged with its
                    // markers ("fix the files, save them, and run Sync again") is taken from the folder.
                    var toCheck = conflicted.Where(f => !takenWhole.Contains(f)).ToList();
                    foreach (var f in toCheck)
                    {
                        if (File.Exists(f) && FindConflictMarkerLine(ReadTextOrEmpty(f)) == 0 && FindConflictMarkerLine(StagedText(f)) > 0)
                        {
                            RunGit(true, "--literal-pathspecs", "add", "--", f);
                            LogVerbose($"Using {f} as it is in the folder now (the staged copy still had conflict markers).");
                        }
                    }

                    // Leftover markers, or an index that silently drops the other side, would be published - check first
                    var markers = toCheck
                        .Select(f => (File: f, Line: FindConflictMarkerLine(StagedText(f))))
                        .Where(m => m.Line > 0)
                        .ToList();
                    bool dropsRemote = !startedHere && RunGit(true, "diff", "--cached", "--quiet").Success;
                    if (markers.Count > 0 || dropsRemote)
                    {
                        if (markers.Count > 0)
                        {
                            WriteLineColor(ConsoleColor.Yellow, "These files still contain what looks like a conflict marker (<<<<<<<, ======= or >>>>>>>):");
                            foreach (var (file, line) in markers.Take(10)) Console.WriteLine($"    {file} (line {line})");
                        }
                        else
                        {
                            WriteLineColor(ConsoleColor.Yellow, "Finishing it as it is would drop every change that came from the other side.");
                        }
                        Console.WriteLine("[1] Cancel the merge (your files go back to how they were before it)");
                        Console.WriteLine("[2] Leave it - I'll sort it out myself");
                        Console.WriteLine(markers.Count > 0
                            ? "[3] Finish the merge anyway (those lines are supposed to be there)"
                            : "[3] Finish it anyway (keep only this side's version)");
                        string finishChoice = AskChoice("Choice: ", "1", "2", "3");
                        if (finishChoice == "1")
                        {
                            AbortMerge();
                            return false;
                        }
                        if (finishChoice == "2")
                        {
                            // The user keeps the merge, with whatever they resolved so far: cleanup must not cancel it
                            sessionMergeStarted = false;
                            keepChanges = true;
                            Console.WriteLine("The merge stays unfinished. Fix the files, save them, and run Sync or Pull again - GitSync finishes it then.");
                            return false;
                        }
                    }

                    var finish = RunGit(true, "commit", "--no-edit");
                    if (finish.Success)
                    {
                        WriteLineColor(ConsoleColor.Green, "Merge completed.");
                        return true;
                    }
                    ExplainGitFailure(finish, "Finishing the merge");
                    if (AskYesNo("Try again?", true)) continue;
                    AbortMerge();
                    return false;
                }

                // Binary files and "deleted on one side" have nothing to edit by hand; they need a mine/remote answer
                var notEditable = files.Where(f => !File.Exists(f) || !HasConflictMarkers(f)).ToList();

                WriteLineColor(ConsoleColor.Yellow, $"\n{files.Count} file(s) were changed both here and on the remote:");
                foreach (var f in files.Take(15)) Console.WriteLine($"    {f}{DescribeConflict(f)}");
                if (files.Count > 15) Console.WriteLine($"    ... and {files.Count - 15} more");
                Console.WriteLine("[1] Keep MY version of all of them");
                Console.WriteLine("[2] Keep the REMOTE version of all of them");
                Console.WriteLine("[3] Decide file by file");
                Console.WriteLine("[4] I'll fix them myself (opens the folder; come back here when done)");
                Console.WriteLine("[5] Cancel the merge (nothing is lost)");
                string choice = AskChoice("Choice: ", "1", "2", "3", "4", "5");

                if (choice == "5")
                {
                    AbortMerge();
                    return false;
                }
                if (choice == "1" || choice == "2")
                {
                    foreach (var f in files) TakeVersion(f, mine: choice == "1");
                    takenWhole.UnionWith(files);
                }
                else if (choice == "3")
                {
                    foreach (var f in files) TakeVersion(f, mine: AskChoice($"{f}{DescribeConflict(f)}: keep [m]ine or [r]emote? ", "m", "r") == "m");
                    takenWhole.UnionWith(files);
                }
                else
                {
                    foreach (var f in notEditable)
                    {
                        TakeVersion(f, mine: AskChoice($"{f} can't be edited by hand{DescribeConflict(f)}: keep [m]ine or [r]emote? ", "m", "r") == "m");
                        takenWhole.Add(f);
                    }

                    var editable = files.Except(notEditable).ToList();
                    if (editable.Count == 0) continue;

                    WriteLineColor(ConsoleColor.Cyan, "These files now contain both versions, between <<<<<<< and >>>>>>> lines:");
                    foreach (var f in editable) Console.WriteLine($"    {f}");
                    Console.WriteLine("Edit them so only the text you want is left (delete the marker lines), save them, and come back here.");
                    OpenFolder(Environment.CurrentDirectory);
                    GetUserInput("Press Enter when you're done...");
                    foreach (var f in editable)
                    {
                        if (File.Exists(f) && HasConflictMarkers(f))
                        {
                            WriteLineColor(ConsoleColor.Yellow, $"{f} still contains conflict markers.");
                            continue;
                        }
                        RunGit(true, "--literal-pathspecs", "add", "-A", "--", f);
                    }
                }
            }
        }

        static void AbortMerge()
        {
            var abort = RunGit(true, "merge", "--abort");
            sessionMergeStarted = false;
            if (abort.Success) WriteLineColor(ConsoleColor.Yellow, "Merge cancelled - your files are back to how they were before it.");
            else ExplainGitFailure(abort, "Cancelling the merge");
        }

        static string DescribeConflict(string file)
        {
            var stages = GetConflictStages(file);
            if (!stages.Contains(1) && stages.Contains(2) && !stages.Contains(3)) return " (new here, in a folder that was moved on the remote)";
            if (!stages.Contains(1) && stages.Contains(3) && !stages.Contains(2)) return " (new on the remote, in a folder that was moved here)";
            if (stages.Contains(1) && stages.Contains(2) && !stages.Contains(3)) return " (deleted on the remote)";
            if (stages.Contains(1) && stages.Contains(3) && !stages.Contains(2)) return " (deleted here)";
            return "";
        }

        static void TakeVersion(string file, bool mine)
        {
            var stages = GetConflictStages(file);
            int wanted = mine ? 2 : 3;
            int other = mine ? 3 : 2;
            if (stages.Contains(wanted))
            {
                RunGit(true, "--literal-pathspecs", "checkout", mine ? "--ours" : "--theirs", "--", file);
                RunGit(true, "--literal-pathspecs", "add", "--", file);
            }
            else if (!stages.Contains(1) && stages.Contains(other))
            {
                // Added on one side only (e.g. a new file in a folder the other side moved): there is nothing to
                // choose between, so keep the only version there is instead of deleting it
                RunGit(true, "--literal-pathspecs", "checkout", other == 2 ? "--ours" : "--theirs", "--", file);
                RunGit(true, "--literal-pathspecs", "add", "--", file);
            }
            else
            {
                // A real "changed on one side, deleted on the other" - and the chosen side deleted it
                RunGit(true, "--literal-pathspecs", "rm", "--quiet", "-f", "--", file);
            }
        }

        // A real conflict block: a "<<<<<<< " line, later "=======", then a ">>>>>>> " line. Not a Markdown/RST heading
        // underlined with seven "=", and not a document that quotes a single marker.
        internal static bool HasConflictMarkers(string file)
        {
            try
            {
                int seen = 0; // 1 = "<<<<<<<" found, 2 = then "======="
                foreach (var raw in File.ReadLines(file))
                {
                    string line = raw.TrimEnd('\r');
                    if (line.StartsWith("<<<<<<< ")) seen = 1;
                    else if (seen == 1 && line == "=======") seen = 2;
                    else if (seen == 2 && line.StartsWith(">>>>>>> ")) return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        // First line of this text that looks like any single conflict marker (one left behind after removing the others),
        // or 0. Only used on files that had a conflict.
        static int FindConflictMarkerLine(string text)
        {
            if (text.IndexOf('\0') >= 0) return 0; // binary
            int number = 0;
            foreach (var raw in text.Split('\n'))
            {
                number++;
                string line = raw.TrimEnd('\r');
                if (line == "=======" || line.StartsWith("<<<<<<< ") || line.StartsWith(">>>>>>> ") || line.StartsWith("||||||| ")) return number;
            }
            return 0;
        }

        // The staged version of a file (":0:" so a name such as "1:notes" can't be read as a merge stage), or ""
        static string StagedText(string file)
        {
            var info = new FileInfo(file);
            if (info.Exists && info.Length > 2 * 1024 * 1024) return ""; // big files are data, not text with markers
            return RunGit(true, "show", ":0:" + file).Output;
        }

        static string ReadTextOrEmpty(string file)
        {
            try { return File.ReadAllText(file); } catch { return ""; }
        }

        // Staged files (added or changed) that contain a real conflict block
        internal static List<string> FindConflictBlocksInStagedFiles()
        {
            var staged = new HashSet<string>(SplitNul(RunGit(true, "diff", "--cached", "--name-only", "-z", "--diff-filter=AMR").Output));
            if (staged.Count == 0) return new List<string>();
            // One quick search through the staged contents, then a closer look at the few files it finds
            var candidates = SplitNul(RunGit(true, "grep", "--cached", "-l", "-z", "-I", "-e", "^<<<<<<< ").Output);
            return candidates.Where(staged.Contains).Where(HasConflictMarkers).ToList();
        }

        // During a merge: files whose staged version differs from both sides, i.e. the ones both sides changed
        static List<string> ChangedOnBothSides()
        {
            var fromTheirs = new HashSet<string>(SplitNul(RunGit(true, "diff", "--cached", "--name-only", "-z", "MERGE_HEAD").Output));
            return SplitNul(RunGit(true, "diff", "--cached", "--name-only", "-z", "HEAD").Output).Where(fromTheirs.Contains).ToList();
        }

        static void PerformPush(string branch)
        {
            string remote = GetRemoteUrl();
            // "Already on the remote" has to be true before the checks below trust it (or a rebuild sits on it)
            RefreshRemoteBranches();
            if (!HandleLargeBlobsBeforePush()) return;
            if (!HandleSecretsInUnpushedCommits()) return;
            bool firstPublish = CheckRemoteBranch(branch, out _) == RemoteBranchState.Missing;

            while (true)
            {
                LogVerbose("Pushing to remote server...");
                var result = RunGitLive("push", "--progress", "-u", "origin", $"HEAD:refs/heads/{branch}");

                if (result.Success)
                {
                    // Mark success so cleanup doesn't run
                    keepChanges = true;
                    Log("Success", remote, branch);

                    if (result.AllText.Contains("Everything up-to-date"))
                    {
                        WriteLineColor(ConsoleColor.Green, "\nAlready up to date - there was nothing new to push.");
                        return;
                    }

                    WriteLineColor(ConsoleColor.Green, "\nSUCCESS!");
                    FixDanglingRemoteHead(remote, branch);
                    string? webUrl = ToWebUrl(remote);
                    if (webUrl != null)
                    {
                        Console.WriteLine($"View it online: {webUrl}");
                        if (firstPublish) OpenBrowser(webUrl); // only the first time - not a new browser tab on every daily sync
                    }
                    return;
                }

                Log("Failed", remote, branch);
                ExplainGitFailure(result, "Push", outputAlreadyShown: true);

                if (IsRejectedNonFastForward(result))
                {
                    // Someone pushed in the meantime: merge their commits and try again
                    if (AskYesNo("Merge the new remote changes and try again?", true) && IntegrateRemote(ref branch, out _, out _)) continue;
                }
                else if (OfferSignInAgain(result, remote))
                {
                    continue;
                }
                else if (OfferHttpsInsteadOfSsh(result, remote))
                {
                    remote = GetRemoteUrl();
                    continue;
                }
                else if (OfferSafeDirectoryFor(result))
                {
                    continue;
                }

                bool canUndo = gitInitialized && !remoteMerged;
                Console.WriteLine("\n[1] Try again");
                Console.WriteLine("[2] Stop here and keep everything (run Sync again later to push)");
                Console.WriteLine("[3] Use a different remote URL");
                if (canUndo) Console.WriteLine("[4] Undo everything GitSync did in this folder");
                string choice = canUndo ? AskChoice("Choice: ", "1", "2", "3", "4") : AskChoice("Choice: ", "1", "2", "3");
                if (choice == "1") continue;
                if (choice == "3")
                {
                    SetupRemote(askForNewUrl: true);
                    remote = GetRemoteUrl();
                    branch = ReconcileBranchWithRemote(branch);
                    // The new remote is checked like the first one: its own commits merged in, and anything it doesn't
                    // have yet (now: everything) checked for large files and secrets again
                    if (!IntegrateRemote(ref branch, out _, out _) || !HandleLargeBlobsBeforePush() || !HandleSecretsInUnpushedCommits())
                    {
                        keepChanges = true;
                        return;
                    }
                    firstPublish = CheckRemoteBranch(branch, out _) == RemoteBranchState.Missing;
                    continue;
                }

                // Keeping is the default; undo only runs when explicitly chosen (cleanup does it when the mode ends)
                keepChanges = choice != "4";
                return;
            }
        }

        // A bare repository made with plain "git init --bare" points HEAD at "master". After the team pushes "main" into
        // it, its clones would come out empty - so point HEAD at main (or master), or at the pushed branch when it's the
        // only one. Never at a feature branch while main is there.
        static void FixDanglingRemoteHead(string remote, string branch)
        {
            if (!IsExistingFolderPath(remote)) return;
            var symref = RunGit(true, "ls-remote", "--symref", "origin", "HEAD");
            if (!symref.Success || Regex.IsMatch(symref.Output, @"^ref: refs/heads/\S+\s+HEAD", RegexOptions.Multiline)) return;

            var heads = GetRemoteBranchNames() ?? new List<string>();
            string? target = heads.Contains("main") ? "main" : heads.Contains("master") ? "master" : heads.Count == 1 ? heads[0] : null;
            if (target == null) return;
            if (RunGit(true, "--git-dir=" + remote, "symbolic-ref", "HEAD", "refs/heads/" + target).Success)
            {
                LogVerbose($"The shared repository pointed at a branch that doesn't exist, so it now opens on '{target}' by default.");
            }
        }

        // Returns false if the push should not go ahead
        static bool HandleLargeBlobsBeforePush()
        {
            var large = FindLargeBlobsToPush();
            if (large.Count == 0) return true;

            WriteLineColor(ConsoleColor.Red, "\nThese files are over GitHub's 100 MB limit and are inside commits that haven't been pushed yet:");
            foreach (var (path, size) in large) Console.WriteLine($"    {path} ({Math.Round(size / 1024.0 / 1024.0, 1)} MB)");
            Console.WriteLine("GitHub would reject the whole push - and deleting the files now wouldn't help, because the earlier commits still contain them.");

            bool lfsAvailable = RunGit(true, "lfs", "version").Output.Contains("git-lfs");
            if (lfsAvailable) Console.WriteLine("[1] Move them into Git LFS (rewrites only the commits that aren't on the remote yet) - recommended");
            Console.WriteLine("[2] Leave them out (they stay on your disk) and rebuild the commits that aren't pushed yet");
            Console.WriteLine("[3] Push anyway");
            Console.WriteLine("[4] Cancel");
            string choice = lfsAvailable ? AskChoice("Choice: ", "1", "2", "3", "4") : AskChoice("Choice: ", "2", "3", "4");
            if (choice == "4") return false;
            if (choice == "3") return true;

            if (choice == "2")
            {
                if (!CanRebuildUnpushedCommits()) return false; // before anything is touched
                var tracked = GetTrackedFiles();
                foreach (var (path, _) in large) KeepOutOfGit(path, tracked);
                RunGit(true, "add", "--", ".gitignore");
                return SquashUnpushedCommits();
            }

            // Without --include-ref, git-lfs only rewrites this branch's commits that aren't on any remote yet - so
            // commits that are already published (e.g. from main, for a new feature branch) are never touched.
            // --skip-fetch: it goes by the remote branches GitSync just checked, instead of moving them mid-way.
            var migrate = RecordAppendBy(Path.Combine(sessionFolder!, ".gitattributes"), () =>
            {
                var run = RunGitLive("lfs", "migrate", "import", "--above=100MiB", "--skip-fetch");
                return !run.Success && run.AllText.Contains("unknown flag") ? RunGitLive("lfs", "migrate", "import", "--above=100MiB") : run;
            });
            if (!migrate.Success)
            {
                ExplainGitFailure(migrate, "Moving the files into Git LFS", outputAlreadyShown: true);
                return false;
            }
            keepChanges = true; // the commits were rewritten: they hold the user's work now, cancelling mustn't undo files in them

            // migrate leaves small "pointer" text files in the folder where the files were - put the real ones back
            // (from the local LFS store, nothing is downloaded) before anything else happens
            RunGit(true, "lfs", "checkout");
            var pointers = large.Where(l => IsLfsPointerFile(Path.Combine(sessionFolder!, l.Path))).ToList();
            if (pointers.Count > 0)
            {
                WriteLineColor(ConsoleColor.Red, "\nGit LFS didn't put these files back in the folder (they're stored safely in .git\\lfs):");
                foreach (var (path, _) in pointers) Console.WriteLine($"    {path}");
                Console.WriteLine("Run 'git lfs checkout' in this folder to get them back. Nothing was pushed.");
                return false;
            }
            lfsTrackedCount += large.Count;
            WriteLineColor(ConsoleColor.Green, "Done - those files now go to Git LFS.");
            return true;
        }

        // Secrets inside commits that haven't been pushed yet would be published with them - also ones committed outside
        // GitSync, or kept out of Git only now. Those commits are rebuilt without them (nothing has left this computer
        // yet). A file whose current version is fine stays in the project: the rebuild only drops its older versions.
        // Returns false if the push should not go ahead.
        internal static bool HandleSecretsInUnpushedCommits()
        {
            while (true)
            {
                var found = FindSecretsInUnpushedCommits().Where(f => !secretBlobsAccepted.Contains(f.Blob)).ToList();
                if (found.Count == 0) return true;

                // The file as it is now (its staged version) tells "the file holds a key" from "an earlier version did"
                var current = StagedBlobs();
                var keepOut = new List<string>();
                WriteLineColor(ConsoleColor.Red, "\nThese files in commits that haven't been pushed yet look like they hold passwords or keys:");
                foreach (var hits in found.GroupBy(f => f.File))
                {
                    string file = hits.Key;
                    current.TryGetValue(file, out string? now);
                    bool nowFlagged = now != null && hits.Any(h => h.Blob == now);
                    bool byName = IsSensitiveFileName(Path.GetFileName(file)) && (now == null || !secretBlobsAccepted.Contains(now));
                    if (nowFlagged || byName) keepOut.Add(file);
                    string where = now == null ? " - no longer in the project, but still inside an earlier commit"
                                 : nowFlagged || byName ? ""
                                 : " - only in an earlier version; the file as it is now is fine and stays";
                    Console.WriteLine($"    {file} ({hits.First().Reason}){where}");
                }

                // Leaving out a file the remote already has takes it out of the project for everyone
                var published = keepOut.Where(f => current.ContainsKey(f)
                    && RunGit(true, "--literal-pathspecs", "log", "-1", "--format=%h", "--remotes=origin", "--", f).Output.Trim().Length > 0).ToList();
                Console.WriteLine("Anything pushed can be seen and copied - even after you delete it again.");
                if (published.Count > 0)
                {
                    Console.WriteLine($"Already on the remote: {string.Join(", ", published)}. Leaving them out removes them from the project (your copy stays);");
                    Console.WriteLine("other computers lose theirs on their next pull - unless they use GitSync, which keeps ignored local copies.");
                }
                Console.WriteLine(keepOut.Count > 0
                    ? "[1] Leave them out: rebuild the unpushed commits as one commit without them (recommended - your files stay on disk)"
                    : "[1] Rebuild the unpushed commits as one commit without those older versions (recommended - your files stay as they are)");
                Console.WriteLine("[2] Push anyway (I'm sure they're safe)");
                Console.WriteLine("[3] Cancel");
                string choice = AskChoice("Choice: ", "1", "2", "3");
                if (choice == "3") return false;
                if (choice == "2")
                {
                    foreach (var hit in found) secretBlobsAccepted.Add(hit.Blob);
                    secretsStatus = "Warning Ignored";
                    return true;
                }

                if (!CanRebuildUnpushedCommits()) return false; // before anything is touched
                if (keepOut.Count > 0)
                {
                    var tracked = GetTrackedFiles();
                    foreach (var file in keepOut) KeepOutOfGit(file, tracked);
                    RunGit(true, "add", "--", ".gitignore");
                }
                if (!SquashUnpushedCommits(keepOut.Where(current.ContainsKey).ToList())) return false;
                // ...and check again: the same content can sit in the history under another name too
            }
        }

        // path -> blob id of every staged file (what the next commit holds)
        static Dictionary<string, string> StagedBlobs()
        {
            var blobs = new Dictionary<string, string>();
            foreach (var entry in SplitNul(RunGit(true, "ls-files", "-s", "-z").Output))
            {
                int tab = entry.IndexOf('\t');
                var parts = tab > 0 ? entry.Substring(0, tab).Split(' ') : Array.Empty<string>();
                if (parts.Length >= 3 && parts[2] == "0") blobs[entry.Substring(tab + 1)] = parts[1];
            }
            return blobs;
        }

        // A rebuild has to sit on one commit the remote already has; says so before anything is changed when there isn't one
        static bool CanRebuildUnpushedCommits()
        {
            if (TryGetPublishedBase(out _)) return true;
            WriteLineColor(ConsoleColor.Red, "\nThe unpushed commits can't be rebuilt automatically: they join work from more than one branch that's already on the remote.");
            Console.WriteLine("Take the files out of those commits in your Git tool (or use Git LFS for large files), then run Sync again.");
            return false;
        }

        // Turns the commits that aren't on the remote yet into one commit of the current (already cleaned) index. It sits
        // on the newest commit the remote already has - so published commits are never rewritten or left out, and if the
        // remote has moved on meanwhile, the push is rejected (and merged) instead of quietly undoing someone's work.
        // stoppedTracking: files the rebuilt commit removes from the project, named in its message.
        internal static bool SquashUnpushedCommits(List<string>? stoppedTracking = null)
        {
            if (!TryGetPublishedBase(out string? baseCommit))
            {
                CanRebuildUnpushedCommits(); // says why
                return false;
            }

            string message = RunGit(true, "log", "-1", "--no-merges", "--format=%s", "HEAD", "--not", "--remotes=origin").Output.Trim();
            if (message == "") message = "Update files";
            if (stoppedTracking is { Count: > 0 }) message += $" (stop tracking {string.Join(", ", stoppedTracking)})";

            string oldHead = CurrentHead();
            var reset = baseCommit != null ? RunGit(true, "reset", "--soft", baseCommit) : RunGit(true, "update-ref", "-d", "HEAD");
            if (!reset.Success)
            {
                ExplainGitFailure(reset, "Rebuilding the commits");
                return false;
            }
            if (baseCommit != null && RunGit(true, "diff", "--cached", "--quiet").Success)
            {
                // Those files were all the unpushed work there was: the branch is simply back where the remote is
                keepChanges = true;
                WriteLineColor(ConsoleColor.Green, "Without those files, nothing new is left to push.");
                return true;
            }
            var commit = RunGit(true, "commit", "--quiet", "-m", message);
            if (!commit.Success)
            {
                ExplainGitFailure(commit, "Rebuilding the commits");
                RunGit(true, "update-ref", "HEAD", oldHead); // the branch goes back to the commits it had
                return false;
            }
            keepChanges = true; // the rebuilt commit holds the user's work now: cancelling mustn't undo files in it
            WriteLineColor(ConsoleColor.Green, $"Rebuilt the unpushed work as one commit: {RunGit(true, "log", "-1", "--format=%h %s").Output.Trim()}");
            return true;
        }

        // =================================================================================
        // [2] Pull Latest Changes
        // =================================================================================

        static void HandlePull()
        {
            string? folder = PickFolder("Select the repository folder to update");
            if (folder == null)
            {
                Console.WriteLine("No folder selected.");
                return;
            }

            OpenSessionFolder(folder);
            if (!EnsureFolderTrusted(folder)) return;
            if (!IsInsideRepository())
            {
                WriteLineColor(ConsoleColor.Red, "\nThis folder is not a Git repository. Use [1] Sync to create one, or [3] Clone to download one.");
                return;
            }

            // Work from the repository's top folder, so file paths from git always line up
            OpenSessionFolder(GetRepositoryRoot());
            WriteLineColor(ConsoleColor.Cyan, $"\nRepository: {sessionFolder}");
            if (!ConfirmBroadFolder(sessionFolder!)) return;

            if (!PrepareExistingRepository(out string branch, allowBranchChange: false)) return;
            if (!HasCommits())
            {
                WriteLineColor(ConsoleColor.Yellow, "This repository has no commits yet. Use [1] Sync to publish it first.");
                return;
            }
            UseLongPaths();
            ExcludeLocalOnlyFiles();

            if (HasRemote("origin"))
            {
                WriteLineColor(ConsoleColor.Cyan, $"Remote 'origin': {MaskUrl(GetRemoteUrl())}");
            }
            else
            {
                WriteLineColor(ConsoleColor.Yellow, "This repository isn't connected to a remote yet.");
                SetupRemote();
            }

            // Pull brings in what the branch is linked to (like "git pull"), else what Sync was told to sync it with
            string recorded = RunGit(true, "config", "--get", $"branch.{branch}.gitsynctarget").Output.Trim();
            string remoteBranch = ReconcileBranchWithRemote(GetUpstreamBranchName() ?? (recorded != "" ? recorded : branch));

            // Uncommitted work would block the pull (or get mixed into it), so it gets committed first
            var dirty = GetWorkingTreeChanges();
            if (dirty.Count > 0)
            {
                WriteLineColor(ConsoleColor.Yellow, $"\nYou have {dirty.Count} uncommitted change(s):");
                foreach (var change in dirty.Take(10)) Console.WriteLine($"    {DescribeStatusLine(change)}");
                if (dirty.Count > 10) Console.WriteLine($"    ... and {dirty.Count - 10} more");
                Console.WriteLine("[1] Commit them first, then pull (recommended)");
                Console.WriteLine("[2] Cancel");
                if (AskChoice("Choice: ", "1", "2") == "2") return;
                if (!CommitAllChanges()) return;
            }

            if (!IntegrateRemote(ref remoteBranch, out int pulled, out bool remoteBranchExists))
            {
                WriteLineColor(ConsoleColor.Yellow, "Nothing was pulled.");
                return;
            }

            if (!remoteBranchExists)
            {
                WriteLineColor(ConsoleColor.Yellow, $"\nThe branch '{remoteBranch}' isn't on the remote yet, so there was nothing to pull.");
            }
            else
            {
                // Plain "git pull"/"git push" (and other Git tools) know where this branch belongs from now on - also
                // after it was renamed to the remote's main branch, whose old name doesn't exist there any more
                if (GetUpstream() != $"origin/{remoteBranch}") RunGit(true, "branch", $"--set-upstream-to=origin/{remoteBranch}");

                if (pulled > 0)
                {
                    WriteLineColor(ConsoleColor.Green, $"\nUpdated! Pulled {pulled} new commit(s).");
                    var changed = RunGit(true, "diff", "--name-only", "ORIG_HEAD", "HEAD").Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    Console.WriteLine($"{changed.Length} file(s) changed.");
                    if (File.Exists(".gitmodules")) UpdateSubmodules();
                }
                else
                {
                    WriteLineColor(ConsoleColor.Green, "\nAlready up to date.");
                }
            }

            // Pushing only goes back to where the updates came from. A branch that gets them from a differently named one
            // (work linked to main) is pushed by Sync, which asks where it goes - never by a generic "Push them now?"
            string local = GetCurrentBranch();
            string target = RunGit(true, "config", "--get", $"branch.{local}.gitsynctarget").Output.Trim();
            if ((target != "" ? target : local) != remoteBranch)
            {
                if (CountUnpushedCommits() > 0)
                {
                    Console.WriteLine($"\nYour branch '{local}' gets its updates from '{remoteBranch}'. To upload your own commits, use [1] Sync - it asks where they go.");
                }
                return;
            }

            int unpushed = CountCommitsToPush(remoteBranch, remoteBranchExists);
            if (!remoteBranchExists)
            {
                // Pushing creates it - also when every commit already sits on some other remote branch
                WriteLineColor(ConsoleColor.Yellow, unpushed > 0
                    ? $"You have {unpushed} commit(s) that aren't on the remote yet (pushing creates '{remoteBranch}' there)."
                    : $"Pushing creates '{remoteBranch}' on the remote.");
                if (AskYesNo(unpushed > 0 ? "Push them now?" : "Publish it now?", true)) PerformPush(remoteBranch);
            }
            else if (unpushed > 0)
            {
                WriteLineColor(ConsoleColor.Yellow, $"You have {unpushed} commit(s) that aren't on the remote yet.");
                if (AskYesNo("Push them now?", true)) PerformPush(remoteBranch);
            }
        }

        static bool CommitAllChanges()
        {
            CheckGitIdentity();
            BackupIndex();
            CheckEmbeddedRepositories();
            CheckLargeFiles(sessionFolder!);
            ScanSecrets(sessionFolder!, onlyChangedFiles: HasPublishedHistory());

            if (!StageAll()) return false;
            var changes = GetStagedChanges();
            if (changes.Count == 0) return true;

            string suggested = SuggestCommitMessage(changes);
            string message = GetUserInput($"Commit message (Enter = \"{suggested}\"): ", freeText: true);
            if (message == "") message = suggested;
            return Commit(message);
        }

        static void UpdateSubmodules()
        {
            LogVerbose("This project uses submodules (other repositories inside it) - downloading them...");
            var update = RunGitLive("submodule", "update", "--init", "--recursive", "--progress");
            if (!update.Success) ExplainGitFailure(update, "Downloading the submodules", outputAlreadyShown: true);
        }

        // =================================================================================
        // [4] Status
        // =================================================================================

        static void HandleStatus()
        {
            string? folder = PickFolder("Select a repository folder");
            if (folder == null)
            {
                Console.WriteLine("No folder selected.");
                return;
            }

            OpenSessionFolder(folder);
            if (!EnsureFolderTrusted(folder)) return;
            if (!IsInsideRepository())
            {
                WriteLineColor(ConsoleColor.Yellow, "\nThis folder is not a Git repository yet. Use [1] Sync to create one.");
                return;
            }
            OpenSessionFolder(GetRepositoryRoot());

            string branch = GetCurrentBranch();
            WriteLineColor(ConsoleColor.Cyan, $"\nRepository:  {sessionFolder}");
            Console.WriteLine($"Branch:      {(branch == "" ? "(none - detached HEAD)" : branch)}");
            Console.WriteLine($"Last commit: {(HasCommits() ? RunGit(true, "log", "-1", "--format=%h %s (%cr)").Output.Trim() : "(no commits yet)")}");
            if (IsMergeInProgress() || IsRebaseInProgress()) WriteLineColor(ConsoleColor.Yellow, "A merge or rebase is unfinished - run [1] Sync or [2] Pull to finish or cancel it.");

            if (!HasRemote("origin"))
            {
                WriteLineColor(ConsoleColor.Yellow, "Not connected to a remote yet - use [1] Sync to publish it.");
            }
            else
            {
                Console.WriteLine($"Remote:      {MaskUrl(GetRemoteUrl())}");
                // The same branch Pull gets its updates from
                string recorded = branch == "" ? "" : RunGit(true, "config", "--get", $"branch.{branch}.gitsynctarget").Output.Trim();
                string remoteBranch = GetUpstreamBranchName() ?? (recorded != "" ? recorded : branch);
                if (remoteBranch != "" && HasCommits())
                {
                    var state = CheckRemoteBranch(remoteBranch, out var probe);
                    if (state == RemoteBranchState.Error)
                    {
                        ExplainGitFailure(probe, "Checking the remote");
                    }
                    else if (state == RemoteBranchState.Missing)
                    {
                        WriteLineColor(ConsoleColor.Yellow, $"'{remoteBranch}' isn't on the remote yet - use [1] Sync to publish it.");
                    }
                    else
                    {
                        var fetch = FetchBranch(remoteBranch, quiet: true);
                        if (!fetch.Success)
                        {
                            ExplainGitFailure(fetch, "Checking the remote");
                        }
                        else
                        {
                            var (ahead, behind) = GetAheadBehind($"refs/remotes/origin/{remoteBranch}");
                            if (ahead == 0 && behind == 0) WriteLineColor(ConsoleColor.Green, "Up to date with the remote.");
                            if (behind > 0) WriteLineColor(ConsoleColor.Yellow, $"{behind} new commit(s) on the remote - use [2] Pull to get them.");
                            if (ahead > 0) WriteLineColor(ConsoleColor.Yellow, $"{ahead} commit(s) not pushed yet - use [1] Sync to push them.");
                        }
                    }
                }
            }

            var changes = GetWorkingTreeChanges();
            if (changes.Count == 0)
            {
                WriteLineColor(ConsoleColor.Green, "No uncommitted changes.");
                return;
            }
            WriteLineColor(ConsoleColor.Yellow, $"{changes.Count} uncommitted change(s):");
            foreach (var change in changes.Take(20)) Console.WriteLine($"    {DescribeStatusLine(change)}");
            if (changes.Count > 20) Console.WriteLine($"    ... and {changes.Count - 20} more");
        }

        // " M src/app.js" -> "modified  src/app.js"
        internal static string DescribeStatusLine(string porcelainLine)
        {
            string code = porcelainLine.Substring(0, 2);
            string path = porcelainLine.Substring(3);
            string kind = code == "??" ? "new"
                : code.Contains('U') || code == "AA" || code == "DD" ? "conflict"
                : code.Contains('D') ? "deleted"
                : code.Contains('R') ? "renamed"
                : code.Contains('A') ? "added"
                : code.Contains('M') ? "modified"
                : "changed";
            return $"{kind,-9} {path}";
        }

        // =================================================================================
        // [3] Clone & Setup
        // =================================================================================

        static void HandleClone()
        {
            string? url = AskCloneUrl();
            if (url == null) return;

            string? picked = PickCloneParent();
            if (picked == null) return;
            string parent = picked;

            // Named after the repository - unless the user typed a name because that folder was taken
            bool nameFromUrl = true;
            string? ChooseClonePath(string path)
            {
                while (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                {
                    WriteLineColor(ConsoleColor.Yellow, $"\n'{path}' already exists and isn't empty.");
                    string other = GetUserInput("Enter a different folder name (Enter = cancel): ");
                    if (other == "") return null;
                    if (other.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    {
                        Console.WriteLine("That name has characters Windows doesn't allow in folder names.");
                        continue;
                    }
                    path = Path.Combine(parent, other);
                    nameFromUrl = false;
                }
                return path;
            }

            string? clonePath = ChooseClonePath(Path.Combine(parent, GetRepoNameFromUrl(url)));
            if (clonePath == null) return;

            while (true)
            {
                LogVerbose($"Cloning {MaskUrl(url)} into {clonePath}...");
                var result = RunGitLive("clone", "--progress", url, clonePath);
                if (result.Success) break;

                ExplainGitFailure(result, "Clone", outputAlreadyShown: true);
                if (OfferSignInAgain(result, url)) continue;
                string? https = HttpsAlternativeFor(result, url);
                if (https != null && AskYesNo($"Use the HTTPS address ({https}) and sign in through the browser instead?", true))
                {
                    url = https;
                    continue;
                }

                Console.WriteLine("[1] Try again");
                Console.WriteLine("[2] Enter a different URL");
                Console.WriteLine("[3] Cancel");
                string choice = AskChoice("Choice: ", "1", "2", "3");
                if (choice == "3") return;
                if (choice == "2")
                {
                    url = AskCloneUrl();
                    if (url == null) return;
                    // A folder named after the mistyped address would be confusing - name it after the new one
                    clonePath = ChooseClonePath(nameFromUrl ? Path.Combine(parent, GetRepoNameFromUrl(url)) : clonePath);
                    if (clonePath == null) return;
                }
            }

            RememberFolder(clonePath);
            if (!IsInsideWorkTree(parent)) SaveCloneParent(parent);
            Directory.SetCurrentDirectory(clonePath);
            WriteLineColor(ConsoleColor.Green, $"\nCloned to: {clonePath}");
            CheckOutIfCloneIsEmpty();
            if (File.Exists(".gitmodules")) UpdateSubmodules();

            // Detect type
            var types = DetectProjectTypes(clonePath);
            var meta = ParseProjectMetadata(clonePath, types);
            Console.WriteLine($"\nDetected Project: {meta.Name}" + (types.Count > 0 ? $" ({string.Join(", ", types)})" : ""));

            string? setupCmd = GetSetupCommand(clonePath, types);
            if (setupCmd != null)
            {
                if (AskYesNo($"\nRun setup command '{setupCmd}'?", false)) RunShellCommand(setupCmd, clonePath);
            }
            else if (types.Contains("Unity")) Console.WriteLine("Open the folder in Unity Hub (Add > Add project from disk) to get started.");
            else if (types.Contains("Unreal")) Console.WriteLine("Double-click the .uproject file to open it in Unreal Engine.");
            else if (types.Contains("Godot")) Console.WriteLine("Import the folder in the Godot project manager to get started.");

            if (AskYesNo("\nOpen in Explorer?", true)) OpenFolder(clonePath);
        }

        static string? AskCloneUrl()
        {
            while (true)
            {
                string answer = GetUserInput("\nEnter Git URL to clone: ");
                if (string.IsNullOrWhiteSpace(answer)) return null;
                string? url = NormalizeRemoteUrl(answer);
                if (url != null) return url;
                Console.WriteLine("That doesn't look like a Git URL. On GitHub, copy it from the green 'Code' button. (Enter = cancel)");
            }
        }

        // A remote whose HEAD points at a missing branch clones as an empty folder: check out the branch it does have
        static void CheckOutIfCloneIsEmpty()
        {
            if (HasCommits()) return;
            var branches = RunGit(true, "for-each-ref", "--format=%(refname:strip=3)", "refs/remotes/origin").Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(b => b.Trim())
                .Where(b => b != "HEAD")
                .ToList();
            if (branches.Count == 0) return;

            string pick = branches.Count == 1 ? branches[0] : branches.Contains("main") ? "main" : branches.Contains("master") ? "master" : branches[0];
            if (RunGit(true, "checkout", pick).Success) LogVerbose($"The remote didn't say which branch to open, so '{pick}' was checked out.");
        }

        static bool IsInsideWorkTree(string folder)
        {
            return RunGit(true, "-C", folder, "rev-parse", "--is-inside-work-tree").Output.Trim() == "true";
        }

        static string? PickCloneParent()
        {
            // A project folder dropped onto GitSync.exe is a repository itself - clone next to it, not inside it
            string? fromArgs = startupFolder != null && IsInsideWorkTree(startupFolder) ? Path.GetDirectoryName(startupFolder) : startupFolder;
            string defaultParent = fromArgs ?? LoadCloneParent() ?? DefaultReposFolder();

            while (true)
            {
                WriteLineColor(ConsoleColor.Cyan, $"\nClone into: {defaultParent}");
                string answer = GetUserInput("Press Enter to use it, type B to browse, or paste another folder path: ").Trim('"');
                if (answer == "" || IsYesWord(answer))
                {
                    Directory.CreateDirectory(defaultParent);
                    return defaultParent;
                }
                if (IsExistingFolderPath(answer)) return Path.GetFullPath(answer);
                if (answer.Equals("b", StringComparison.OrdinalIgnoreCase) && !Console.IsInputRedirected)
                {
                    return ShowFolderSelection("Choose where to put the cloned project", defaultParent);
                }

                bool fullPath = false;
                try { fullPath = Path.IsPathFullyQualified(answer); } catch { }
                if (fullPath && AskYesNo($"'{answer}' doesn't exist yet. Create it?", true))
                {
                    Directory.CreateDirectory(answer);
                    return Path.GetFullPath(answer);
                }
                Console.WriteLine("That folder doesn't exist.");
            }
        }

        static string DefaultReposFolder()
        {
            string repos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos");
            return Directory.Exists(repos) ? repos : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        static string? LoadCloneParent()
        {
            try
            {
                string file = Path.Combine(DataDir, "clone_folder.txt");
                string? folder = File.Exists(file) ? File.ReadAllText(file).Trim() : null;
                return !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? folder : null;
            }
            catch
            {
                return null;
            }
        }

        static void SaveCloneParent(string folder)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(Path.Combine(DataDir, "clone_folder.txt"), folder);
            }
            catch { }
        }

        // Only suggests a command when the project's marker file sits at the top of the clone (where the command runs)
        static string? GetSetupCommand(string folder, List<string> types)
        {
            bool Has(string name) => File.Exists(Path.Combine(folder, name));

            if (types.Contains("Unity") || types.Contains("Unreal") || types.Contains("Godot")) return null; // opened in their editors
            if (types.Contains("Node.js") && Has("package.json"))
                return Has("pnpm-lock.yaml") ? "pnpm install" : Has("yarn.lock") ? "yarn install" : "npm install";
            if (types.Contains("Python") && Has("requirements.txt")) return "pip install -r requirements.txt";
            if (types.Contains("Rust") && Has("Cargo.toml")) return "cargo build";
            if (types.Contains("VisualStudio") && Directory.EnumerateFiles(folder, "*.sln*").Concat(Directory.EnumerateFiles(folder, "*.csproj")).Any()) return "dotnet restore";
            if (types.Contains("Go") && Has("go.mod")) return "go mod download";
            if (types.Contains("Flutter") && Has("pubspec.yaml")) return "flutter pub get";
            if (types.Contains("Ruby") && Has("Gemfile")) return "bundle install";
            if (types.Contains("Java") && Has("pom.xml")) return "mvn install -DskipTests";
            if (types.Contains("C++") && Has("CMakeLists.txt")) return "cmake -S . -B build";
            return null;
        }

        // Runs through cmd.exe so .cmd/.bat tools such as npm work, in the project folder, showing its output
        static void RunShellCommand(string command, string workingDirectory)
        {
            LogVerbose($"Running {command}...");
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c " + command) { UseShellExecute = false, WorkingDirectory = workingDirectory };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return;
                    p.WaitForExit();
                    Log($"Setup command '{command}' exited with code {p.ExitCode}");

                    if (p.ExitCode == 0) WriteLineColor(ConsoleColor.Green, "Setup finished.");
                    else if (p.ExitCode == 9009) WriteLineColor(ConsoleColor.Yellow, $"'{command.Split(' ')[0]}' isn't installed (or isn't on PATH). Install it, then run '{command}' in the project folder.");
                    else WriteLineColor(ConsoleColor.Yellow, $"'{command}' finished with exit code {p.ExitCode}.");
                }
            }
            catch (Exception ex)
            {
                WriteLineColor(ConsoleColor.Red, $"Couldn't run '{command}': {ex.Message}");
            }
        }

        // =================================================================================
        // Identity & Credentials
        // =================================================================================

        static void CheckGitIdentity()
        {
            LogVerbose("Checking Git configuration...");

            // Effective values (system, global or this repository) - not just --global
            string name = RunGit(true, "config", "user.name").Output.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                WriteLineColor(ConsoleColor.Yellow, "Git user.name is not set (it's saved once, for all your projects).");
                string newName = "";
                while (newName == "") newName = GetUserInput("Enter your Name (shown on your commits): ");
                RunGit(false, "config", "--global", "user.name", newName);
            }

            string email = RunGit(true, "config", "user.email").Output.Trim();
            if (string.IsNullOrWhiteSpace(email))
            {
                WriteLineColor(ConsoleColor.Yellow, "Git user.email is not set.");
                Console.WriteLine("Use the email of your GitHub account (or the private 'noreply' one from GitHub > Settings > Emails).");
                Console.WriteLine("It is visible to anyone who can see your commits.");
                string newEmail = GetUserInput("Enter your Email: ");
                while (!newEmail.Contains('@')) newEmail = GetUserInput("That doesn't look like an email address (it needs an @). Enter your Email: ");
                RunGit(false, "config", "--global", "user.email", newEmail);
            }
        }

        static void CheckCredentialHelper()
        {
            string helper = RunGit(true, "config", "credential.helper").Output.Trim();
            if (string.IsNullOrWhiteSpace(helper) && RunGit(true, "credential-manager", "--version").Success)
            {
                LogVerbose("Setting credential helper to 'manager'...");
                RunGit(false, "config", "--global", "credential.helper", "manager");
            }
        }
    }
}
