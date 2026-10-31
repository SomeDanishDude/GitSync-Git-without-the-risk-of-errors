using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GitSync
{
    public class GitResult
    {
        public int ExitCode { get; set; } = -1;
        public string Output { get; set; } = "";
        public string Error { get; set; } = "";
        public bool Success => ExitCode == 0;
        public string AllText => (Output + "\n" + Error).Trim();
    }

    enum RemoteBranchState { Exists, Missing, Error }

    public partial class Program
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        // The git process that is running right now, so Ctrl+C / closing the window can wait for it before cleaning up
        private static Process? runningGit = null;

        // Absolute path of git.exe from PATH - never the current folder: CreateProcess searches the working directory
        // before PATH, so a "git.exe" inside a project or a cloned repository would otherwise run instead of the real one
        private static readonly Lazy<string> GitExecutable = new Lazy<string>(FindGitOnPath);

        static string FindGitOnPath()
        {
            foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string dir = entry.Trim().Trim('"');
                    if (!Path.IsPathFullyQualified(dir)) continue;
                    string candidate = Path.Combine(dir, "git.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return "git"; // not found: starting it fails and EnsureGitInstalled explains how to install Git
        }

        // "Writing objects:  42% (21/50)" style lines that git repaints in place
        private static readonly Regex ProgressLine = new Regex(@"^(remote: )?[A-Za-z][A-Za-z ]*:\s+\d{1,3}% \(\d+/\d+\)", RegexOptions.Compiled);
        private static bool progressLineOpen = false;

        // =================================================================================
        // Running git
        // =================================================================================

        // Runs git and captures its output; silent = false also prints it afterwards.
        static GitResult RunGit(bool silent, params string[] args)
        {
            return ExecuteGit(args, silent, live: false, input: null);
        }

        // Runs git and shows its output while it runs. Used for push/pull/fetch/clone/commit, which can take a while
        // and are where problems show up - so the user always sees what git says.
        static GitResult RunGitLive(params string[] args)
        {
            return ExecuteGit(args, silent: false, live: true, input: null);
        }

        static GitResult RunGitWithSpinner(params string[] args)
        {
            if (Console.IsOutputRedirected) return RunGit(true, args);

            var task = Task.Run(() => RunGit(true, args));
            var spinner = new[] { '|', '/', '-', '\\' };
            int counter = 0;

            TryConsole(() => Console.CursorVisible = false);
            try
            {
                while (!task.IsCompleted)
                {
                    lock (ConsoleLock)
                    {
                        Console.Write($"\rWorking... {spinner[counter++ % 4]}");
                    }
                    Thread.Sleep(100);
                }
            }
            finally
            {
                TryConsole(() => Console.CursorVisible = true);
            }

            var result = task.Result;
            lock (ConsoleLock)
            {
                Console.Write(result.Success ? "\rDone!          \n" : "\rFailed.        \n");
            }
            return result;
        }

        static GitResult ExecuteGit(string[] args, bool silent, bool live, string? input)
        {
            var result = new GitResult();
            var output = new StringBuilder();
            var error = new StringBuilder();

            try
            {
                using (var p = new Process { StartInfo = CreateGitStartInfo(args) })
                {
                    if (live)
                    {
                        p.OutputDataReceived += (s, e) => OnLiveLine(e.Data, output);
                        p.ErrorDataReceived += (s, e) => OnLiveLine(e.Data, error);
                    }

                    p.Start();
                    runningGit = p;

                    // Start draining stdout/stderr before writing stdin: a big input (cat-file --batch-check) would otherwise
                    // fill the pipes and deadlock both sides
                    Task<string>? outputTask = null, errorTask = null;
                    if (live)
                    {
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                    }
                    else
                    {
                        outputTask = p.StandardOutput.ReadToEndAsync();
                        errorTask = p.StandardError.ReadToEndAsync();
                    }

                    // Feed (or just close) stdin so git never sits waiting for input that will not come
                    try
                    {
                        if (input != null) p.StandardInput.Write(input);
                        p.StandardInput.Close();
                    }
                    catch (IOException) { }

                    if (outputTask != null && errorTask != null)
                    {
                        Task.WaitAll(outputTask, errorTask);
                        output.Append(outputTask.Result);
                        error.Append(errorTask.Result);
                    }
                    p.WaitForExit();

                    result.ExitCode = p.ExitCode;
                }
            }
            catch (Win32Exception ex)
            {
                error.Append($"Could not start git: {ex.Message}");
            }
            finally
            {
                runningGit = null;
                if (live) EndProgressLine();
            }

            result.Output = output.ToString();
            result.Error = error.ToString();

            if (!silent && !live)
            {
                if (!string.IsNullOrWhiteSpace(result.Output)) Console.WriteLine(result.Output.TrimEnd());
                if (!string.IsNullOrWhiteSpace(result.Error)) Console.WriteLine(result.Error.TrimEnd());
            }

            LogGitCommand(args, result, logOutput: live || !silent || !result.Success);
            return result;
        }

        // Like RunGit, but stdout comes back as raw bytes - for "cat-file --batch", whose sizes count bytes, not characters
        static (bool Success, byte[] Output) RunGitForBytes(string[] args, string input)
        {
            try
            {
                using (var p = new Process { StartInfo = CreateGitStartInfo(args) })
                {
                    p.Start();
                    runningGit = p;
                    var output = new MemoryStream();
                    var outputTask = p.StandardOutput.BaseStream.CopyToAsync(output);
                    var errorTask = p.StandardError.ReadToEndAsync();
                    try
                    {
                        p.StandardInput.Write(input);
                        p.StandardInput.Close();
                    }
                    catch (IOException) { }

                    Task.WaitAll(outputTask, errorTask);
                    p.WaitForExit();
                    LogGitCommand(args, new GitResult { ExitCode = p.ExitCode, Error = errorTask.Result }, logOutput: p.ExitCode != 0);
                    return (p.ExitCode == 0, output.ToArray());
                }
            }
            catch (Win32Exception)
            {
                return (false, Array.Empty<byte>());
            }
            finally
            {
                runningGit = null;
            }
        }

        static ProcessStartInfo CreateGitStartInfo(string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = GitExecutable.Value,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                // Keep the console attached: Git Credential Manager and SSH need it to ask for a login,
                // a key passphrase or a host-key confirmation. (CreateNoWindow = true made those fail.)
                CreateNoWindow = false,
                // Git writes UTF-8; without this, æøå in file names and messages come out garbled
                StandardOutputEncoding = Utf8NoBom,
                StandardErrorEncoding = Utf8NoBom,
            };

            // Show non-ASCII file names as-is instead of "\303\246" escapes
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("core.quotepath=false");

            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }
            return psi;
        }

        // Ctrl+C reaches git too (same console), but it may still be writing into .git - let it finish before cleanup touches it
        static void StopRunningGit()
        {
            try
            {
                var git = runningGit;
                if (git != null && !git.WaitForExit(3000)) git.Kill(entireProcessTree: true);
            }
            catch { }
        }

        static void OnLiveLine(string? line, StringBuilder buffer)
        {
            if (line == null) return;

            bool isProgress = ProgressLine.IsMatch(line);
            bool finished = line.Contains("done", StringComparison.OrdinalIgnoreCase);
            if (!isProgress || finished) buffer.AppendLine(line);

            lock (ConsoleLock)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                if (isProgress && !Console.IsOutputRedirected)
                {
                    // Repaint progress on one line instead of printing hundreds of them
                    int width = Math.Max(20, SafeWindowWidth() - 1);
                    string text = "    " + line;
                    if (text.Length > width) text = text.Substring(0, width);
                    Console.Write("\r" + text.PadRight(width));
                    progressLineOpen = !finished;
                    if (finished) Console.WriteLine();
                }
                else if (!isProgress || finished)
                {
                    if (progressLineOpen)
                    {
                        Console.WriteLine();
                        progressLineOpen = false;
                    }
                    Console.WriteLine("    " + line);
                }
                Console.ResetColor();
            }
        }

        static void EndProgressLine()
        {
            lock (ConsoleLock)
            {
                if (progressLineOpen)
                {
                    Console.WriteLine();
                    progressLineOpen = false;
                }
            }
        }

        static void LogGitCommand(string[] args, GitResult result, bool logOutput)
        {
            string command = "git " + string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            var sb = new StringBuilder($"$ {MaskUrl(command)}  (exit {result.ExitCode})");
            string text = logOutput ? MaskUrl(result.AllText.Replace('\0', ' ')) : "";
            if (text.Length > 4000) text = text.Substring(0, 4000) + "\n... (truncated)";
            foreach (var line in text.Split('\n').Where(l => l.Trim().Length > 0))
            {
                sb.Append(Environment.NewLine).Append("        ").Append(line.TrimEnd('\r'));
            }
            Log(sb.ToString());
        }

        static bool EnsureGitInstalled()
        {
            var version = RunGit(true, "--version");
            if (version.Success)
            {
                LogVerbose(version.Output.Trim());
                return true;
            }

            WriteLineColor(ConsoleColor.Red, "\nGit is not installed (or not on PATH), so GitSync can't do anything yet.");
            Console.WriteLine("Install it from https://git-scm.com/download/win");
            Console.WriteLine("(or in a terminal: winget install --id Git.Git -e), then start GitSync again.");
            return false;
        }

        // =================================================================================
        // Explaining failures
        // =================================================================================

        private static readonly Regex AuthFailure = new Regex(
            @"Authentication failed|could not read Username|could not read Password|Invalid username or (password|token)|" +
            @"Support for password authentication was removed|terminal prompts disabled|HTTP Basic: Access denied",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RejectedNonFastForward = new Regex(
            @"\[rejected\].*\((fetch first|non-fast-forward)\)|Updates were rejected because",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // First match wins, so the specific patterns come before the general ones
        private static readonly (Regex Pattern, string Hint)[] GitHints =
        {
            (new Regex(@"src refspec .+ does not match any", RegexOptions.IgnoreCase),
                "The branch you tried to push doesn't exist on this computer (or has no commits yet)."),
            (new Regex(@"detected dubious ownership", RegexOptions.IgnoreCase),
                "Git doesn't trust this folder because it belongs to another Windows user (common on USB or external drives)."),
            (AuthFailure,
                "The login was rejected. GitHub no longer accepts your account password for Git - sign in through the browser " +
                "window that Git Credential Manager opens, or use a personal access token instead of the password."),
            (new Regex(@"Permission to .+ denied|The requested URL returned error: 403", RegexOptions.IgnoreCase),
                "You are signed in, but that account isn't allowed to push to this repository. Check that it's the right " +
                "GitHub account, or ask the owner to add you as a collaborator."),
            (new Regex(@"Repository not found|does not appear to be a git repository|returned error: 404", RegexOptions.IgnoreCase),
                "The remote repository wasn't found. Check the URL for typos, create the repository on GitHub first, and make " +
                "sure you're signed in with an account that can see it (private repositories look 'not found' to other accounts)."),
            (new Regex(@"Host key verification failed", RegexOptions.IgnoreCase),
                "SSH doesn't know this server yet. Open a terminal, run 'ssh -T git@github.com' once and answer 'yes' " +
                "- or use the HTTPS URL instead."),
            (new Regex(@"Permission denied \(publickey", RegexOptions.IgnoreCase),
                "Your SSH key isn't set up for this server. Use the HTTPS URL instead, or add your public key on GitHub " +
                "(Settings > SSH and GPG keys)."),
            (new Regex(@"Could not resolve host|Failed to connect|Connection timed out|Connection refused|Network is unreachable|Operation timed out", RegexOptions.IgnoreCase),
                "Couldn't reach the server. Check your internet connection, VPN or proxy, then try again."),
            (new Regex(@"SSL certificate problem|unable to get local issuer certificate", RegexOptions.IgnoreCase),
                "A secure-connection (SSL) problem, often caused by antivirus software or a company proxy. This usually fixes it: " +
                "git config --global http.sslBackend schannel"),
            (RejectedNonFastForward,
                "The remote has commits that you don't have yet. They have to be merged in before you can push."),
            (new Regex(@"GH001|exceeds GitHub's file size limit|Large files detected", RegexOptions.IgnoreCase),
                "A file over 100 MB is inside the commits you're pushing, and GitHub refuses it. Deleting it in a new commit isn't " +
                "enough (the old commit still has it) - run Sync again and GitSync offers to move it into Git LFS."),
            (new Regex(@"GH007|would publish a private email", RegexOptions.IgnoreCase),
                "GitHub blocked the push because your commit email is set to private. Use the 'noreply' email shown on " +
                "GitHub > Settings > Emails as your Git email."),
            (new Regex(@"GH006|protected branch", RegexOptions.IgnoreCase),
                "This branch is protected on the server. Push to a new branch instead and open a pull (merge) request."),
            (new Regex(@"\[remote rejected\]|hook declined", RegexOptions.IgnoreCase),
                "The server itself refused the push (a rule set up on the remote). Its reason is in the 'remote:' lines above."),
            (new Regex(@"does not have a commit checked out|adding embedded git repository", RegexOptions.IgnoreCase),
                "A folder inside your project is a separate Git repository. Skip it (add it to .gitignore), or delete its " +
                ".git folder if its files should be uploaded as part of this project."),
            (new Regex(@"untracked working tree files would be overwritten", RegexOptions.IgnoreCase),
                "Files in your folder have the same names as files coming from the remote, and Git won't overwrite them."),
            (new Regex(@"Your local changes to the following files would be overwritten by checkout", RegexOptions.IgnoreCase),
                "Switching branches would overwrite your unsaved edits in these files. Press Enter at the branch question to sync them on the current branch first."),
            (new Regex(@"Your local changes to the following files would be overwritten", RegexOptions.IgnoreCase),
                "You have uncommitted changes in files that the incoming changes also modify. Commit them first."),
            (new Regex(@"Filename too long", RegexOptions.IgnoreCase),
                "A path is longer than Windows' old 260-character limit. GitSync turns on long-path support for the repositories it syncs - run it again."),
            (new Regex(@"open\("".*""\): Permission denied|unable to index file|Permission denied.*\.(docx?|xlsx?|pptx?)|The process cannot access the file", RegexOptions.IgnoreCase),
                "Another program has a file open (Word/Excel, OneDrive, antivirus...). Close it and try again."),
            (new Regex(@"refusing to merge unrelated histories", RegexOptions.IgnoreCase),
                "The remote repository was started separately (for example with a README on GitHub), so the histories must be joined."),
            (new Regex(@"Please tell me who you are|empty ident name|unable to auto-detect email", RegexOptions.IgnoreCase),
                "Git needs your name and email before it can commit."),
            (new Regex(@"index\.lock.*File exists|Another git process seems to be running", RegexOptions.IgnoreCase),
                "Another Git program is using this repository (or one crashed). Close other Git tools; if none are open, delete " +
                ".git\\index.lock and try again."),
            (new Regex(@"You have not concluded your merge|MERGE_HEAD exists|you need to resolve your current index first|Committing is not possible because you have unmerged files", RegexOptions.IgnoreCase),
                "A merge is still unfinished. Run Sync or Pull again and GitSync will help you finish or cancel it."),
            (new Regex(@"not a git repository", RegexOptions.IgnoreCase),
                "This folder isn't a Git repository."),
        };

        internal static string? GetFriendlyHint(string gitOutput)
        {
            // File lists (tab-indented names, "CONFLICT ... in <file>") must not trigger hints - a file can be called anything
            string text = string.Join("\n", gitOutput.Split('\n')
                .Where(l => !l.StartsWith("\t") && !l.TrimStart().StartsWith("CONFLICT") && !l.TrimStart().StartsWith("Auto-merging")));
            foreach (var (pattern, hint) in GitHints)
            {
                if (pattern.IsMatch(text)) return hint;
            }
            return null;
        }

        static bool IsAuthFailure(GitResult result) => AuthFailure.IsMatch(result.AllText);

        // Rejected logins and also the "signed in with the wrong account" cases: 403, and 404 for private repositories
        static bool IsAccountProblem(GitResult result) =>
            IsAuthFailure(result) || Regex.IsMatch(result.AllText, @"Permission to .+ denied|returned error: 403|Repository not found", RegexOptions.IgnoreCase);
        static bool IsRejectedNonFastForward(GitResult result) => RejectedNonFastForward.IsMatch(result.AllText);

        // Prints why a git command failed: git's own words (unless they were already shown live) plus a plain-language hint.
        static void ExplainGitFailure(GitResult result, string action, bool outputAlreadyShown = false)
        {
            WriteLineColor(ConsoleColor.Red, $"\n{action} failed.");
            if (!outputAlreadyShown)
            {
                var lines = result.AllText.Split('\n')
                    .Select(l => l.TrimEnd('\r'))
                    .Where(l => l.Trim().Length > 0)
                    .TakeLast(12)
                    .ToList();
                if (lines.Count > 0)
                {
                    WriteLineColor(ConsoleColor.DarkGray, "Git said:");
                    foreach (var line in lines) WriteLineColor(ConsoleColor.DarkGray, "    " + MaskUrl(line));
                }
            }

            string? hint = GetFriendlyHint(result.AllText);
            if (hint != null) WriteLineColor(ConsoleColor.Yellow, "What this means: " + hint);
            WriteLineColor(ConsoleColor.DarkGray, $"(Details are saved in the log: {LogFile})");
        }

        // An SSH address (git@github.com:...) needs an SSH key registered with GitHub. When that's the problem, the HTTPS
        // address of the same repository works with a normal browser sign-in. Returns that address, or null.
        internal static string? HttpsAlternativeFor(GitResult result, string url)
        {
            if (IsHttpUrl(url) || !Regex.IsMatch(result.AllText, @"Permission denied \(publickey|Host key verification failed", RegexOptions.IgnoreCase)) return null;
            string? web = ToWebUrl(url);
            if (web == null) return null;
            // "github-work" (a ~/.ssh/config alias) or ssh.dev.azure.com aren't the site's web address, so a guess would be wrong
            string host = new Uri(web).Host;
            if (!host.Contains('.') || host.StartsWith("ssh.", StringComparison.OrdinalIgnoreCase) || host.StartsWith("vs-ssh.", StringComparison.OrdinalIgnoreCase)) return null;
            return web + ".git";
        }

        // Offers to switch origin from SSH to HTTPS after an SSH key problem - only once that address has answered, so a
        // working SSH setup is never swapped for a wrong guess. Returns true if it switched.
        static bool OfferHttpsInsteadOfSsh(GitResult result, string url)
        {
            string? https = HttpsAlternativeFor(result, url);
            if (https == null || !AskYesNo($"Switch to the HTTPS address ({https}) and sign in through the browser instead?", true)) return false;
            Console.WriteLine($"Connecting to {https} (a sign-in window may pop up)...");
            var probe = RunGit(true, "ls-remote", "--heads", https);
            if (!probe.Success)
            {
                ExplainGitFailure(probe, "Connecting to the HTTPS address");
                Console.WriteLine("Keeping the SSH address.");
                return false;
            }
            RunGit(true, "remote", "set-url", "origin", https);
            LogVerbose($"Remote 'origin' now uses {https}");
            return true;
        }

        // Git refuses folders owned by another Windows user (USB drives, copied folders). Offers the standard fix.
        static bool EnsureFolderTrusted(string folder)
        {
            var probe = RunGit(true, "rev-parse", "--is-inside-work-tree");
            if (!probe.Error.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase)) return true;
            return OfferSafeDirectoryFor(probe, folder);
        }

        // For any "dubious ownership" failure - this folder, or e.g. a shared remote repository on a NAS - names the
        // folder git complained about and offers to mark it as safe. Returns true if it did (so the step can be retried).
        static bool OfferSafeDirectoryFor(GitResult result, string? fallbackFolder = null)
        {
            if (!result.AllText.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase)) return false;

            var match = Regex.Match(result.AllText, @"safe\.directory\s+'?([^'\r\n]+?)'?\s*$", RegexOptions.Multiline);
            string? path = match.Success ? match.Groups[1].Value.Trim() : fallbackFolder?.Replace('\\', '/');
            if (path == null) return false;

            WriteLineColor(ConsoleColor.Yellow, $"\nGit doesn't trust '{path}' because it belongs to another Windows user (common on USB drives and network shares).");
            if (!AskYesNo($"Mark '{path}' as safe for Git?", true)) return false;
            RunGit(true, "config", "--global", "--add", "safe.directory", path);
            return true;
        }

        // Asks Git Credential Manager to forget the stored login (for one account, when git named it), so the next
        // attempt asks again. git works out protocol, host, port and path from the URL itself.
        static void ForgetSavedLogin(string remoteUrl, string? userName = null)
        {
            if (!IsHttpUrl(remoteUrl)) return;
            string input = $"url={remoteUrl}\n" + (userName != null ? $"username={userName}\n" : "") + "\n";
            ExecuteGit(new[] { "credential", "reject" }, silent: true, live: false, input: input);
            LogVerbose("Cleared the saved login for this site (if there was one). You'll be asked to sign in again.");
        }

        // Offers to sign in again (or with another account) after an HTTPS login/permission problem. True = retry.
        static bool OfferSignInAgain(GitResult result, string url)
        {
            if (!IsHttpUrl(url) || !IsAccountProblem(result)) return false;

            var deniedTo = Regex.Match(result.AllText, @"denied to ([^\s.]+)");
            string? user = deniedTo.Success ? deniedTo.Groups[1].Value : null;
            string question = user != null
                ? $"You're signed in as '{user}'. Sign in with a different account?"
                : "Sign in again (maybe with a different account)?";
            if (!AskYesNo(question, true)) return false;

            ForgetSavedLogin(url, user);
            Console.WriteLine("If the browser signs you straight back in with the wrong account, switch accounts on github.com first.");
            return true;
        }

        // =================================================================================
        // Repository state
        // =================================================================================

        // Current branch name, or "" when HEAD is detached. Unlike "rev-parse --abbrev-ref HEAD"
        // this also works in a brand-new repository that has no commits yet.
        internal static string GetCurrentBranch()
        {
            var result = RunGit(true, "symbolic-ref", "--quiet", "--short", "HEAD");
            return result.Success ? result.Output.Trim() : "";
        }

        static bool HasCommits() => RunGit(true, "rev-parse", "--verify", "--quiet", "HEAD").Success;

        // Purely syntactic check, so it also works before "git init"
        internal static bool IsValidBranchName(string name) =>
            name.Length > 0 && !name.StartsWith("-") && name != "HEAD" && RunGit(true, "check-ref-format", "refs/heads/" + name).Success;

        static bool BranchExists(string name) => RunGit(true, "show-ref", "--verify", "--quiet", "refs/heads/" + name).Success;

        // Git branch names are case-sensitive, but Windows keeps them as files: "Main" next to "main" is the same file here
        // and a second branch on GitHub. A typed name that only differs in case from an existing branch means that branch.
        internal static string MatchExistingBranchSpelling(string name)
        {
            string? match = FindBranchSpelledLike(name);
            if (match == null || match == name) return name;
            LogVerbose($"Using the existing branch '{match}' (Git tells '{name}' and '{match}' apart, Windows doesn't).");
            return match;
        }

        // The exact spelling of a branch named like this (ignoring case) here or on the remote, or null if there is none
        static string? FindBranchSpelledLike(string name)
        {
            var known = RunGit(true, "for-each-ref", "--format=%(refname:strip=2)", "refs/heads/").Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(b => b.Trim())
                .Concat(GetRemoteTrackingRefs().Select(r => r.Substring("refs/remotes/origin/".Length)))
                .ToList();
            string? match = known.Contains(name) ? name : known.FirstOrDefault(b => string.Equals(b, name, StringComparison.OrdinalIgnoreCase));
            // A branch the remote got since the last fetch counts too
            if (match == null && HasRemote("origin"))
            {
                var remote = GetRemoteBranchNames() ?? new List<string>();
                match = remote.Contains(name) ? name : remote.FirstOrDefault(b => string.Equals(b, name, StringComparison.OrdinalIgnoreCase));
            }
            return match;
        }

        static bool IsInsideRepository()
        {
            var result = RunGit(true, "rev-parse", "--is-inside-work-tree");
            return result.Success && result.Output.Trim() == "true";
        }

        static string GetRepositoryRoot()
        {
            var result = RunGit(true, "rev-parse", "--show-toplevel");
            return result.Success ? Path.GetFullPath(result.Output.Trim()) : Environment.CurrentDirectory;
        }

        static string GetGitDir()
        {
            var result = RunGit(true, "rev-parse", "--absolute-git-dir");
            return result.Success ? Path.GetFullPath(result.Output.Trim()) : "";
        }

        static bool IsMergeInProgress()
        {
            string gitDir = GetGitDir();
            return gitDir != "" && File.Exists(Path.Combine(gitDir, "MERGE_HEAD"));
        }

        static bool IsRebaseInProgress()
        {
            string gitDir = GetGitDir();
            return gitDir != "" && (Directory.Exists(Path.Combine(gitDir, "rebase-merge")) || Directory.Exists(Path.Combine(gitDir, "rebase-apply")));
        }

        static string GetRemoteUrl() => RunGit(true, "remote", "get-url", "origin").Output.Trim();

        static List<string> GetConflictedFiles() => SplitNul(RunGit(true, "diff", "--name-only", "--diff-filter=U", "-z").Output).Distinct().ToList();

        static (int Ahead, int Behind) GetAheadBehind(string otherRef)
        {
            return ParseAheadBehind(RunGit(true, "rev-list", "--left-right", "--count", $"HEAD...{otherRef}").Output);
        }

        internal static (int Ahead, int Behind) ParseAheadBehind(string text)
        {
            var parts = text.Split(new[] { '\t', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0], out int ahead) && int.TryParse(parts[1], out int behind)) return (ahead, behind);
            return (0, 0);
        }

        // ls-remote --exit-code: 0 = branch found, 2 = no such branch, anything else = couldn't reach/read the remote.
        // (The old check treated an error message as "branch found".)
        internal static RemoteBranchState CheckRemoteBranch(string branch, out GitResult result)
        {
            result = RunGit(true, "ls-remote", "--exit-code", "--heads", "origin", "refs/heads/" + branch);
            if (result.ExitCode == 0) return RemoteBranchState.Exists;
            if (result.ExitCode == 2) return RemoteBranchState.Missing;
            return RemoteBranchState.Error;
        }

        static GitResult FetchBranch(string branch, bool quiet = false)
        {
            // Explicit refspec, so origin/<branch> is updated even if the remote's fetch settings are unusual
            string refspec = $"+refs/heads/{branch}:refs/remotes/origin/{branch}";
            return quiet ? RunGit(true, "fetch", "--quiet", "origin", refspec) : RunGitLive("fetch", "--progress", "origin", refspec);
        }

        // The branch the remote's page shows by default (GitHub: usually "main"), or null for an empty remote. A shared
        // repository made with plain "git init --bare" can point HEAD at a "master" that doesn't exist while the team
        // pushes "main" - then the branches that do exist decide.
        static string? GetRemoteDefaultBranch()
        {
            var result = RunGit(true, "ls-remote", "--symref", "origin", "HEAD");
            if (!result.Success) return null;
            var match = Regex.Match(result.Output, @"^ref: refs/heads/(\S+)\s+HEAD", RegexOptions.Multiline);
            if (match.Success) return match.Groups[1].Value;

            var heads = GetRemoteBranchNames() ?? new List<string>();
            return heads.Count == 1 ? heads[0] : heads.Contains("main") ? "main" : heads.Contains("master") ? "master" : null;
        }

        // The branch names the remote has right now, or null if it couldn't be asked
        static List<string>? GetRemoteBranchNames()
        {
            var result = RunGit(true, "ls-remote", "--heads", "origin");
            if (!result.Success) return null;
            return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\t').ElementAtOrDefault(1)?.Trim() ?? "")
                .Where(r => r.StartsWith("refs/heads/"))
                .Select(r => r.Substring("refs/heads/".Length))
                .ToList();
        }

        // refs/remotes/origin/* - what this computer knows the remote has (without the HEAD pointer)
        static List<string> GetRemoteTrackingRefs()
        {
            return RunGit(true, "for-each-ref", "--format=%(refname)", "refs/remotes/origin/").Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(r => r.Trim())
                .Where(r => r.Length > 0 && r != "refs/remotes/origin/HEAD")
                .ToList();
        }

        static void DeleteRefs(List<string> refs)
        {
            if (refs.Count == 0) return;
            ExecuteGit(new[] { "update-ref", "--stdin" }, silent: true, live: false, input: string.Concat(refs.Select(r => $"delete {r}\n")));
            // An origin/HEAD pointing at a branch that's gone would make git print warnings
            if (!RunGit(true, "rev-parse", "--verify", "--quiet", "refs/remotes/origin/HEAD").Success)
            {
                RunGit(true, "symbolic-ref", "--delete", "--quiet", "refs/remotes/origin/HEAD");
            }
        }

        // Before a push: origin/* made to match what the remote has right now. Branches deleted there are dropped, renamed
        // and rewritten (force-pushed) ones are brought up to date - so "already on the remote" is true for the checks and
        // rebuilds that rely on it. The explicit refspec also covers clones made with --single-branch.
        static void RefreshRemoteBranches()
        {
            var fetch = RunGit(true, "fetch", "--quiet", "--prune", "origin", "+refs/heads/*:refs/remotes/origin/*");
            if (!fetch.Success) return; // couldn't reach it: keep what we know - the push itself explains the problem
            // An origin/HEAD pointing at a branch that's gone would make git print warnings
            if (!RunGit(true, "rev-parse", "--verify", "--quiet", "refs/remotes/origin/HEAD").Success)
            {
                RunGit(true, "symbolic-ref", "--delete", "--quiet", "refs/remotes/origin/HEAD");
            }
        }

        // After origin was pointed at a different repository: nothing fetched from the old one is "on the remote" any more
        static void ForgetRemoteBranches()
        {
            DeleteRefs(GetRemoteTrackingRefs());
            RunGit(true, "symbolic-ref", "--delete", "--quiet", "refs/remotes/origin/HEAD");
        }

        // Two addresses of the same repository (SSH and HTTPS, with or without ".git" or a login in it)
        internal static bool SameRepository(string a, string b)
        {
            static string Key(string url)
            {
                string? web = ToWebUrl(url);
                if (web != null) return web.TrimEnd('/');
                try { return Path.GetFullPath(url.Trim()).TrimEnd('\\', '/'); } catch { return url.Trim(); }
            }
            return string.Equals(Key(a), Key(b), StringComparison.OrdinalIgnoreCase);
        }

        // "origin/main" for a branch that is set up to sync with origin, otherwise ""
        static string GetUpstream()
        {
            var result = RunGit(true, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
            string upstream = result.Output.Trim();
            return result.Success && upstream.StartsWith("origin/") ? upstream : "";
        }

        static string? GetUpstreamBranchName()
        {
            string upstream = GetUpstream();
            return upstream == "" ? null : upstream.Substring("origin/".Length);
        }

        // Commits on this branch that no remote branch has yet
        static int CountUnpushedCommits()
        {
            return int.TryParse(RunGit(true, "rev-list", "--count", "HEAD", "--not", "--remotes=origin").Output.Trim(), out int count) ? count : 0;
        }

        // Commits a push adds to <branch> on the remote. Compared with that branch when it's there - commits that already
        // sit on another remote branch (a merged feature branch) still have to go to this one. When it isn't, an old
        // origin/<branch> left from before (deleted on the remote, or a recreated repository) doesn't count.
        internal static int CountCommitsToPush(string branch, bool remoteBranchExists)
        {
            string remoteRef = $"refs/remotes/origin/{branch}";
            if (remoteBranchExists && RunGit(true, "show-ref", "--verify", "--quiet", remoteRef).Success) return GetAheadBehind(remoteRef).Ahead;
            var count = RunGit(true, "rev-list", "--count", "HEAD", "--not", "--exclude=origin/HEAD", $"--exclude=origin/{branch}", "--remotes=origin");
            return int.TryParse(count.Output.Trim(), out int n) ? n : 0;
        }

        // Whether any of this branch's history is on the remote already (false for a project that was never pushed)
        static bool HasPublishedHistory()
        {
            int all = int.TryParse(RunGit(true, "rev-list", "--count", "HEAD").Output.Trim(), out int count) ? count : 0;
            return all > 0 && CountUnpushedCommits() < all;
        }

        // The newest commit of HEAD's history that the remote already has: what a rebuild of the unpushed commits must sit
        // on, so nothing that is published gets rewritten or left out. baseCommit = null: none of it is on the remote.
        // Returns false when there is no single such commit (published work from different branches was merged in).
        internal static bool TryGetPublishedBase(out string? baseCommit)
        {
            baseCommit = null;
            var lines = RunGit(true, "rev-list", "--boundary", "HEAD", "--not", "--remotes=origin").Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .ToList();
            if (!lines.Any(l => !l.StartsWith("-")))
            {
                baseCommit = CurrentHead(); // nothing unpushed
                return true;
            }

            var boundary = lines.Where(l => l.StartsWith("-")).Select(l => l.Substring(1)).Distinct().ToList();
            if (boundary.Count == 0) return true;
            // After a merge of the remote branch the boundary is its tip plus the old fork point, which the tip contains
            var newest = boundary.Where(b => boundary.All(o => o == b || RunGit(true, "merge-base", "--is-ancestor", o, b).Success)).ToList();
            if (newest.Count != 1) return false;
            baseCommit = newest[0];
            return true;
        }

        // Which versions of a conflicted file exist: 1 = common ancestor, 2 = local ("ours"), 3 = remote ("theirs")
        static HashSet<int> GetConflictStages(string file)
        {
            var stages = new HashSet<int>();
            foreach (var entry in SplitNul(RunGit(true, "--literal-pathspecs", "ls-files", "-u", "-z", "--", file).Output))
            {
                var parts = entry.Split('\t')[0].Split(' ');
                if (parts.Length >= 3 && int.TryParse(parts[2], out int stage)) stages.Add(stage);
            }
            return stages;
        }

        // Files that "git add -A" would commit: tracked files plus new files that aren't ignored (repository-relative, "/" separated)
        static List<string> GetFilesToCommit()
        {
            var result = RunGit(true, "ls-files", "-z", "--cached", "--others", "--exclude-standard");
            return result.Success ? SplitNul(result.Output).Distinct().ToList() : new List<string>();
        }

        static HashSet<string> GetTrackedFiles()
        {
            return new HashSet<string>(SplitNul(RunGit(true, "ls-files", "-z", "--cached").Output), StringComparer.OrdinalIgnoreCase);
        }

        // "added    src/app.js" style lines for everything that is staged
        static List<string> GetStagedChanges()
        {
            var lines = RunGit(true, "diff", "--cached", "--name-status").Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var changes = new List<string>();
            foreach (var raw in lines)
            {
                var parts = raw.TrimEnd('\r').Split('\t');
                if (parts.Length < 2) continue;
                string kind = parts[0].Length > 0 ? parts[0].Substring(0, 1) : "?";
                string label = kind switch { "A" => "added   ", "M" => "modified", "D" => "deleted ", "R" => "renamed ", "C" => "copied  ", "T" => "changed ", _ => "changed " };
                changes.Add(parts.Length >= 3 ? $"{label} {parts[1]} -> {parts[2]}" : $"{label} {parts[1]}");
            }
            return changes;
        }

        // "XY path" entries from git status, with paths unquoted (-z) and a rename's old name dropped
        internal static List<string> GetWorkingTreeChanges()
        {
            var entries = SplitNul(RunGit(true, "status", "--porcelain", "-z").Output);
            var changes = new List<string>();
            for (int i = 0; i < entries.Count; i++)
            {
                string entry = entries[i];
                if (entry.Length < 4) continue;
                changes.Add(entry);
                // -z puts the original name of a rename in the next entry - also for one in the working tree (" R")
                if (entry[0] is 'R' or 'C' || entry[1] is 'R' or 'C') i++;
            }
            return changes;
        }

        // Tracked files with uncommitted changes, new files that aren't ignored, and files already staged with "git add"
        // (which --modified leaves out, because it compares with the staging area) - what a commit would add now
        static List<string> GetChangedFiles()
        {
            var unstaged = SplitNul(RunGit(true, "ls-files", "-z", "--modified", "--others", "--exclude-standard").Output);
            var staged = SplitNul(RunGit(true, "diff", "--cached", "--name-only", "-z", "--diff-filter=d").Output);
            return unstaged.Concat(staged).Distinct().ToList();
        }

        // Tracked files that an ignore rule matches (they're committed anyway, e.g. added before the rule, or with -f)
        static HashSet<string> GetTrackedIgnoredFiles()
        {
            return new HashSet<string>(SplitNul(RunGit(true, "ls-files", "-z", "--cached", "--ignored", "--exclude-standard").Output));
        }

        // A small text file that stands in for a file stored in Git LFS ("version https://git-lfs.github.com/spec/v1 ...")
        static bool IsLfsPointerFile(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists && info.Length < 1024 && File.ReadAllText(path).StartsWith("version https://git-lfs.github.com/spec/");
            }
            catch
            {
                return false;
            }
        }

        // Files over GitHub's 100 MiB limit inside commits that haven't been pushed yet. GitHub rejects the whole push for
        // one of these, and deleting the file in a later commit doesn't help because the earlier commit still holds it.
        static List<(string Path, long Size)> FindLargeBlobsToPush()
        {
            var objects = RunGit(true, "rev-list", "--objects", "HEAD", "--not", "--remotes=origin");
            if (!objects.Success || objects.Output.Trim().Length == 0) return new List<(string Path, long Size)>();

            var sizes = ExecuteGit(new[] { "cat-file", "--batch-check=%(objecttype) %(objectsize) %(rest)" }, silent: true, live: false, input: objects.Output);
            var large = new List<(string Path, long Size)>();
            foreach (var line in sizes.Output.Split('\n'))
            {
                var parts = line.TrimEnd('\r').Split(' ', 3);
                if (parts.Length == 3 && parts[0] == "blob" && long.TryParse(parts[1], out long size) && size > 100L * 1024 * 1024)
                {
                    large.Add((parts[2], size));
                }
            }
            return large.Distinct().ToList();
        }

        // A repository just created on GitHub (with "Add a README / .gitignore / license" ticked) holds only those starter files
        static bool LooksLikeFreshRemote(string remoteRef)
        {
            int commits = int.TryParse(RunGit(true, "rev-list", "--count", remoteRef).Output.Trim(), out int count) ? count : int.MaxValue;
            var files = RunGit(true, "ls-tree", "-r", "--name-only", remoteRef).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim());
            return commits <= 3 && files.All(f => !f.Contains('/') &&
                Regex.IsMatch(f, @"^((readme|license|licence|copying)(\.\w+)?|\.gitignore|\.gitattributes)$", RegexOptions.IgnoreCase));
        }

        static void ShowIncomingCommits(string remoteRef)
        {
            var log = RunGit(true, "log", "--oneline", "--no-decorate", "-n", "10", $"HEAD..{remoteRef}");
            foreach (var line in log.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                WriteLineColor(ConsoleColor.DarkGray, "    " + line.TrimEnd('\r'));
            }
        }

        static List<string> SplitNul(string text) => text.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();

        // =================================================================================
        // URLs
        // =================================================================================

        internal static bool LooksLikeGitUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (Regex.IsMatch(url, @"^(https?|ssh|git|file)://\S+$", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(url, @"^[A-Za-z0-9._\-]+@[A-Za-z0-9._\-]+:\S+$")) return true; // git@github.com:user/repo.git
            // A local or network folder, e.g. a bare repository on a shared drive
            return IsExistingFolderPath(url);
        }

        internal static bool IsHttpUrl(string url) => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

        // The page to open in a browser for a remote: https://github.com/user/repo for both the HTTPS and the SSH form
        internal static string? ToWebUrl(string remoteUrl)
        {
            string url = remoteUrl.Trim();
            var scp = Regex.Match(url, @"^[A-Za-z0-9._\-]+@([A-Za-z0-9._\-]+):(?!//)(.+)$");
            if (scp.Success && !url.Contains("://"))
            {
                url = $"https://{scp.Groups[1].Value}/{scp.Groups[2].Value.TrimStart('/')}";
            }
            else if (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("git://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var sshUri)) return null;
                url = $"https://{sshUri.Host}{sshUri.AbsolutePath}"; // the SSH port isn't the web port
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsHttpUrl(url)) return null;
            string path = uri.AbsolutePath.TrimEnd('/');
            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path.Substring(0, path.Length - 4);
            return $"{uri.Scheme}://{uri.Authority}{path}"; // Authority leaves out any user:token@ part
        }

        // "https://github.com/user/my-repo.git/" -> "my-repo"
        internal static string GetRepoNameFromUrl(string url)
        {
            string trimmed = url.Trim().TrimEnd('/', '\\');
            if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed.Substring(0, trimmed.Length - 4);
            int slash = trimmed.LastIndexOfAny(new[] { '/', '\\', ':' });
            string name = slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "repository" : name;
        }

        // Hides tokens/passwords embedded in URLs (https://user:token@host/...) before showing or logging them
        internal static string MaskUrl(string text) => Regex.Replace(text, @"(?<=\b[a-zA-Z][a-zA-Z0-9+.\-]*://)[^/@\s]+@", "***@");
    }
}
