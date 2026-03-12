using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GitSync
{
    public class ProjectMetadata
    {
        public string Name { get; set; } = "MyProject";
        public string Description { get; set; } = "A new project.";
        public string Version { get; set; } = "0.1.0";
        public string RunCommand { get; set; } = "";
    }

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
            WriteColoredLine("==========================================", ConsoleColor.Cyan);
            WriteColoredLine("   GitSync - Git but without pain added   ", ConsoleColor.Cyan);
            WriteColoredLine("==========================================", ConsoleColor.Cyan);

            try
            {
                // Hook process exit for cleanup on close
                AppDomain.CurrentDomain.ProcessExit += (s, e) => PerformCleanup();
                Console.CancelKeyPress += (s, e) => {
                    PerformCleanup();
                    e.Cancel = false; // Allow termination
                };

                while (true)
                {
                    Console.WriteLine("\nChoose a mode:");
                    Console.WriteLine("[1] Sync Local Folder (Init/Push)");
                    Console.WriteLine("[2] Clone & Setup (Startup)");
                    Console.WriteLine("[3] Exit");

                    string choice = GetUserInput("Choice: ");

                    if (choice == "1")
                    {
                        HandleLocalSync();
                        break;
                    }
                    else if (choice == "2")
                    {
                        HandleClone();
                        break;
                    }
                    else if (choice == "3")
                    {
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                WriteColoredLine($"\n[ERROR] An unexpected error occurred: {ex.Message}", ConsoleColor.Red);
                PerformCleanup();
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
            }
        }

        static void HandleLocalSync()
        {
            // 1. Select Folder
            LogVerbose("Waiting for user to select folder...");
            string currentDir = AppDomain.CurrentDomain.BaseDirectory;
            string? selectedFolder = ShowFolderSelection(currentDir);
            if (selectedFolder == null)
            {
                Console.WriteLine("No folder selected. Exiting...");
                return;
            }

            Directory.SetCurrentDirectory(selectedFolder);
            WriteColoredLine($"\nSelected Folder: {selectedFolder}", ConsoleColor.Cyan);

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

            // 9. Quality Gate
            if (!CheckQualityGates(selectedFolder)) return;

            // 10. Commit Message
            string commitMsg = GetUserInput("\nEnter Initial Commit Message: ");
            if (string.IsNullOrEmpty(commitMsg)) commitMsg = "Initial commit";

            // 11. Summary & Push
            PerformPush(remoteUrl, branchName, commitMsg, selectedFolder);
        }

        static void HandleClone()
        {
            string url = GetUserInput("\nEnter Git URL to clone: ");
            if (string.IsNullOrWhiteSpace(url)) return;

            string folderName = Path.GetFileNameWithoutExtension(url);
            if (folderName.EndsWith(".git")) folderName = folderName.Substring(0, folderName.Length - 4);

            LogVerbose($"Cloning {url}...");
            RunGitWithSpinner($"clone {url}");

            string clonePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, folderName);
            if (Directory.Exists(clonePath))
            {
                Directory.SetCurrentDirectory(clonePath);
                Console.WriteLine($"\nCloned to: {clonePath}");

                // Detect type
                var types = SuggestGitIgnore(clonePath); // Reusing this for detection primarily
                var meta = ParseProjectMetadata(clonePath, types);

                Console.WriteLine($"\nDetected Project: {meta.Name}");

                if (!string.IsNullOrWhiteSpace(meta.RunCommand))
                {
                    string setupCmd = meta.RunCommand.Split('\n')[0]; // Simple heuristic
                    if (types.Contains("Node.js")) setupCmd = "npm install";
                    if (types.Contains("Python")) setupCmd = "pip install -r requirements.txt";
                    if (types.Contains("Rust")) setupCmd = "cargo build";
                    if (types.Contains("C++")) setupCmd = "cmake .";

                    string answer = GetUserInput($"\nRun setup command '{setupCmd}'? (y/n): ");
                    if (answer.ToLower() == "y")
                    {
                        var parts = setupCmd.Split(' ');
                        var exe = parts[0];
                        var args = string.Join(" ", parts.Skip(1));

                        LogVerbose($"Running {setupCmd}...");

                        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = true };
                        Process.Start(psi)?.WaitForExit();
                    }
                }

                string open = GetUserInput("\nOpen in Explorer? (y/n): ");
                if (open.ToLower() == "y")
                {
                    Process.Start("explorer.exe", clonePath);
                }
            }
            else
            {
                Console.WriteLine("Clone failed or folder not found.");
            }
        }

        static bool CheckQualityGates(string folder)
        {
            Console.WriteLine("\n[Quality Gate] Checking code quality...");
            int issues = 0;

            // TODO Scanner
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains(".git") && new FileInfo(f).Length < 1024 * 1024); // < 1MB text files

            foreach (var f in files)
            {
                try
                {
                    var lines = File.ReadLines(f);
                    foreach (var line in lines)
                    {
                        if (line.Contains("TODO:") || line.Contains("FIXME:"))
                        {
                            issues++;
                        }
                    }
                }
                catch { }
            }

            if (issues > 0)
            {
                WriteColoredLine($"Found {issues} TODO/FIXME comments.", ConsoleColor.Yellow);

                string choice = GetUserInput("Issues found. [1] Abort, [2] Push Anyway (MASTER OVERRIDE): ");
                if (choice == "1") return false;
            }
            else
            {
                Console.WriteLine("No TODOs found. Clean.");
            }

            return true;
        }

        // =================================================================================
        // Features
        // =================================================================================

        static void PerformCleanup()
        {
            if (keepChanges) return;
            if (!gitInitialized && createdFiles.Count == 0) return;

            WriteColoredLine("\n[INFO] Aborted. Cleaning up created files...", ConsoleColor.Yellow);

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
                WriteColoredLine("\nProcess Aborted by User.", ConsoleColor.Red);
                Environment.Exit(0);
            }
            return input;
        }

        static void LogVerbose(string message)
        {
            WriteColoredLine($"[INFO] {message}", ConsoleColor.DarkGray);
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
                WriteColoredLine("Git user.name is not set.", ConsoleColor.Yellow);
                string newName = GetUserInput("Enter your Name: ");
                if (string.IsNullOrWhiteSpace(newName)) newName = "User";
                RunGit($"config --global user.name \"{newName}\"");
            }

            string email = RunGit("config --global user.email", true);
            if (string.IsNullOrWhiteSpace(email))
            {
                WriteColoredLine("Git user.email is not set.", ConsoleColor.Yellow);
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

            var detectedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rootInfo = new DirectoryInfo(folderPath);
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

            foreach (var info in rootInfo.EnumerateFileSystemInfos("*", options))
            {
                var name = info.Name;
                var isDirectory = (info.Attributes & FileAttributes.Directory) == FileAttributes.Directory;
                var isTopLevel = string.Equals(Path.GetDirectoryName(info.FullName)?.TrimEnd(Path.DirectorySeparatorChar), rootInfo.FullName.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

                if (!isDirectory)
                {
                    var ext = info.Extension;
                    if (string.Equals(ext, ".py", StringComparison.OrdinalIgnoreCase)) detectedSet.Add("Python");
                    if (string.Equals(name, "package.json", StringComparison.OrdinalIgnoreCase)) detectedSet.Add("Node.js");
                    if (isTopLevel && string.Equals(ext, ".uproject", StringComparison.OrdinalIgnoreCase)) detectedSet.Add("Unreal");
                    if (string.Equals(ext, ".sln", StringComparison.OrdinalIgnoreCase)) detectedSet.Add("VisualStudio");
                    if (string.Equals(name, "pom.xml", StringComparison.OrdinalIgnoreCase)) detectedSet.Add("Java");
                    if (string.Equals(name, "Cargo.toml", StringComparison.OrdinalIgnoreCase)) detectedSet.Add("Rust");
                    if (string.Equals(name, "CMakeLists.txt", StringComparison.OrdinalIgnoreCase)) detectedSet.Add("C++");
                    if (string.Equals(name, "pubspec.yaml", StringComparison.OrdinalIgnoreCase)) detectedSet.Add("Flutter");
                    if (string.Equals(name, "go.mod", StringComparison.OrdinalIgnoreCase)) detectedSet.Add("Go");
                    if (string.Equals(name, "Gemfile", StringComparison.OrdinalIgnoreCase)) detectedSet.Add("Ruby");
                    if (string.Equals(ext, ".xcodeproj", StringComparison.OrdinalIgnoreCase)) detectedSet.Add("Swift");
                    if (string.Equals(name, "project.godot", StringComparison.OrdinalIgnoreCase)) detectedSet.Add("Godot");
                }
                else if (isTopLevel && string.Equals(name, "Assets", StringComparison.OrdinalIgnoreCase))
                {
                    detectedSet.Add("Unity");
                }

                if (detectedSet.Count == templates.Count) break;
            }

            var detected = detectedSet.ToList();

            if (detected.Count > 0)
            {
                WriteColoredLine($"Detected project type(s): {string.Join(", ", detected)}", ConsoleColor.Yellow);

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

            WriteColoredLine("\nNo LICENSE file found.", ConsoleColor.Cyan);
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

        static ProjectMetadata ParseProjectMetadata(string folder, List<string> types)
        {
            var meta = new ProjectMetadata { Name = new DirectoryInfo(folder).Name };

            try
            {
                if (types.Contains("Node.js"))
                {
                    string pkg = Path.Combine(folder, "package.json");
                    if (File.Exists(pkg))
                    {
                        string json = File.ReadAllText(pkg);
                        // Simple regex parsing to avoid Json dependencies
                        var nameMatch = Regex.Match(json, "\"name\":\\s*\"(.*?)\"");
                        var descMatch = Regex.Match(json, "\"description\":\\s*\"(.*?)\"");
                        var verMatch = Regex.Match(json, "\"version\":\\s*\"(.*?)\"");

                        if (nameMatch.Success) meta.Name = nameMatch.Groups[1].Value;
                        if (descMatch.Success) meta.Description = descMatch.Groups[1].Value;
                        if (verMatch.Success) meta.Version = verMatch.Groups[1].Value;

                        meta.RunCommand = "npm install\nnpm start";
                    }
                }
                else if (types.Contains("Unity"))
                {
                    string settings = Path.Combine(folder, "ProjectSettings", "ProjectSettings.asset");
                    if (File.Exists(settings))
                    {
                        string content = File.ReadAllText(settings);
                        var nameMatch = Regex.Match(content, "productName: (.*)");
                        if (nameMatch.Success) meta.Name = nameMatch.Groups[1].Value.Trim();
                        meta.Description = "A Unity Game Project.";
                        meta.RunCommand = "Open in Unity Hub";
                    }
                }
                else if (types.Contains("Rust"))
                {
                    string cargo = Path.Combine(folder, "Cargo.toml");
                    if (File.Exists(cargo))
                    {
                        string content = File.ReadAllText(cargo);
                        var nameMatch = Regex.Match(content, "name\\s*=\\s*\"(.*?)\"");
                        if (nameMatch.Success) meta.Name = nameMatch.Groups[1].Value;
                        meta.RunCommand = "cargo run";
                    }
                }
                else if (types.Contains("Python"))
                {
                    meta.RunCommand = "pip install -r requirements.txt\npython main.py";
                }
            }
            catch { }

            return meta;
        }

        static void SuggestReadme(string folderPath, List<string> types)
        {
            if (File.Exists(Path.Combine(folderPath, "README.md"))) return;

            string choice = GetUserInput("\nCreate default README.md? (y/n): ");
            if (choice.ToLower() != "y") return;

            // Intelligent Parsing
            LogVerbose("Parsing project files for metadata...");
            var meta = ParseProjectMetadata(folderPath, types);

            string content = $"# {meta.Name} (v{meta.Version})\n\n";
            content += $"{meta.Description}\n\n";
            content += "## Getting Started\n\n";

            if (!string.IsNullOrEmpty(meta.RunCommand))
            {
                content += "```bash\n" + meta.RunCommand + "\n```\n";
            }
            else
            {
                content += "Instructions on how to run the project.\n";
            }

            content += "\nGenerated by GitSync.";

            string path = Path.Combine(folderPath, "README.md");
            File.WriteAllText(path, content);
            createdFiles.Add(path);
            LogVerbose($"Custom README.md created for {meta.Name}.");
        }

        static void CheckLargeFiles(string folderPath)
        {
            WriteColoredLine("\nScanning for large files (>100MB)...", ConsoleColor.Cyan);

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
                WriteColoredLine("No large files found.", ConsoleColor.Green);
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
                WriteColoredLine($"\n[WARNING] Large file: {relPath} ({sizeMB} MB)", ConsoleColor.Yellow);

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
            WriteColoredLine("\nScanning for secrets...", ConsoleColor.Cyan);

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
                                WriteColoredLine($"[CRITICAL] Potential secret in: {Path.GetRelativePath(folderPath, f)}", ConsoleColor.Red);
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
                WriteColored("\nSecrets detected! Proceed? (yes/no): ", ConsoleColor.Red);
                string proceed = GetUserInput("");
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
                WriteColoredLine("Remote branch found. Pulling...", ConsoleColor.Yellow);
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

            Console.Clear();
            WriteColoredLine("==========================================", ConsoleColor.Cyan);
            WriteColoredLine("           FINAL SUMMARY", ConsoleColor.Cyan);
            WriteColoredLine("==========================================", ConsoleColor.Cyan);
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
                WriteColoredLine("\nSUCCESS!", ConsoleColor.Green);

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
                WriteColoredLine("\nPush Failed.", ConsoleColor.Red);
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

        private static void WriteColoredLine(string text, ConsoleColor color)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ResetColor();
        }

        private static void WriteColored(string text, ConsoleColor color)
        {
            Console.ForegroundColor = color;
            Console.Write(text);
            Console.ResetColor();
        }
    }
}
