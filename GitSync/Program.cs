using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace GitSync
{
    public class ProjectMetadata
    {
        public string Name { get; set; } = "MyProject";
        public string Description { get; set; } = "A new project.";
        public string Version { get; set; } = "0.1.0";
        public bool HasVersion { get; set; } = false; // true when Version came from the project's own files
        public string RunCommand { get; set; } = "";
    }

    // Thrown when the user types abort/cancel/exit or picks an Abort option.
    // It cancels the current mode (with cleanup) and returns to the main menu.
    public class UserAbortException : Exception
    {
        public UserAbortException(string message = "Process Aborted by User.") : base(message) { }
    }

    public partial class Program
    {
        private static readonly object ConsoleLock = new object();
        private static readonly object CleanupLock = new object();
        private static HashSet<string> autoIgnoreExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Track session state for cleanup
        private static string? sessionFolder = null;
        private static bool gitInitialized = false;
        private static bool keepChanges = false;
        private static bool remoteMerged = false;
        private static List<string> createdFiles = new List<string>();
        private static Dictionary<string, byte[]> modifiedFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, byte[]> writtenContent = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, List<string>> appendedText = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string> userEditedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // edited between two GitSync writes
        private static (string Path, byte[]? Content)? indexBackup = null;
        private static string headAtIndexBackup = "";
        private static bool sessionMergeStarted = false;           // a merge GitSync started is still unfinished
        private static string? branchToRestore = null;             // the branch the user was on before GitSync switched
        private static string? branchCreatedThisSession = null;
        private static HashSet<string> secretBlobsAccepted = new HashSet<string>(); // exact file versions the user called safe

        // Track statistics for summary
        private static int filesIgnoredCount = 0;
        private static int lfsTrackedCount = 0;
        private static List<string> detectedProjectTypes = new List<string>();
        private static string secretsStatus = "Clean";

        // Folder given on the command line (or dropped onto GitSync.exe); offered first in the folder picker
        private static string? startupFolder = null;

        // Log and recent-folder list live in %LOCALAPPDATA%\GitSync (GITSYNC_DATA_DIR overrides it)
        private static string DataDir
        {
            get
            {
                string? custom = Environment.GetEnvironmentVariable("GITSYNC_DATA_DIR");
                return !string.IsNullOrWhiteSpace(custom)
                    ? custom
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitSync");
            }
        }
        private static string LogFile => Path.Combine(DataDir, "gitsync_log.txt");
        private static string RecentFile => Path.Combine(DataDir, "recent_folders.txt");

        [STAThread]
        static void Main(string[] args)
        {
            // Programs started by name (cmd, notepad, tools git runs) must never be picked up from the project folder
            Environment.SetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", "1");
            UseUtf8Console();

            TryConsole(() => Console.Title = "GitSync (C#)");
            WriteLineColor(ConsoleColor.Cyan, "==========================================");
            WriteLineColor(ConsoleColor.Cyan, "   GitSync - Git but without pain added   ");
            WriteLineColor(ConsoleColor.Cyan, "==========================================");

            // Hook process exit for cleanup on close
            AppDomain.CurrentDomain.ProcessExit += (s, e) => {
                StopRunningGit();
                PerformCleanup();
                RestoreConsoleEncoding();
            };
            Console.CancelKeyPress += (s, e) => {
                StopRunningGit();
                PerformCleanup();
                e.Cancel = false; // Allow termination
            };

            if (args.Length > 0 && Directory.Exists(args[0])) startupFolder = Path.GetFullPath(args[0]);
            RotateLog();

            if (!EnsureGitInstalled())
            {
                PressAnyKey("Press any key to exit...");
                return;
            }

            while (true)
            {
                Console.WriteLine("\nChoose a mode:");
                Console.WriteLine("[1] Sync Local Folder (Init/Commit/Push)");
                Console.WriteLine("[2] Pull Latest Changes");
                Console.WriteLine("[3] Clone & Setup (Startup)");
                Console.WriteLine("[4] Status (am I up to date?)");
                Console.WriteLine("[5] Open Log File");
                Console.WriteLine("[6] Exit");

                string choice;
                try
                {
                    choice = GetUserInput("Choice: ");
                }
                catch (UserAbortException)
                {
                    return; // "exit" at the main menu closes GitSync
                }

                if (choice == "1") RunMode("Sync", HandleLocalSync);
                else if (choice == "2") RunMode("Pull", HandlePull);
                else if (choice == "3") RunMode("Clone", HandleClone);
                else if (choice == "4") RunMode("Status", HandleStatus);
                else if (choice == "5") OpenLogFile();
                else if (choice == "6") return;
                else if (choice != "") Console.WriteLine("Please choose 1-6.");
            }
        }

        // Runs one menu mode. Errors and aborts end the mode (after cleanup) and return to the menu instead of closing the window.
        static void RunMode(string name, Action mode)
        {
            ResetSession();
            Log($"===== {name} started =====");
            try
            {
                mode();
            }
            catch (UserAbortException ex)
            {
                WriteLineColor(ConsoleColor.Red, $"\n{ex.Message.Trim()}");
                Log($"{name}: {ex.Message.Trim()}");
            }
            catch (Exception ex)
            {
                WriteLineColor(ConsoleColor.Red, $"\n[ERROR] An unexpected error occurred: {ex.Message}");
                Log($"{name}: unexpected error: {ex}");
            }
            finally
            {
                PerformCleanup();
                ResetSession();
                // Don't keep the folder locked as our working directory while the menu waits
                TryConsole(() => Directory.SetCurrentDirectory(AppContext.BaseDirectory));
            }
            Log($"===== {name} finished =====");
        }

        internal static void ResetSession()
        {
            lock (CleanupLock)
            {
                sessionFolder = null;
                gitInitialized = false;
                keepChanges = false;
                remoteMerged = false;
                createdFiles.Clear();
                modifiedFiles.Clear();
                writtenContent.Clear();
                appendedText.Clear();
                userEditedFiles.Clear();
                indexBackup = null;
                headAtIndexBackup = "";
                sessionMergeStarted = false;
                branchToRestore = null;
                branchCreatedThisSession = null;
                secretBlobsAccepted.Clear();
                autoIgnoreExtensions.Clear();
                filesIgnoredCount = 0;
                lfsTrackedCount = 0;
                detectedProjectTypes = new List<string>();
                secretsStatus = "Clean";
            }
        }

        // Makes the folder the working directory for all git commands and remembers it for next time
        internal static void OpenSessionFolder(string folder)
        {
            folder = Path.GetFullPath(folder);
            Directory.SetCurrentDirectory(folder);
            sessionFolder = folder;
            RememberFolder(folder);
        }

        // =================================================================================
        // Cleanup
        // =================================================================================

        // Puts the folder back the way it was when a mode is aborted before anything was committed. Every step runs
        // on its own, so one locked file can't stop the others; .git goes last.
        internal static void PerformCleanup()
        {
            lock (CleanupLock)
            {
                if (sessionFolder == null) return;

                // 1. A merge GitSync started and didn't finish: cancel it first, while its own index is still in place.
                //    (This applies even after a commit - a half-finished merge leaves <<<<<<< markers in the files.)
                CleanupStep(() =>
                {
                    if (sessionMergeStarted && IsMergeInProgress())
                    {
                        var abort = RunGit(true, "merge", "--abort");
                        WriteLineColor(ConsoleColor.Yellow, abort.Success
                            ? "\nThe unfinished merge was cancelled - your files are back to how they were before it."
                            : "\nThe unfinished merge couldn't be cancelled - run Sync again to finish it, or cancel it in your Git tool.");
                    }
                    sessionMergeStarted = false;
                });

                // 2. Undo our staging in an existing repository - only while nothing has moved on since the backup, otherwise
                //    an old index would quietly undo a merge or a commit. This runs even when the work is kept: if HEAD
                //    hasn't moved, "git add -A" did nothing except replace what the user had staged themselves.
                CleanupStep(() =>
                {
                    if (indexBackup is { } backup && !IsMergeInProgress() && !IsRebaseInProgress() && CurrentHead() == headAtIndexBackup)
                    {
                        if (backup.Content != null) File.WriteAllBytes(backup.Path, backup.Content);
                        else if (File.Exists(backup.Path)) File.Delete(backup.Path);
                    }
                    indexBackup = null;
                });

                if (keepChanges) return;
                if (!gitInitialized && createdFiles.Count == 0 && modifiedFiles.Count == 0 && branchToRestore == null) return;
                WriteLineColor(ConsoleColor.Yellow, "\n[INFO] Aborted. Cleaning up created files...");

                // 3. Files GitSync created or changed - but never the user's own edits. Before switching branches: an
                //    edited .gitignore would make git refuse to switch back.
                foreach (var file in createdFiles)
                {
                    CleanupStep(() =>
                    {
                        if (!File.Exists(file)) return;
                        if (EditedSinceWritten(file) || userEditedFiles.Contains(file))
                        {
                            var (removed, missing) = RemoveOwnAdditions(file);
                            if (removed > 0 && missing == 0 && File.ReadAllText(file).Trim().Length == 0)
                            {
                                File.Delete(file);
                                Console.WriteLine($"Removed {Path.GetFileName(file)}.");
                            }
                            else
                            {
                                Console.WriteLine(DescribeKeptFile(file, removed, missing));
                            }
                            return;
                        }
                        File.Delete(file);
                        Console.WriteLine($"Removed {Path.GetFileName(file)}.");
                    });
                }

                foreach (var kv in modifiedFiles)
                {
                    CleanupStep(() =>
                    {
                        if (EditedSinceWritten(kv.Key) || userEditedFiles.Contains(kv.Key))
                        {
                            var (removed, missing) = RemoveOwnAdditions(kv.Key);
                            Console.WriteLine(DescribeKeptFile(kv.Key, removed, missing));
                            return;
                        }
                        File.WriteAllBytes(kv.Key, kv.Value);
                        Console.WriteLine($"Restored {Path.GetFileName(kv.Key)}.");
                    });
                }

                // 4. Back to the branch the user started on (a branch GitSync made and never used is removed again)
                CleanupStep(() =>
                {
                    if (branchToRestore == null || GetCurrentBranch() == branchToRestore) return;
                    // A branch with no commits yet (plain "git init") can't be checked out - only pointed at again
                    bool unborn = !RunGit(true, "rev-parse", "--verify", "--quiet", "refs/heads/" + branchToRestore).Success && !HasCommits();
                    var back = unborn
                        ? RunGit(true, "symbolic-ref", "HEAD", "refs/heads/" + branchToRestore)
                        : RunGit(true, "checkout", "--quiet", "--no-overwrite-ignore", branchToRestore);
                    if (!back.Success)
                    {
                        Console.WriteLine($"Couldn't switch back to '{branchToRestore}' - you're still on '{GetCurrentBranch()}'.");
                        return;
                    }
                    Console.WriteLine($"Switched back to branch '{branchToRestore}'.");
                    if (branchCreatedThisSession != null &&
                        RunGit(true, "rev-parse", branchCreatedThisSession).Output == RunGit(true, "rev-parse", branchToRestore).Output)
                    {
                        RunGit(true, "branch", "-D", branchCreatedThisSession);
                    }
                });

                // 5. The repository GitSync created, last
                CleanupStep(() =>
                {
                    string gitDir = Path.Combine(sessionFolder, ".git");
                    if (gitInitialized && Directory.Exists(gitDir) && !LfsHoldsOnlyCopy(gitDir))
                    {
                        // Rename first: if deleting stops half-way (a locked file), what's left is no longer
                        // a ".git" that the next run would mistake for a working repository
                        string doomed = gitDir + ".gitsync-delete-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                        try { Directory.Move(gitDir, doomed); } catch { doomed = gitDir; }

                        // Remove ReadOnly attributes to allow deletion
                        var dir = new DirectoryInfo(doomed);
                        dir.Attributes = FileAttributes.Normal;
                        foreach (var info in dir.GetFileSystemInfos("*", SearchOption.AllDirectories))
                        {
                            info.Attributes = FileAttributes.Normal;
                        }
                        try
                        {
                            dir.Delete(true);
                            Console.WriteLine("Removed .git directory.");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Couldn't remove all of {Path.GetFileName(doomed)} ({ex.Message}) - you can delete that folder yourself.");
                        }
                    }
                });

                // Never clean the same session twice (Ctrl+C runs this, then ProcessExit runs it again)
                gitInitialized = false;
                branchToRestore = null;
                createdFiles.Clear();
                modifiedFiles.Clear();
                writtenContent.Clear();
                appendedText.Clear();
                userEditedFiles.Clear();
            }
        }

        // Git LFS keeps files in .git\lfs and can leave small "pointer" text files in the folder instead - then deleting
        // .git would delete the only copy. Puts them back first; if that doesn't work, .git is kept (renamed) instead.
        static bool LfsHoldsOnlyCopy(string gitDir)
        {
            string objects = Path.Combine(gitDir, "lfs", "objects");
            if (!Directory.Exists(objects) || !Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories).Any()) return false;

            List<string> Pointers() => SplitNul(RunGit(true, "ls-files", "-z").Output).Where(f => IsLfsPointerFile(Path.Combine(sessionFolder!, f))).ToList();
            var pointers = Pointers();
            if (pointers.Count > 0)
            {
                RunGit(true, "lfs", "checkout");
                pointers = Pointers();
            }
            if (pointers.Count == 0) return false;

            string backup = gitDir + "-gitsync-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            bool moved = true;
            try { Directory.Move(gitDir, backup); } catch { moved = false; }
            Console.WriteLine($"{pointers.Count} file(s), e.g. {pointers[0]}, exist only inside Git LFS - so the repository wasn't deleted.");
            Console.WriteLine(moved
                ? $"It's kept as {Path.GetFileName(backup)}: rename that back to .git and run 'git lfs checkout' to get the files back."
                : "Run 'git lfs checkout' in this folder to get the files back.");
            return true;
        }

        // Takes the text GitSync appended back out of a file, leaving everything else - the user's own edits - exactly as it
        // is: the file is cut as bytes (never re-encoded), and only whole lines that are still exactly as GitSync wrote them
        // are removed. Returns how many of GitSync's additions were taken out, and how many weren't found intact.
        internal static (int Removed, int Missing) RemoveOwnAdditions(string path)
        {
            if (!appendedText.TryGetValue(Path.GetFullPath(path), out var chunks) || !File.Exists(path)) return (0, 0);
            byte[] content = File.ReadAllBytes(path);
            int removed = 0, missing = 0;
            foreach (var chunk in Enumerable.Reverse(chunks))
            {
                // An editor may have saved the file with other line endings
                string lf = chunk.Replace("\r\n", "\n");
                var forms = new[] { chunk, lf, lf.Replace("\n", "\r\n") }.Distinct().Select(f => System.Text.Encoding.UTF8.GetBytes(f));
                var found = forms.Select(f => (Bytes: f, At: FindWholeLines(content, f))).FirstOrDefault(m => m.At >= 0);
                if (found.Bytes == null)
                {
                    missing++;
                    continue;
                }
                content = content.AsSpan(0, found.At).ToArray().Concat(content.AsSpan(found.At + found.Bytes.Length).ToArray()).ToArray();
                removed++;
            }
            if (removed > 0) File.WriteAllBytes(path, content);
            return (removed, missing);
        }

        // Last place where this text stands as whole lines: it starts at the beginning of the file or of a line (or itself
        // starts with a line break) and ends at a line break or the end of the file. -1 if it isn't there.
        static int FindWholeLines(byte[] content, byte[] text)
        {
            if (text.Length == 0) return -1;
            for (int at = content.Length - text.Length; at >= 0; at--)
            {
                if (!content.AsSpan(at, text.Length).SequenceEqual(text)) continue;
                int end = at + text.Length;
                bool startsLine = at == 0 || content[at - 1] == '\n' || text[0] == '\r' || text[0] == '\n';
                bool endsLine = end == content.Length || text[^1] == '\n' || content[end] == '\r' || content[end] == '\n';
                if (startsLine && endsLine) return at;
            }
            return -1;
        }

        static string DescribeKeptFile(string file, int removed, int missing)
        {
            string name = Path.GetFileName(file);
            if (removed > 0 && missing == 0) return $"Took GitSync's additions out of {name} (your own changes are kept).";
            if (removed > 0) return $"Took most of GitSync's additions out of {name} - lines of its own that you edited were left as they are.";
            if (missing > 0) return $"Kept your current {name} (you changed it) - some lines GitSync added are still in it.";
            return $"Kept your current {name} (you changed it).";
        }

        static void AddAppendedChunk(string fullPath, string text)
        {
            if (!appendedText.TryGetValue(fullPath, out var chunks)) appendedText[fullPath] = chunks = new List<string>();
            chunks.Add(text);
        }

        // For a git command that adds lines to a file (git lfs track and git lfs migrate -> .gitattributes): remembered like
        // AppendToFile, so a cancel takes out exactly what it added - also when the user edited the file in between
        static GitResult RecordAppendBy(string path, Func<GitResult> write)
        {
            RememberFileState(path);
            string full = Path.GetFullPath(path);
            if (EditedSinceWritten(full)) userEditedFiles.Add(full);
            byte[] before = File.Exists(full) ? File.ReadAllBytes(full) : Array.Empty<byte>();
            var result = write();
            byte[] after = File.Exists(full) ? File.ReadAllBytes(full) : Array.Empty<byte>();
            if (after.Length > before.Length && after.AsSpan(0, before.Length).SequenceEqual(before))
            {
                AddAppendedChunk(full, System.Text.Encoding.UTF8.GetString(after, before.Length, after.Length - before.Length));
            }
            RecordWritten(full);
            return result;
        }

        static void CleanupStep(Action step)
        {
            try
            {
                step();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cleanup step failed: {ex.Message}");
            }
        }

        static string CurrentHead() => RunGit(true, "rev-parse", "--verify", "--quiet", "HEAD").Output.Trim();

        // Records a file's current state before GitSync (or a git command) changes it, so cleanup can undo it
        static void RememberFileState(string path)
        {
            path = Path.GetFullPath(path);
            if (createdFiles.Contains(path, StringComparer.OrdinalIgnoreCase) || modifiedFiles.ContainsKey(path)) return;
            if (File.Exists(path)) modifiedFiles[path] = File.ReadAllBytes(path);
            else createdFiles.Add(path);
        }

        // Records what GitSync wrote, so cleanup can tell a file the user edited afterwards (and must keep) from its own
        static void RecordWritten(string path)
        {
            path = Path.GetFullPath(path);
            if (File.Exists(path)) writtenContent[path] = File.ReadAllBytes(path);
        }

        static bool EditedSinceWritten(string path)
        {
            return writtenContent.TryGetValue(Path.GetFullPath(path), out var written)
                && File.Exists(path)
                && !File.ReadAllBytes(path).AsSpan().SequenceEqual(written);
        }

        internal static void AppendToFile(string path, string text)
        {
            RememberFileState(path);
            string full = Path.GetFullPath(path);
            // Changed by the user since GitSync's last write: cancelling must keep their part and take out only GitSync's
            if (EditedSinceWritten(full)) userEditedFiles.Add(full);
            // Don't glue our first line onto an existing last line that has no line break
            if (File.Exists(full) && !EndsWithNewline(full)) text = Environment.NewLine + text;
            File.AppendAllText(full, text);
            AddAppendedChunk(full, text);
            RecordWritten(full);
        }

        // Creates a file GitSync generates (README, LICENSE) and remembers it for cleanup
        static void WriteGeneratedFile(string path, string content)
        {
            RememberFileState(path);
            File.WriteAllText(path, content);
            RecordWritten(path);
        }

        static bool EndsWithNewline(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length == 0) return true;
                fs.Seek(-1, SeekOrigin.End);
                return fs.ReadByte() == '\n';
            }
        }

        // =================================================================================
        // Input & Output
        // =================================================================================

        static bool IsAbortWord(string input) =>
            input.Equals("abort", StringComparison.OrdinalIgnoreCase) ||
            input.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
            input.Equals("cancel", StringComparison.OrdinalIgnoreCase);

        internal static bool IsYesWord(string input) =>
            new[] { "y", "yes", "j", "ja", "ok" }.Contains(input.Trim().ToLowerInvariant());

        // freeText: an answer such as a commit message, where "cancel" might be meant literally - so ask first
        static string GetUserInput(string prompt, bool freeText)
        {
            try
            {
                return GetUserInput(prompt);
            }
            catch (UserAbortException) when (freeText && lastAnswer != null)
            {
                string word = lastAnswer;
                if (AskYesNo($"Stop what GitSync is doing? (n = use \"{word}\" as the text)", true)) throw;
                return word;
            }
        }

        private static string? lastAnswer = null;

        static string GetUserInput(string prompt)
        {
            lock (ConsoleLock)
            {
                Console.Write(prompt);
            }
            lastAnswer = null;
            string? line = Console.ReadLine();
            if (line == null) throw new UserAbortException("No more input. Process Aborted.");

            // Drop invisible characters that sneak in with pasted text (zero-width spaces from web pages, a BOM from piped input)
            string input = new string(line.Where(c => c != '﻿' && c != '​' && c != '‌' && c != '‍' && c != '⁠').ToArray()).Trim();
            // Echo scripted answers so redirected transcripts read like a real session
            if (Console.IsInputRedirected) Console.WriteLine(input);

            if (IsAbortWord(input))
            {
                lastAnswer = input;
                throw new UserAbortException();
            }
            return input;
        }

        // Accepts y/yes/j/ja and n/no/nej in any case. With a default, Enter picks it; without one, it keeps asking.
        static bool AskYesNo(string question, bool? defaultAnswer = null)
        {
            string suffix = defaultAnswer == true ? " (Y/n): " : defaultAnswer == false ? " (y/N): " : " (y/n): ";
            while (true)
            {
                string answer = GetUserInput(question + suffix).ToLowerInvariant();
                if (answer == "" && defaultAnswer.HasValue) return defaultAnswer.Value;
                if (answer == "y" || answer == "yes" || answer == "j" || answer == "ja") return true;
                if (answer == "n" || answer == "no" || answer == "nej") return false;
                Console.WriteLine("Please answer y or n.");
            }
        }

        // Keeps asking until one of the given choices is entered (case-insensitive). Returns it in lower case.
        static string AskChoice(string prompt, params string[] choices)
        {
            while (true)
            {
                string answer = GetUserInput(prompt).ToLowerInvariant();
                if (choices.Any(c => c.Equals(answer, StringComparison.OrdinalIgnoreCase))) return answer;
                Console.WriteLine($"Please enter {string.Join(", ", choices.Take(choices.Length - 1))} or {choices.Last()}.");
            }
        }

        static void LogVerbose(string message)
        {
            WriteLineColor(ConsoleColor.DarkGray, $"[INFO] {message}");
        }

        // Console writes go through one lock: live git output arrives on two threads at once
        static void WriteLineColor(ConsoleColor color, string text)
        {
            lock (ConsoleLock)
            {
                Console.ForegroundColor = color;
                Console.WriteLine(text);
                Console.ResetColor();
            }
        }

        private static (System.Text.Encoding Input, System.Text.Encoding Output)? originalConsoleEncoding = null;

        // A UTF-8 console keeps æøå intact in typed commit messages and names, also where the console's code page lacks
        // them (code page 437 has no ø, so it would silently become "o"). Windows 10 1903+ reads UTF-8 console input properly.
        static void UseUtf8Console()
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)) return;
            TryConsole(() =>
            {
                var original = (Console.InputEncoding, Console.OutputEncoding);
                var utf8 = new System.Text.UTF8Encoding(false);
                Console.InputEncoding = utf8;
                Console.OutputEncoding = utf8;
                originalConsoleEncoding = original;
            });
        }

        // The code page belongs to the console window, so give the terminal back the one it had
        static void RestoreConsoleEncoding()
        {
            if (originalConsoleEncoding is { } original)
            {
                TryConsole(() =>
                {
                    Console.InputEncoding = original.Input;
                    Console.OutputEncoding = original.Output;
                });
            }
        }

        static void PressAnyKey(string message)
        {
            Console.WriteLine(message);
            TryConsole(() => { if (!Console.IsInputRedirected) Console.ReadKey(true); });
        }

        // Console.Title/CursorVisible/ReadKey throw when there is no real console (redirected output, some IDE consoles)
        static void TryConsole(Action action)
        {
            try { action(); } catch { }
        }

        static int SafeWindowWidth()
        {
            try { return Console.WindowWidth > 0 ? Console.WindowWidth : 80; } catch { return 80; }
        }

        // Scripted runs (answers from a file or pipe, and the tests) never open windows - like the folder picker, which
        // they skip too. GITSYNC_NO_WINDOWS=1 does the same.
        static bool CanOpenWindows => !Console.IsInputRedirected && Environment.GetEnvironmentVariable("GITSYNC_NO_WINDOWS") != "1";

        static void OpenFolder(string path)
        {
            if (!CanOpenWindows) return;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not open the folder: {ex.Message}");
            }
        }

        static void OpenBrowser(string url)
        {
            if (!CanOpenWindows) return;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }

        // =================================================================================
        // Folder Selection
        // =================================================================================

        // Offers recent folders first (Enter = the latest one); "B" or no history opens the folder dialog.
        static string? PickFolder(string description)
        {
            var recent = LoadRecentFolders();
            if (startupFolder != null)
            {
                recent.RemoveAll(f => string.Equals(f, startupFolder, StringComparison.OrdinalIgnoreCase));
                recent.Insert(0, startupFolder);
            }

            if (recent.Count > 0)
            {
                Console.WriteLine("\nRecent folders:");
                for (int i = 0; i < recent.Count; i++) Console.WriteLine($"[{i + 1}] {recent[i]}");
                Console.WriteLine("[B] Browse for another folder... (or paste a folder path)");
                while (true)
                {
                    string choice = GetUserInput("Choice (Enter = 1): ").Trim('"');
                    if (choice == "") return recent[0];
                    if (int.TryParse(choice, out int n) && n >= 1 && n <= recent.Count) return recent[n - 1];
                    if (choice.Equals("b", StringComparison.OrdinalIgnoreCase)) break;
                    if (IsExistingFolderPath(choice)) return Path.GetFullPath(choice);
                    Console.WriteLine("Unknown choice.");
                }
            }

            if (Console.IsInputRedirected)
            {
                // No one can click a dialog when input is scripted
                string typed = GetUserInput("Folder path: ").Trim('"');
                return IsExistingFolderPath(typed) ? Path.GetFullPath(typed) : null;
            }

            LogVerbose("Waiting for user to select folder...");
            return ShowFolderSelection(description, recent.FirstOrDefault() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        static bool IsExistingFolderPath(string text)
        {
            try { return text.Length > 0 && Path.IsPathFullyQualified(text) && Directory.Exists(text); } catch { return false; }
        }

        static string? ShowFolderSelection(string description, string initialPath)
        {
            Console.WriteLine("Opening Folder Selection Window...");

            // A hidden top-most owner keeps the dialog from opening behind the console window
            using (var owner = new Form { TopMost = true, ShowInTaskbar = false })
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = description;
                dialog.UseDescriptionForTitle = true;
                dialog.ShowNewFolderButton = true;
                if (Directory.Exists(initialPath)) dialog.SelectedPath = initialPath;

                DialogResult result = dialog.ShowDialog(owner);

                if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                {
                    return dialog.SelectedPath;
                }
            }

            return null;
        }

        static List<string> LoadRecentFolders()
        {
            try
            {
                if (!File.Exists(RecentFile)) return new List<string>();
                return File.ReadAllLines(RecentFile)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && Directory.Exists(l))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        static void RememberFolder(string folder)
        {
            try
            {
                var recent = LoadRecentFolders();
                recent.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
                recent.Insert(0, folder);
                Directory.CreateDirectory(DataDir);
                File.WriteAllLines(RecentFile, recent.Take(5));
            }
            catch { }
        }

        // =================================================================================
        // Logging
        // =================================================================================

        static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch { }
        }

        static void Log(string status, string remote, string branch)
        {
            Log($"Status: {status} | Remote: {MaskUrl(remote)} | Branch: {branch}");
        }

        // Keeps the log from growing forever: past 5 MB the old one is set aside
        static void RotateLog()
        {
            try
            {
                var info = new FileInfo(LogFile);
                if (info.Exists && info.Length > 5 * 1024 * 1024)
                {
                    File.Move(LogFile, Path.Combine(DataDir, "gitsync_log.old.txt"), overwrite: true);
                }
            }
            catch { }
        }

        static void OpenLogFile()
        {
            if (!File.Exists(LogFile))
            {
                Console.WriteLine("No log yet. It is created the first time GitSync runs a git command.");
                return;
            }

            Console.WriteLine($"Log file: {LogFile}");
            if (!CanOpenWindows) return;
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe")) { UseShellExecute = false };
                psi.ArgumentList.Add(LogFile);
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not open it: {ex.Message}");
            }
        }
    }
}
