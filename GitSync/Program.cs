using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GitSync
{
    internal class Program
    {
        private static string LogFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gitsync_log.txt");
        private static HashSet<string> autoIgnoreExtensions = new HashSet<string>();

        // Track session state for cleanup
        private static bool gitInitialized = false;
        private static bool keepChanges = false;
        private static List<string> createdFiles = new List<string>();

        // Track statistics for summary
        private static int filesIgnoredCount = 0;
        private static int lfsTrackedCount = 0;
        private static List<string> detectedProjectTypes = new List<string>();
        private static string secretsStatus = "Clean";

        [STAThread]
        static void Main(string[] args)
        {
            Console.Title = "GitSync (C#)";
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("==========================================");
            Console.WriteLine("              GitSync (C#)");
            Console.WriteLine("==========================================");
            Console.ResetColor();

            try
            {
                // Hook process exit for cleanup on close
                AppDomain.CurrentDomain.ProcessExit += (s, e) => PerformCleanup();
                Console.CancelKeyPress += (s, e) => {
                    PerformCleanup();
                    e.Cancel = false; // Allow termination
                };

                // 1. Select Folder
                LogVerbose("Waiting for user to select folder...");
                string currentDir = AppDomain.CurrentDomain.BaseDirectory;
                string? selectedFolder = ShowFolderSelection(currentDir);
                if (selectedFolder == null)
                {
                    Console.WriteLine("No folder selected. Exiting.");
                    return;
                }

                Directory.SetCurrentDirectory(selectedFolder);
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"\nSelected Folder: {selectedFolder}");
                Console.ResetColor();

                // 2. Identity & Credentials
                CheckGitIdentity();
                CheckCredentialHelper();

                // 3. Git Init
                if (!Directory.Exists(".git"))
                {
                    LogVerbose("Initializing new Git repository...");
                    RunGitWithSpinner("init");
                    gitInitialized = true;

                    // Rename master to main if created
                    string branch = GetCurrentBranch();
                    if (branch == "master")
                    {
                        LogVerbose("Renaming default branch to 'main'...");
                        RunGitWithSpinner("branch -M main");
                    }
                }
                else
                {
                    LogVerbose("Git repository already exists.");
                }

                // 4. Project Setup
                detectedProjectTypes = SuggestGitIgnore(selectedFolder);
                SuggestLicense(selectedFolder);
                SuggestReadme(selectedFolder, detectedProjectTypes);

                // 5. Large Files & Secrets
                CheckLargeFiles(selectedFolder);
                ScanSecrets(selectedFolder);

                // 6. Remote URL
                string remoteUrl = SetupRemote();

                // 7. Branch Name
                string branchName = GetUserInput("\nEnter Branch Name (default: main): ");
                if (string.IsNullOrEmpty(branchName)) branchName = "main";

                // 8. Safety Pull
                SafetyPull(branchName);

                // 9. Commit Message
                string commitMsg = GetUserInput("\nEnter Initial Commit Message: ");
                if (string.IsNullOrEmpty(commitMsg)) commitMsg = "Initial commit";

                // 10. Summary & Push
                PerformPush(remoteUrl, branchName, commitMsg, selectedFolder);

            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[ERROR] An unexpected error occurred: {ex.Message}");
                Console.ResetColor();
                PerformCleanup();
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
            }
        }

        // =================================================================================
        // Features
        // =================================================================================

        static void PerformCleanup()
        {
            if (keepChanges) return;
            if (!gitInitialized && createdFiles.Count == 0) return;

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\n[INFO] Aborted. Cleaning up created files...");
            Console.ResetColor();

            try
            {
                if (gitInitialized && Directory.Exists(".git"))
                {
                    // Remove ReadOnly attributes to allow deletion
                    var dir = new DirectoryInfo(".git");
                    dir.Attributes = FileAttributes.Normal;
                    foreach (var info in dir.GetFileSystemInfos("*", SearchOption.AllDirectories))
                    {
                        info.Attributes = FileAttributes.Normal;
                    }
                    dir.Delete(true);
                    Console.WriteLine("Removed .git directory.");
                }

                foreach (var file in createdFiles)
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                        Console.WriteLine($"Removed {Path.GetFileName(file)}.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cleanup failed: {ex.Message}");
            }
        }

        static string GetUserInput(string prompt)
        {
            Console.Write(prompt);
            string input = Console.ReadLine()?.Trim() ?? "";

            if (input.Equals("abort", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("cancel", StringComparison.OrdinalIgnoreCase))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\nProcess Aborted by User.");
                Console.ResetColor();
                Environment.Exit(0);
            }
            return input;
        }

        static void LogVerbose(string message)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[INFO] {message}");
            Console.ResetColor();
        }

        static string? ShowFolderSelection(string path)
        {
            Console.WriteLine("Opening Folder Selection Window...");

            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select a folder to turn into a Git repository";
                dialog.UseDescriptionForTitle = true;
                dialog.ShowNewFolderButton = true;
                dialog.SelectedPath = path;

                DialogResult result = dialog.ShowDialog();

                if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                {
                    return dialog.SelectedPath;
                }
            }

            return null;
        }

        static void CheckGitIdentity()
        {
            LogVerbose("Checking Git configuration...");
            string name = RunGit("config --global user.name", true);
            if (string.IsNullOrWhiteSpace(name))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Git user.name is not set.");
                Console.ResetColor();
                string newName = GetUserInput("Enter your Name: ");
                if (string.IsNullOrWhiteSpace(newName)) newName = "User";
                RunGit($"config --global user.name \"{newName}\"");
            }

            string email = RunGit("config --global user.email", true);
            if (string.IsNullOrWhiteSpace(email))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Git user.email is not set.");
                Console.ResetColor();
                string newEmail = GetUserInput("Enter your Email: ");
                if (string.IsNullOrWhiteSpace(newEmail)) newEmail = "user@example.com";
                RunGit($"config --global user.email \"{newEmail}\"");
            }
        }

        static void CheckCredentialHelper()
        {
            string helper = RunGit("config --global credential.helper", true);
            if (string.IsNullOrWhiteSpace(helper))
            {
                LogVerbose("Setting credential helper to 'manager'...");
                RunGit("config --global credential.helper manager");
            }
        }

        static List<string> SuggestGitIgnore(string folderPath)
        {
            LogVerbose("Analyzing project structure for .gitignore suggestions...");

            var templates = new Dictionary<string, List<string>>
            {
                { "Python", new List<string> { "*.pyc", "__pycache__/", "venv/", "env/", "*.egg-info/" } },
                { "Node.js", new List<string> { "node_modules/", "npm-debug.log", "dist/", ".env" } },
                { "Unity", new List<string> { "Library/", "Temp/", "Obj/", "Build/", "Logs/", "*.csproj", "*.sln" } },
                { "Unreal", new List<string> { "Binaries/", "Build/", "Intermediate/", "Saved/", "DerivedDataCache/", "*.sln" } },
                { "VisualStudio", new List<string> { ".vs/", "bin/", "obj/", "*.user", "*.suo" } },
                { "Java", new List<string> { "*.class", "*.jar", "target/", ".gradle/", "build/" } },
                { "Rust", new List<string> { "target/", "**/*.rs.bk" } },
                { "C++", new List<string> { "build/", "bin/", "*.o", "*.exe", "CMakeCache.txt" } },
                { "Flutter", new List<string> { ".dart_tool/", "build/", ".packages", ".pub/", "ios/Flutter/Generated.xcconfig", "android/.gradle" } },
                { "Go", new List<string> { "bin/", "*.exe", "*.test", "vendor/" } },
                { "Ruby", new List<string> { "vendor/bundle/", "log/", "tmp/", ".byebug_history" } },
                { "Swift", new List<string> { ".build/", "DerivedData/", "*.xcuserstate" } },
                { "Godot", new List<string> { ".import/", "export.cfg", "export_presets.cfg" } }
            };

            var detected = new List<string>();

            if (Directory.GetFiles(folderPath, "*.py", SearchOption.AllDirectories).Any()) detected.Add("Python");
            if (Directory.GetFiles(folderPath, "package.json", SearchOption.AllDirectories).Any()) detected.Add("Node.js");
            if (Directory.GetDirectories(folderPath, "Assets", SearchOption.TopDirectoryOnly).Any()) detected.Add("Unity");
            if (Directory.GetFiles(folderPath, "*.uproject", SearchOption.TopDirectoryOnly).Any()) detected.Add("Unreal");
            if (Directory.GetFiles(folderPath, "*.sln", SearchOption.AllDirectories).Any()) detected.Add("VisualStudio");
            if (Directory.GetFiles(folderPath, "pom.xml", SearchOption.AllDirectories).Any()) detected.Add("Java");
            if (Directory.GetFiles(folderPath, "Cargo.toml", SearchOption.AllDirectories).Any()) detected.Add("Rust");
            if (Directory.GetFiles(folderPath, "CMakeLists.txt", SearchOption.AllDirectories).Any()) detected.Add("C++");
            if (Directory.GetFiles(folderPath, "pubspec.yaml", SearchOption.AllDirectories).Any()) detected.Add("Flutter");
            if (Directory.GetFiles(folderPath, "go.mod", SearchOption.AllDirectories).Any()) detected.Add("Go");
            if (Directory.GetFiles(folderPath, "Gemfile", SearchOption.AllDirectories).Any()) detected.Add("Ruby");
            if (Directory.GetFiles(folderPath, "*.xcodeproj", SearchOption.AllDirectories).Any()) detected.Add("Swift");
            if (Directory.GetFiles(folderPath, "project.godot", SearchOption.AllDirectories).Any()) detected.Add("Godot");

            if (detected.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Detected project type(s): {string.Join(", ", detected)}");
                Console.ResetColor();

                string choice = GetUserInput("Generate standard .gitignore? (y/n): ");
                if (choice.ToLower() == "y")
                {
                    string ignorePath = Path.Combine(folderPath, ".gitignore");
                    if (!File.Exists(ignorePath)) createdFiles.Add(ignorePath);

                    using (StreamWriter sw = File.AppendText(ignorePath))
                    {
                        foreach (var type in detected)
                        {
                            sw.WriteLine($"\n# {type}");
                            foreach (var line in templates[type]) sw.WriteLine(line);
                        }
                    }
                    LogVerbose("Updated .gitignore with standard templates.");
                }
            }

            return detected;
        }

        static void SuggestLicense(string folderPath)
        {
            if (File.Exists(Path.Combine(folderPath, "LICENSE"))) return;

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\nNo LICENSE file found.");
            Console.ResetColor();
            Console.WriteLine("[1] MIT License");
            Console.WriteLine("[2] Apache License 2.0");
            Console.WriteLine("[3] GNU GPLv3");
            Console.WriteLine("[4] None");

            string choice = GetUserInput("Choice: ");
            string author = RunGit("config --global user.name", true);
            string year = DateTime.Now.Year.ToString();
            string content = "";

            switch (choice)
            {
                case "1": content = $"MIT License\n\nCopyright (c) {year} {author}..."; break;
                case "2": content = $"Apache License 2.0..."; break;
                case "3": content = $"GNU GPLv3..."; break;
            }

            if (!string.IsNullOrEmpty(content))
            {
                string path = Path.Combine(folderPath, "LICENSE");
                File.WriteAllText(path, content);
                createdFiles.Add(path);
                LogVerbose("LICENSE file created.");
            }
        }

        static void SuggestReadme(string folderPath, List<string> types)
        {
            if (File.Exists(Path.Combine(folderPath, "README.md"))) return;

            string choice = GetUserInput("\nCreate default README.md? (y/n): ");
            if (choice.ToLower() != "y") return;

            string name = new DirectoryInfo(folderPath).Name;
            string content = $"# {name}\n\nProject Description.\n\n## Getting Started\n";

            if (types.Contains("Node.js")) content += "\n```bash\nnpm install\nnpm start\n```";
            else if (types.Contains("Python")) content += "\n```bash\npip install -r requirements.txt\npython main.py\n```";
            else content += "\nInstructions...";

            string path = Path.Combine(folderPath, "README.md");
            File.WriteAllText(path, content);
            createdFiles.Add(path);
            LogVerbose("README.md created.");
        }

        static void CheckLargeFiles(string folderPath)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\nScanning for large files (>100MB)...");
            Console.ResetColor();

            bool lfsAvailable = RunGit("lfs version", true).Contains("git-lfs");
            long limit = 100 * 1024 * 1024;

            // Gather files first to show count
            // Robust scan avoiding .git folder (even if parent path has .git)
            var allFiles = new List<string>();
            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };

            try
            {
                allFiles = Directory.EnumerateFiles(folderPath, "*", options)
                    .Where(f => {
                        // Check if any segment of the relative path is ".git"
                        var rel = Path.GetRelativePath(folderPath, f);
                        return !rel.Split(Path.DirectorySeparatorChar).Contains(".git");
                    })
                    .ToList();
            }
            catch { }

            // Simple spinner for scanning
            LogVerbose($"Scanning {allFiles.Count} files in file system...");
            var largeFiles = new List<FileInfo>();

            foreach (var f in allFiles)
            {
                try
                {
                    var info = new FileInfo(f);
                    if (info.Length > limit) largeFiles.Add(info);
                }
                catch { }
            }

            if (largeFiles.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("No large files found.");
                Console.ResetColor();
                return;
            }

            LogVerbose($"Found {largeFiles.Count} files larger than 100MB.");

            foreach (var file in largeFiles)
            {
                string relPath = Path.GetRelativePath(folderPath, file.FullName).Replace("\\", "/");
                string extension = file.Extension;

                // Check auto-ignores
                if (autoIgnoreExtensions.Contains(extension))
                {
                    LogVerbose($"Auto-skipping large file (extension '{extension}'): {relPath}");
                    // Logic: Since we added *extension to .gitignore, we don't need to add the specific file.
                    continue;
                }

                // Check existing gitignore
                if (!string.IsNullOrEmpty(RunGit($"check-ignore \"{relPath}\"", true))) continue;

                double sizeMB = Math.Round(file.Length / 1024.0 / 1024.0, 2);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n[WARNING] Large file: {relPath} ({sizeMB} MB)");
                Console.ResetColor();

                while (true)
                {
                    Console.WriteLine("[1] Skip this file (Add to .gitignore)");
                    Console.WriteLine("[2] Abort");
                    Console.WriteLine("[3] Include anyway");
                    if (lfsAvailable) Console.WriteLine("[4] Track with Git LFS");
                    Console.WriteLine($"[5] Skip ALL files with extension '{extension}'");

                    string c = GetUserInput("Choice: ");

                    if (c == "1")
                    {
                        File.AppendAllText(Path.Combine(folderPath, ".gitignore"), $"\n{relPath}");
                        filesIgnoredCount++;
                        break;
                    }
                    if (c == "2") Environment.Exit(0);
                    if (c == "3") break;
                    if (c == "4" && lfsAvailable)
                    {
                        RunGitWithSpinner($"lfs track \"{relPath}\"");
                        RunGitWithSpinner("add .gitattributes");
                        lfsTrackedCount++;
                        break;
                    }
                    if (c == "5")
                    {
                        autoIgnoreExtensions.Add(extension);
                        File.AppendAllText(Path.Combine(folderPath, ".gitignore"), $"\n*{extension}");
                        LogVerbose($"Added *{extension} to .gitignore. Future files of this type will be skipped.");
                        filesIgnoredCount++;
                        break;
                    }
                }
            }
        }

        static void ScanSecrets(string folderPath)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\nScanning for secrets...");
            Console.ResetColor();

            string[] riskWords = { "API_KEY", "SECRET", "PASSWORD", "private_key" };
            bool risksFound = false;

            LogVerbose("Analyzing text files for sensitive patterns...");

            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };

            // Limit scan to small text files to be fast and avoid .git
            var files = Directory.EnumerateFiles(folderPath, "*", options)
                .Where(f => {
                    var rel = Path.GetRelativePath(folderPath, f);
                    return !rel.Split(Path.DirectorySeparatorChar).Contains(".git");
                })
                .Where(f => new FileInfo(f).Length < 5 * 1024 * 1024);

            foreach (var f in files)
            {
                try
                {
                    var lines = File.ReadLines(f).Take(200);
                    foreach (var line in lines)
                    {
                        foreach (var word in riskWords)
                        {
                            if (line.Contains(word, StringComparison.OrdinalIgnoreCase))
                            {
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine($"[CRITICAL] Potential secret in: {Path.GetRelativePath(folderPath, f)}");
                                Console.ResetColor();
                                risksFound = true;
                                goto NextFile;
                            }
                        }
                    }
                }
                catch { }
            NextFile: continue;
            }

            if (risksFound)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                string proceed = GetUserInput("\nSecrets detected! Proceed? (yes/no): ");
                Console.ResetColor();
                if (proceed != "yes") Environment.Exit(0);
                secretsStatus = "Warning Ignored";
            }
        }

        static string SetupRemote()
        {
            while (true)
            {
                string url = GetUserInput("\nEnter Git Remote URL: ");

                if (url.StartsWith("https://") || url.StartsWith("git@"))
                {
                    string existing = RunGit("remote");
                    if (existing.Contains("origin"))
                    {
                        string replace = GetUserInput("Remote 'origin' exists. Replace? (y/n): ");
                        if (replace == "y")
                        {
                            LogVerbose("Replacing origin remote...");
                            RunGit("remote remove origin");
                            RunGit($"remote add origin {url}");
                            return url;
                        }
                    }
                    else
                    {
                        LogVerbose("Adding origin remote...");
                        RunGit($"remote add origin {url}");
                        return url;
                    }
                }
                Console.WriteLine("Invalid URL format.");
            }
        }

        static void SafetyPull(string branch)
        {
            Console.WriteLine("\nChecking for remote updates...");
            RunGit("fetch origin");
            string check = RunGit($"ls-remote --heads origin {branch}", true);
            if (!string.IsNullOrWhiteSpace(check))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Remote branch found. Pulling...");
                Console.ResetColor();
                RunGitWithSpinner($"pull origin {branch} --allow-unrelated-histories");
            }
        }

        static void PerformPush(string remote, string branch, string msg, string folder)
        {
            LogVerbose("Staging all files (this may take a while)...");
            RunGitWithSpinner("add .");

            var status = RunGit("status --short", true);
            var stagedFiles = status.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            int count = stagedFiles.Length;

            // Calculate total size of staged files (approximate)
            long totalSizeBytes = 0;
            // This is a rough estimation because 'status' doesn't give sizes directly.
            // A more accurate way would be to enumerate files in folder again, but let's keep it simple for now.
            // We'll skip complex size calc to avoid performance hit at the end.

            Console.Clear();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("==========================================");
            Console.WriteLine("           FINAL SUMMARY");
            Console.WriteLine("==========================================");
            Console.ResetColor();
            Console.WriteLine($"Project Type:   {string.Join(", ", detectedProjectTypes)}");
            Console.WriteLine($"Remote URL:     {remote}");
            Console.WriteLine($"Target Branch:  {branch}");
            Console.WriteLine($"Commit Message: {msg}");
            Console.WriteLine("------------------------------------------");
            Console.WriteLine($"Files Ignored:  {filesIgnoredCount}");
            Console.WriteLine($"LFS Tracked:    {lfsTrackedCount}");
            Console.WriteLine($"Secrets Check:  {secretsStatus}");
            Console.WriteLine($"Files to Push:  {count}");
            Console.WriteLine("==========================================");

            string confirm = GetUserInput("Ready to Push? (y/n): ");
            if (confirm != "y")
            {
                Log("Aborted", remote, branch);
                return;
            }

            LogVerbose("Committing changes...");
            RunGitWithSpinner($"commit -m \"{msg}\"");

            LogVerbose("Pushing to remote server...");
            string result = RunGitWithSpinner($"push -u origin {branch}");

            if (!result.Contains("error") && !result.Contains("fatal"))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\nSUCCESS!");
                Console.ResetColor();

                // Mark success so cleanup doesn't run
                keepChanges = true;

                Log("Success", remote, branch);

                if (remote.StartsWith("https://"))
                {
                    Process.Start(new ProcessStartInfo(remote) { UseShellExecute = true });
                }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\nPush Failed.");
                Console.ResetColor();
                Log("Failed", remote, branch);
            }
        }

        static string RunGitWithSpinner(string args)
        {
            var task = Task.Run(() => RunGit(args, true));
            var spinner = new[] { '|', '/', '-', '\\' };
            int counter = 0;

            Console.CursorVisible = false;
            while (!task.IsCompleted)
            {
                Console.Write($"\rWorking... {spinner[counter++ % 4]}");
                Thread.Sleep(100);
            }
            Console.Write("\rDone!          \n");
            Console.CursorVisible = true;
            return task.Result;
        }

        static string RunGit(string args, bool silent = false)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var p = Process.Start(psi))
            {
                if (p == null) return "";

                var outputTask = p.StandardOutput.ReadToEndAsync();
                var errorTask = p.StandardError.ReadToEndAsync();

                Task.WaitAll(outputTask, errorTask);
                p.WaitForExit();

                string output = outputTask.Result;
                string err = errorTask.Result;

                if (!silent && !string.IsNullOrWhiteSpace(output)) Console.WriteLine(output);
                if (!silent && !string.IsNullOrWhiteSpace(err)) Console.WriteLine(err);

                return output + err;
            }
        }

        static string GetCurrentBranch()
        {
            return RunGit("rev-parse --abbrev-ref HEAD", true).Trim();
        }

        static void Log(string status, string remote, string branch)
        {
            try
            {
                string msg = $"[{DateTime.Now}] Status: {status} | Remote: {remote} | Branch: {branch}\n";
                File.AppendAllText(LogFile, msg);
            }
            catch { }
        }
    }
}
