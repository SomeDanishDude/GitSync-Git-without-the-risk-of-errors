using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GitSync
{
    public partial class Program
    {
        private static readonly Dictionary<string, List<string>> GitIgnoreTemplates = new Dictionary<string, List<string>>
        {
            { "Python", new List<string> { "*.pyc", "__pycache__/", "venv/", ".venv/", "env/", "*.egg-info/" } },
            { "Node.js", new List<string> { "node_modules/", "npm-debug.log", "dist/", ".env" } },
            // Anchored to the project root: Assets/ may legitimately contain folders called Library, Build, ...
            { "Unity", new List<string> { "/[Ll]ibrary/", "/[Tt]emp/", "/[Oo]bj/", "/[Bb]uild/", "/[Bb]uilds/", "/[Ll]ogs/", "/[Uu]ser[Ss]ettings/", "*.csproj", "*.sln" } },
            { "Unreal", new List<string> { "Binaries/", "Build/", "Intermediate/", "Saved/", "DerivedDataCache/", "*.sln" } },
            { "VisualStudio", new List<string> { ".vs/", "bin/", "obj/", "[Dd]ebug/", "[Rr]elease/", "*.user", "*.suo", "*.pdb", "*.ipch", "TestResults/" } },
            { "Java", new List<string> { "*.class", "*.jar", "target/", ".gradle/", "build/" } },
            { "Rust", new List<string> { "target/", "**/*.rs.bk" } },
            // .vs/ and out/ are what Visual Studio's CMake / "Open Folder" mode creates (with no .sln to trigger the VisualStudio template)
            { "C++", new List<string> { "build/", "bin/", "out/", ".vs/", "*.o", "*.obj", "*.exe", "*.pdb", "*.ilk", "CMakeCache.txt", "CMakeFiles/" } },
            { "Flutter", new List<string> { ".dart_tool/", "build/", ".packages", ".pub/", "ios/Flutter/Generated.xcconfig", "android/.gradle" } },
            { "Go", new List<string> { "bin/", "*.exe", "*.test", "vendor/" } },
            { "Ruby", new List<string> { "vendor/bundle/", "log/", "tmp/", ".byebug_history" } },
            { "Swift", new List<string> { ".build/", "DerivedData/", "xcuserdata/", "*.xcuserstate" } },
            { "Godot", new List<string> { ".godot/", ".import/", "export.cfg", "export_presets.cfg" } }
        };

        // Dependency and build output folders: huge, never contain project markers worth finding
        private static readonly HashSet<string> SkipDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "node_modules", "venv", ".venv", "__pycache__", "bin", "obj", "target", "build", "dist", ".gradle",
            ".vs", ".idea", "Library", "Temp", "Intermediate", "Binaries", "DerivedDataCache", "Saved", ".dart_tool",
            "Pods", "vendor", ".godot", ".import"
        };

        // =================================================================================
        // Project Detection & .gitignore
        // =================================================================================

        internal static List<string> DetectProjectTypes(string folderPath)
        {
            var detected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string root = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar);

            // A Unity project always has both of these at the top (just an "Assets" folder is common in other projects too)
            if (Directory.Exists(Path.Combine(root, "Assets")) && Directory.Exists(Path.Combine(root, "ProjectSettings"))) detected.Add("Unity");

            foreach (var info in EnumerateProjectEntries(root))
            {
                try
                {
                    var name = info.Name;
                    if (info is DirectoryInfo)
                    {
                        // .xcodeproj is a folder, not a file
                        if (name.EndsWith(".xcodeproj", StringComparison.OrdinalIgnoreCase)) detected.Add("Swift");
                        continue;
                    }

                    var ext = info.Extension;
                    var isTopLevel = string.Equals(Path.GetDirectoryName(info.FullName)?.TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase);
                    if (string.Equals(ext, ".py", StringComparison.OrdinalIgnoreCase)) detected.Add("Python");
                    if (string.Equals(name, "package.json", StringComparison.OrdinalIgnoreCase)) detected.Add("Node.js");
                    if (isTopLevel && string.Equals(ext, ".uproject", StringComparison.OrdinalIgnoreCase)) detected.Add("Unreal");
                    // .slnx is the solution format "dotnet new sln" creates since the .NET 9/10 SDKs
                    if (new[] { ".sln", ".slnx", ".csproj", ".fsproj", ".vbproj", ".vcxproj" }.Contains(ext, StringComparer.OrdinalIgnoreCase)) detected.Add("VisualStudio");
                    if (string.Equals(name, "pom.xml", StringComparison.OrdinalIgnoreCase)) detected.Add("Java");
                    if (string.Equals(name, "Cargo.toml", StringComparison.OrdinalIgnoreCase)) detected.Add("Rust");
                    if (string.Equals(name, "CMakeLists.txt", StringComparison.OrdinalIgnoreCase)) detected.Add("C++");
                    if (string.Equals(name, "pubspec.yaml", StringComparison.OrdinalIgnoreCase)) detected.Add("Flutter");
                    if (string.Equals(name, "go.mod", StringComparison.OrdinalIgnoreCase)) detected.Add("Go");
                    if (string.Equals(name, "Gemfile", StringComparison.OrdinalIgnoreCase)) detected.Add("Ruby");
                    if (string.Equals(name, "Package.swift", StringComparison.OrdinalIgnoreCase)) detected.Add("Swift");
                    if (string.Equals(name, "project.godot", StringComparison.OrdinalIgnoreCase)) detected.Add("Godot");
                }
                catch { continue; }

                if (detected.Count == GitIgnoreTemplates.Count) break;
            }

            // Stable, readable order
            return GitIgnoreTemplates.Keys.Where(detected.Contains).ToList();
        }

        // Walks the folder tree without descending into dependency/build folders, junctions or symlinks (a junction that
        // points back up the tree would loop forever). Other reparse points, like OneDrive placeholders, are walked normally.
        static IEnumerable<FileSystemInfo> EnumerateProjectEntries(string root, int maxEntries = 200_000, int maxDepth = 64)
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System, RecurseSubdirectories = false };
            var pending = new Stack<(DirectoryInfo Dir, int Depth)>();
            pending.Push((new DirectoryInfo(root), 0));
            int seen = 0;

            while (pending.Count > 0)
            {
                var (dir, depth) = pending.Pop();
                List<FileSystemInfo> entries;
                try
                {
                    entries = dir.EnumerateFileSystemInfos("*", options).ToList();
                }
                catch
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    if (++seen > maxEntries) yield break;
                    yield return entry;

                    bool descend = false;
                    try
                    {
                        descend = entry is DirectoryInfo
                            && depth < maxDepth
                            && !SkipDirectories.Contains(entry.Name)
                            && !entry.Name.EndsWith(".xcodeproj", StringComparison.OrdinalIgnoreCase)
                            && entry.LinkTarget == null;
                    }
                    catch { }
                    if (descend) pending.Push(((DirectoryInfo)entry, depth + 1));
                }
            }
        }

        // Windows/Office leftovers that no project wants: thumbnail caches, folder settings, Word/Excel lock files
        private static readonly List<string> WindowsJunk = new List<string> { "Thumbs.db", "desktop.ini", "~$*" };

        static List<string> SuggestGitIgnore(string folderPath)
        {
            LogVerbose("Analyzing project structure for .gitignore suggestions...");
            var detected = DetectProjectTypes(folderPath);
            if (detected.Count == 0) return detected;
            WriteLineColor(ConsoleColor.Yellow, $"Detected project type(s): {string.Join(", ", detected)}");

            // Each project type is offered once: when its "# Type" section is missing - e.g. a Node part added to a
            // Python project later - and the user hasn't said no to it before
            string ignorePath = Path.Combine(folderPath, ".gitignore");
            var existing = new HashSet<string>(File.Exists(ignorePath) ? File.ReadAllLines(ignorePath).Select(l => l.Trim()) : Enumerable.Empty<string>());
            var declined = new HashSet<string>(
                RunGit(true, "config", "--get-all", "gitsync.declinedgitignore").Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()),
                StringComparer.OrdinalIgnoreCase);
            var offered = detected.Where(t => !existing.Contains($"# {t}") && !declined.Contains(t)).ToList();

            // Only lines that aren't there yet, so running Sync again never duplicates them
            var additions = new StringBuilder();
            foreach (var type in offered.Append("Windows"))
            {
                var missing = (type == "Windows" ? WindowsJunk : GitIgnoreTemplates[type]).Where(line => existing.Add(line)).ToList();
                if (missing.Count == 0) continue;
                additions.AppendLine().AppendLine($"# {type}");
                foreach (var line in missing) additions.AppendLine(line);
            }
            if (offered.Count == 0 || additions.Length == 0) return detected;

            string question = File.Exists(ignorePath)
                ? $"Add the standard {string.Join(", ", offered)} entries to .gitignore?"
                : "Generate standard .gitignore?";
            if (!AskYesNo(question, true))
            {
                foreach (var type in offered) RunGit(true, "config", "--add", "gitsync.declinedgitignore", type);
                return detected;
            }

            var before = GetFilesToCommit();
            var ignoredBefore = GetTrackedIgnoredFiles();
            AppendToFile(ignorePath, additions.ToString());
            LogVerbose("Updated .gitignore with standard templates.");

            // Show what the new rules leave out, so a wrongly matched project file is noticed before it's missing on GitHub
            var after = new HashSet<string>(GetFilesToCommit());
            var leftOut = before.Where(f => !after.Contains(f)).ToList();
            if (leftOut.Count > 0)
            {
                Console.WriteLine($"These rules leave out {leftOut.Count} file(s), e.g.:");
                foreach (var f in leftOut.Take(5)) Console.WriteLine($"    {f}");
            }
            UntrackNowIgnoredFiles(ignoredBefore);
            return detected;
        }

        // .gitignore only affects files Git doesn't track yet: bin/ or node_modules/ committed before the rules existed
        // would keep being uploaded. Offers to stop tracking them (they stay on disk) - only the ones the new rules match,
        // not files the project's own older rules cover and that were committed on purpose ("git add -f").
        static void UntrackNowIgnoredFiles(HashSet<string> ignoredBefore)
        {
            var tracked = GetTrackedIgnoredFiles().Where(f => !ignoredBefore.Contains(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (tracked.Count == 0) return;

            WriteLineColor(ConsoleColor.Yellow, $"{tracked.Count} file(s) that are already in Git match the new rules, e.g.:");
            foreach (var f in tracked.Take(5)) Console.WriteLine($"    {f}");
            Console.WriteLine("They'd keep being uploaded. Stopping tracking keeps them on your disk, but removes them from the");
            Console.WriteLine("repository (and from other computers on their next pull).");
            if (!AskYesNo("Stop tracking them?", true)) return;

            // Paths go through stdin: a committed node_modules can mean more paths than a command line can hold
            var rm = ExecuteGit(new[] { "--literal-pathspecs", "rm", "--cached", "--quiet", "--pathspec-from-file=-", "--pathspec-file-nul" },
                silent: true, live: false, input: string.Join("\0", tracked) + "\0");
            if (rm.Success) filesIgnoredCount += tracked.Count;
            else ExplainGitFailure(rm, "Untracking the files");
        }

        // A .gitignore line matching exactly this one file or folder: anchored at the top, wildcards taken literally
        internal static string ToGitIgnoreEntry(string relativePath)
        {
            return "/" + EscapeGlob(relativePath.Replace('\\', '/').TrimStart('/'));
        }

        static string EscapeGlob(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in text)
            {
                if (c == '*' || c == '?' || c == '[') sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        // =================================================================================
        // LICENSE & README
        // =================================================================================

        static void SuggestLicense(string folderPath)
        {
            string[] existingNames = { "LICENSE", "LICENSE.md", "LICENSE.txt", "LICENCE", "LICENCE.md", "COPYING" };
            if (existingNames.Any(n => File.Exists(Path.Combine(folderPath, n)))) return;

            WriteLineColor(ConsoleColor.Cyan, "\nNo LICENSE file found.");
            Console.WriteLine("[1] MIT License");
            Console.WriteLine("[2] Apache License 2.0");
            Console.WriteLine("[3] GNU GPLv3");
            Console.WriteLine("[4] None");

            string choice = AskChoice("Choice (Enter = 4 None): ", "1", "2", "3", "4", "", "mit", "apache", "gpl", "none");
            choice = choice switch { "mit" => "1", "apache" => "2", "gpl" => "3", _ => choice };
            string author = RunGit(true, "config", "user.name").Output.Trim();
            string year = DateTime.Now.Year.ToString();
            string? content = null;

            switch (choice)
            {
                case "1": content = MitLicense(year, author); break;
                case "2": content = DownloadLicense("apache-2.0"); break;
                case "3": content = DownloadLicense("gpl-3.0"); break;
                default: return;
            }

            if (content == null)
            {
                WriteLineColor(ConsoleColor.Yellow, "Couldn't download the license text (offline?), so no LICENSE file was created.");
                Console.WriteLine("You can add one later on GitHub: Add file > Create new file > name it LICENSE > Choose a license template.");
                return;
            }

            WriteGeneratedFile(Path.Combine(folderPath, "LICENSE"), content);
            LogVerbose("LICENSE file created.");
        }

        internal static string MitLicense(string year, string author)
        {
            return string.Join("\n", new[]
            {
                "MIT License",
                "",
                $"Copyright (c) {year} {author}",
                "",
                "Permission is hereby granted, free of charge, to any person obtaining a copy",
                "of this software and associated documentation files (the \"Software\"), to deal",
                "in the Software without restriction, including without limitation the rights",
                "to use, copy, modify, merge, publish, distribute, sublicense, and/or sell",
                "copies of the Software, and to permit persons to whom the Software is",
                "furnished to do so, subject to the following conditions:",
                "",
                "The above copyright notice and this permission notice shall be included in all",
                "copies or substantial portions of the Software.",
                "",
                "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR",
                "IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,",
                "FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE",
                "AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER",
                "LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,",
                "OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE",
                "SOFTWARE.",
                ""
            });
        }

        // The full official text from GitHub's license API (the same templates GitHub offers when creating a repository)
        static string? DownloadLicense(string key)
        {
            try
            {
                LogVerbose($"Downloading the {key} license text...");
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("GitSync");
                    http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                    string json = http.GetStringAsync($"https://api.github.com/licenses/{key}").GetAwaiter().GetResult();
                    using (var doc = JsonDocument.Parse(json))
                    {
                        return doc.RootElement.GetProperty("body").GetString();
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"License download failed: {ex.Message}");
                return null;
            }
        }

        public static ProjectMetadata ParseProjectMetadata(string folder, List<string> types)
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
                        if (verMatch.Success)
                        {
                            meta.Version = verMatch.Groups[1].Value;
                            meta.HasVersion = true;
                        }

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
                        var verMatch = Regex.Match(content, "(?m)^version\\s*=\\s*\"(.*?)\"");
                        if (nameMatch.Success) meta.Name = nameMatch.Groups[1].Value;
                        if (verMatch.Success)
                        {
                            meta.Version = verMatch.Groups[1].Value;
                            meta.HasVersion = true;
                        }
                        meta.RunCommand = "cargo run";
                    }
                }
                else if (types.Contains("Python"))
                {
                    // The real entry file, so the README doesn't tell readers to run a main.py that doesn't exist
                    var topLevelPy = Directory.GetFiles(folder, "*.py").Select(Path.GetFileName).ToList();
                    string? entry = new[] { "main.py", "app.py", "run.py", "manage.py" }.FirstOrDefault(f => File.Exists(Path.Combine(folder, f)))
                        ?? (topLevelPy.Count == 1 ? topLevelPy[0] : null);
                    string run = entry != null ? $"python {entry}" : "python <your script>.py";
                    meta.RunCommand = File.Exists(Path.Combine(folder, "requirements.txt")) ? "pip install -r requirements.txt\n" + run : run;
                }
            }
            catch { }

            return meta;
        }

        static void SuggestReadme(string folderPath, List<string> types)
        {
            string[] existingNames = { "README.md", "README", "README.txt", "README.rst", "readme.markdown" };
            if (existingNames.Any(n => File.Exists(Path.Combine(folderPath, n)))) return;

            if (!AskYesNo("\nCreate default README.md?", true)) return;

            // Intelligent Parsing
            LogVerbose("Parsing project files for metadata...");
            var meta = ParseProjectMetadata(folderPath, types);

            string content = meta.HasVersion ? $"# {meta.Name} (v{meta.Version})\n\n" : $"# {meta.Name}\n\n";
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

            content += "\nGenerated by GitSync.\n";

            WriteGeneratedFile(Path.Combine(folderPath, "README.md"), content);
            LogVerbose($"Custom README.md created for {meta.Name}.");
        }

        // =================================================================================
        // Pre-commit Checks
        // =================================================================================

        // A folder with its own .git would be uploaded as an empty "link" instead of its files
        static void CheckEmbeddedRepositories()
        {
            // Without --directory git lists every untracked file, except that a nested repository shows up as "path/"
            var nested = SplitNul(RunGit(true, "ls-files", "-z", "--others", "--exclude-standard").Output)
                .Where(p => p.EndsWith("/") && (Directory.Exists(Path.Combine(p, ".git")) || File.Exists(Path.Combine(p, ".git"))))
                .ToList();

            foreach (var repo in nested)
            {
                string name = repo.TrimEnd('/');
                // One without any commit can't even be added as a link: "git add" would fail for the whole project
                bool canLink = RunGit(true, "-C", name, "rev-parse", "--verify", "--quiet", "HEAD").Success;
                WriteLineColor(ConsoleColor.Yellow, $"\n[WARNING] '{name}' is a separate Git repository inside your project.");
                Console.WriteLine(canLink ? "Git would upload it as an empty link instead of its files." : "It has no commits, so Git can't add it at all.");
                Console.WriteLine("[1] Skip it (Add to .gitignore)");
                if (canLink) Console.WriteLine("[2] Upload it anyway as a link (advanced)");
                Console.WriteLine("[3] Abort (delete its .git folder yourself if you want its files uploaded)");
                string choice = canLink ? AskChoice("Choice: ", "1", "2", "3") : AskChoice("Choice: ", "1", "3");
                if (choice == "3") throw new UserAbortException("Aborted so you can sort out the nested repository.");
                if (choice == "1")
                {
                    AppendToFile(Path.Combine(sessionFolder!, ".gitignore"), ToGitIgnoreEntry(name) + "/" + Environment.NewLine);
                    filesIgnoredCount++;
                }
            }
        }

        static void CheckLargeFiles(string folderPath)
        {
            WriteLineColor(ConsoleColor.Cyan, "\nScanning for large files (>100MB)...");

            bool lfsAvailable = RunGit(true, "lfs", "version").Output.Contains("git-lfs");
            long limit = 100L * 1024 * 1024;

            // Only files that would actually be committed (respects .gitignore, so node_modules etc. are skipped)
            var candidates = GetFilesToCommit();
            LogVerbose($"Scanning {candidates.Count} files...");
            var largeFiles = new List<(string RelPath, long Size)>();

            foreach (var rel in candidates)
            {
                try
                {
                    var info = new FileInfo(Path.Combine(folderPath, rel));
                    if (info.Exists && info.Length > limit) largeFiles.Add((rel, info.Length));
                }
                catch { }
            }

            if (largeFiles.Count == 0)
            {
                WriteLineColor(ConsoleColor.Green, "No large files found.");
                return;
            }

            LogVerbose($"Found {largeFiles.Count} files larger than 100MB.");
            var tracked = GetTrackedFiles();
            string ignorePath = Path.Combine(folderPath, ".gitignore");

            foreach (var (relPath, size) in largeFiles)
            {
                string extension = Path.GetExtension(relPath);

                // Check auto-ignores
                if (extension.Length > 0 && autoIgnoreExtensions.Contains(extension))
                {
                    LogVerbose($"Auto-skipping large file (extension '{extension}'): {relPath}");
                    UntrackIfTracked(relPath, tracked); // the new *.ext rule alone wouldn't stop an already committed one
                    continue;
                }

                // Already taken care of by .gitignore or Git LFS?
                if (!tracked.Contains(relPath) && RunGit(true, "check-ignore", "-q", "--", relPath).Success) continue;
                if (RunGit(true, "check-attr", "filter", "--", relPath).Output.Contains("filter: lfs")) continue;

                double sizeMB = Math.Round(size / 1024.0 / 1024.0, 2);
                WriteLineColor(ConsoleColor.Yellow, $"\n[WARNING] Large file: {relPath} ({sizeMB} MB) - GitHub rejects files over 100 MB.");

                while (true)
                {
                    Console.WriteLine("[1] Skip this file (Add to .gitignore)");
                    Console.WriteLine("[2] Abort");
                    Console.WriteLine("[3] Include anyway (the push will most likely be rejected)");
                    if (lfsAvailable) Console.WriteLine("[4] Track with Git LFS (recommended)");
                    if (extension.Length > 0) Console.WriteLine($"[5] Skip ALL files with extension '{extension}'");

                    string c = GetUserInput("Choice: ");

                    if (c == "1")
                    {
                        AppendToFile(ignorePath, ToGitIgnoreEntry(relPath) + Environment.NewLine);
                        UntrackIfTracked(relPath, tracked);
                        if (!RunGit(true, "check-ignore", "-q", "--no-index", "--", relPath).Success)
                        {
                            WriteLineColor(ConsoleColor.Yellow, "Hmm, .gitignore doesn't seem to cover that file (another rule may re-include it). Check .gitignore before pushing.");
                        }
                        filesIgnoredCount++;
                        break;
                    }
                    if (c == "2") throw new UserAbortException("Aborted at the large file check.");
                    if (c == "3") break;
                    if (c == "4" && lfsAvailable)
                    {
                        // --filename escapes wildcards; the leading "/" keeps a name like "!old.mp4" from reading as a negation
                        var track = RecordAppendBy(Path.Combine(folderPath, ".gitattributes"), () => RunGit(true, "lfs", "track", "--filename", "/" + relPath));
                        if (!track.Success)
                        {
                            ExplainGitFailure(track, "Git LFS");
                            continue;
                        }
                        if (!RunGit(true, "check-attr", "filter", "--", relPath).Output.Contains("filter: lfs"))
                        {
                            WriteLineColor(ConsoleColor.Yellow, "Git LFS didn't pick that file up - check .gitattributes, or skip the file instead.");
                            continue;
                        }
                        // A file committed earlier must be re-added for LFS to take over
                        UntrackIfTracked(relPath, tracked);
                        lfsTrackedCount++;
                        break;
                    }
                    if (c == "5" && extension.Length > 0)
                    {
                        // "*.json" for one huge dump would also drop package.json & co. - say so first
                        var smaller = candidates.Where(f => string.Equals(Path.GetExtension(f), extension, StringComparison.OrdinalIgnoreCase)
                                                         && !largeFiles.Any(l => l.RelPath == f)).ToList();
                        if (smaller.Count > 0)
                        {
                            WriteLineColor(ConsoleColor.Yellow, $"That would also leave out {smaller.Count} smaller '{extension}' file(s), e.g. {string.Join(", ", smaller.Take(3))}.");
                            if (!AskYesNo($"Leave out every '{extension}' file anyway?", false)) continue;
                        }
                        autoIgnoreExtensions.Add(extension);
                        AppendToFile(ignorePath, "*" + EscapeGlob(extension) + Environment.NewLine);
                        UntrackIfTracked(relPath, tracked);
                        LogVerbose($"Added *{extension} to .gitignore. Future files of this type will be skipped.");
                        filesIgnoredCount++;
                        break;
                    }
                }
            }
        }

        // .gitignore doesn't affect files that are already committed, so stop tracking them (the file itself stays on disk)
        static void UntrackIfTracked(string relPath, HashSet<string> tracked)
        {
            if (tracked.Contains(relPath)) RunGit(true, "--literal-pathspecs", "rm", "--cached", "--quiet", "--", relPath);
        }

        private static readonly (string Kind, Regex Pattern)[] SecretPatterns =
        {
            ("a private key", new Regex(@"-----BEGIN (?:RSA |DSA |EC |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY(?: BLOCK)?-----")),
            ("an AWS access key", new Regex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b")),
            ("a GitHub token", new Regex(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{22,})")),
            ("a Slack token", new Regex(@"\bxox[abposr]-[A-Za-z0-9-]{10,}")),
            ("a Google API key", new Regex(@"\bAIza[0-9A-Za-z_\-]{35}\b")),
            ("a Stripe live key", new Regex(@"\b[rs]k_live_[0-9A-Za-z]{20,}")),
            ("an AI API key", new Regex(@"\bsk-(?:ant-|proj-)?[A-Za-z0-9_\-]{32,}")),
            ("a hard-coded password or key", new Regex(@"(?i)\b(?:password|passwd|pwd|secret|api[_-]?key|access[_-]?token|auth[_-]?token|client[_-]?secret)\b[""']?\s*[:=]\s*[""']([^""'\s]{6,})[""']")),
            ("a password in a connection string", new Regex(@"(?i)\b(?:password|pwd)\s*=\s*([^;""'\s]{4,})\s*;")),
        };

        // "your_api_key_here", "${API_KEY}", "<password>", "changeme", ... are examples, not secrets
        private static readonly Regex PlaceholderValue = new Regex(
            @"(?i)^(your|my|example|sample|placeholder|changeme|change_me|dummy|fake|test|xxx|\*|<|\$\{|\{\{|%|todo|none|null|password|secret|process\.env|os\.environ)",
            RegexOptions.Compiled);

        // Cheap first pass: a line without any of these can't match a secret pattern, so most lines skip the full check
        private static readonly Regex SecretHint = new Regex(
            @"(?i)key|token|secret|pass|pwd|BEGIN|AKIA|ASIA|gh[pousr]_|github_pat|AIza|xox[abposr]-|[rs]k_live|sk-",
            RegexOptions.Compiled);

        internal static List<(int Line, string Kind)> FindSecrets(string text)
        {
            var hits = new List<(int Line, string Kind)>();
            if (!SecretHint.IsMatch(text)) return hits;
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!SecretHint.IsMatch(lines[i])) continue;
                foreach (var (kind, pattern) in SecretPatterns)
                {
                    var match = pattern.Match(lines[i]);
                    if (!match.Success) continue;
                    if (match.Groups.Count > 1 && match.Groups[1].Success && PlaceholderValue.IsMatch(match.Groups[1].Value)) continue;
                    hits.Add((i + 1, kind));
                    break;
                }
            }
            return hits;
        }

        // .env files, private keys and certificates are secrets by nature (but .env.example & co. are meant to be shared)
        internal static bool IsSensitiveFileName(string fileName)
        {
            string name = fileName.ToLowerInvariant();
            if (Regex.IsMatch(name, @"(example|sample|template|dist|\.pub)$")) return false;
            return name == ".env" || name.StartsWith(".env.")
                || name is "id_rsa" or "id_dsa" or "id_ecdsa" or "id_ed25519" or "credentials.json"
                || Regex.IsMatch(name, @"\.(pem|key|pfx|p12|keystore|jks)$");
        }

        // onlyChangedFiles: in a repository that is already on the remote, only new, modified and staged files - the rest
        // is published already (and flagging it every day teaches people to ignore the warning). Commits that aren't
        // pushed yet are checked separately before the push (FindSecretsInUnpushedCommits).
        static void ScanSecrets(string folderPath, bool onlyChangedFiles)
        {
            WriteLineColor(ConsoleColor.Cyan, "\nScanning for secrets...");
            var candidates = onlyChangedFiles ? GetChangedFiles() : GetFilesToCommit();
            LogVerbose($"Analyzing {candidates.Count} text file(s) for sensitive patterns...");

            // Only files that would actually be committed; whole text files up to 2MB
            var flagged = new List<(string File, string Reason)>();
            foreach (var rel in candidates)
            {
                string fileName = rel.Contains('/') ? rel.Substring(rel.LastIndexOf('/') + 1) : rel;
                if (IsSensitiveFileName(fileName))
                {
                    flagged.Add((rel, "this kind of file usually holds passwords or keys"));
                    continue;
                }

                string full = Path.Combine(folderPath, rel);
                try
                {
                    var info = new FileInfo(full);
                    if (!info.Exists || info.Length > 2 * 1024 * 1024 || IsProbablyBinary(full)) continue;
                    var hits = FindSecrets(File.ReadAllText(full));
                    if (hits.Count > 0) flagged.Add((rel, $"looks like {hits[0].Kind} on line {hits[0].Line}" + (hits.Count > 1 ? $", +{hits.Count - 1} more" : "")));
                }
                catch { }
            }

            if (flagged.Count == 0)
            {
                WriteLineColor(ConsoleColor.Green, "No secrets found.");
                return;
            }

            var tracked = GetTrackedFiles();
            foreach (var (file, reason) in flagged)
            {
                WriteLineColor(ConsoleColor.Red, $"[CRITICAL] Potential secret in: {file} ({reason})");
                // Already uploaded once: keeping it out from now on can't take it back
                if (tracked.Contains(file) && RunGit(true, "--literal-pathspecs", "log", "-1", "--format=%h", "--remotes=origin", "--", file).Output.Trim().Length > 0)
                {
                    WriteLineColor(ConsoleColor.Yellow, "    This file is on the remote already - if that value was in it there, it's public: change that password or key.");
                }
            }

            Console.WriteLine("\nAnything pushed to GitHub can be seen and copied - even after you delete it again.");
            Console.WriteLine("[1] Keep these files out of Git (Add to .gitignore) and continue");
            Console.WriteLine("[2] Continue anyway (I'm sure they're safe)");
            Console.WriteLine("[3] Abort");
            string choice = AskChoice("Choice: ", "1", "2", "3");
            if (choice == "3") throw new UserAbortException("Aborted at the secrets check.");
            if (choice == "2")
            {
                // Exactly these versions aren't asked about again before the push - an older version of the same file in a
                // commit that isn't pushed yet still is, because the user never saw what's in it
                var present = flagged.Select(f => f.File).Where(f => File.Exists(Path.Combine(folderPath, f))).ToList();
                if (present.Count > 0)
                {
                    var ids = RunGit(true, new[] { "hash-object", "--" }.Concat(present).ToArray()).Output;
                    foreach (var id in ids.Split('\n', StringSplitOptions.RemoveEmptyEntries)) secretBlobsAccepted.Add(id.Trim());
                }
                secretsStatus = "Warning Ignored";
                return;
            }

            if (flagged.Any(f => tracked.Contains(f.File)))
            {
                Console.WriteLine("Files that are already in Git are removed from the repository (your copy stays). Other computers");
                Console.WriteLine("lose theirs on their next pull - unless they use GitSync, which keeps ignored local copies.");
            }
            foreach (var (file, _) in flagged)
            {
                KeepOutOfGit(file, tracked); // the push still checks that it isn't inside commits that aren't pushed yet
            }
            secretsStatus = $"{flagged.Count} file(s) kept out of Git";
            LogVerbose($"Added {flagged.Count} file(s) to .gitignore.");
        }

        // Adds a file to .gitignore (once) and stops tracking it; the file itself stays on disk
        static void KeepOutOfGit(string relPath, HashSet<string> tracked)
        {
            string ignorePath = Path.Combine(sessionFolder!, ".gitignore");
            string entry = ToGitIgnoreEntry(relPath);
            if (!File.Exists(ignorePath) || !File.ReadLines(ignorePath).Any(l => l.Trim() == entry))
            {
                AppendToFile(ignorePath, entry + Environment.NewLine);
            }
            UntrackIfTracked(relPath, tracked);
            filesIgnoredCount++;
        }

        // Extensions that never hold text worth scanning; skipping them keeps the check of a big history quick
        private static readonly HashSet<string> BinaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff", ".psd", ".mp3", ".wav", ".ogg", ".flac",
            ".mp4", ".mov", ".avi", ".mkv", ".webm", ".zip", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".tar", ".exe", ".dll",
            ".pdb", ".so", ".dylib", ".lib", ".obj", ".pdf", ".ttf", ".otf", ".woff", ".woff2", ".fbx", ".blend", ".unitypackage",
        };

        // Secrets inside commits that haven't been pushed yet - including commits made outside GitSync (VS Code, plain git)
        // and files that were changed or deleted again since. The folder scan can't see those, but the push would publish
        // them. Only content that isn't on the remote already is read. One hit per flagged version (Blob) of a file.
        internal static List<(string File, string Blob, string Reason)> FindSecretsInUnpushedCommits()
        {
            var flagged = new List<(string File, string Blob, string Reason)>();
            var objects = RunGit(true, "rev-list", "--objects", "HEAD", "--not", "--remotes=origin");
            if (!objects.Success || objects.Output.Trim().Length == 0) return flagged;

            var info = ExecuteGit(new[] { "cat-file", "--batch-check=%(objectname) %(objecttype) %(objectsize) %(rest)" }, silent: true, live: false, input: objects.Output);
            var toRead = new List<(string Id, string Path)>();
            long budget = 256L * 1024 * 1024;
            int skipped = 0;
            foreach (var line in info.Output.Split('\n'))
            {
                var parts = line.TrimEnd('\r').Split(' ', 4);
                if (parts.Length < 4 || parts[1] != "blob" || !long.TryParse(parts[2], out long size)) continue;
                string path = parts[3];
                string name = path.Contains('/') ? path.Substring(path.LastIndexOf('/') + 1) : path;
                if (IsSensitiveFileName(name))
                {
                    flagged.Add((path, parts[0], "this kind of file usually holds passwords or keys"));
                    continue;
                }
                if (size > 2 * 1024 * 1024 || BinaryExtensions.Contains(Path.GetExtension(name))) continue;
                if (size > budget)
                {
                    skipped++;
                    continue;
                }
                budget -= size;
                toRead.Add((parts[0], path));
            }
            if (skipped > 0) LogVerbose($"({skipped} more file version(s) weren't read - over 256 MB of text in total.)");
            if (toRead.Count == 0) return flagged;

            // "<id> blob <size>\n<content>\n" for every object asked for
            var (ok, data) = RunGitForBytes(new[] { "cat-file", "--batch" }, string.Concat(toRead.Select(b => b.Id + "\n")));
            if (!ok) return flagged;
            int pos = 0;
            foreach (var (id, path) in toRead)
            {
                int headerEnd = Array.IndexOf(data, (byte)'\n', pos);
                if (headerEnd < 0) break;
                var header = Encoding.ASCII.GetString(data, pos, headerEnd - pos).Split(' ');
                pos = headerEnd + 1;
                if (header.Length < 3) continue; // "<id> missing" - no content follows
                if (!int.TryParse(header[2], out int size) || pos + size > data.Length) break;

                var content = new ReadOnlySpan<byte>(data, pos, size);
                pos += size + 1;
                if (content.Slice(0, Math.Min(size, 8000)).IndexOf((byte)0) >= 0) continue; // binary
                var hits = FindSecrets(Encoding.UTF8.GetString(content));
                if (hits.Count > 0) flagged.Add((path, id, $"looks like {hits[0].Kind} on line {hits[0].Line}"));
            }
            return flagged.Distinct().ToList();
        }

        // onlyChangedFiles: TODOs in files that haven't changed since the last sync were already shown before
        static bool CheckQualityGates(string folder, bool onlyChangedFiles)
        {
            Console.WriteLine("\n[Quality Gate] Checking code quality...");
            var perFile = new List<(string File, int Count)>();

            // TODO Scanner - only files that will be committed, text files under 1MB
            foreach (var rel in onlyChangedFiles ? GetChangedFiles() : GetFilesToCommit())
            {
                string full = Path.Combine(folder, rel);
                try
                {
                    var info = new FileInfo(full);
                    if (!info.Exists || info.Length >= 1024 * 1024 || IsProbablyBinary(full)) continue;
                    int count = File.ReadLines(full).Count(line => line.Contains("TODO:") || line.Contains("FIXME:"));
                    if (count > 0) perFile.Add((rel, count));
                }
                catch { }
            }

            int issues = perFile.Sum(p => p.Count);
            if (issues > 0)
            {
                WriteLineColor(ConsoleColor.Yellow, $"Found {issues} TODO/FIXME comments.");
                foreach (var (file, count) in perFile.OrderByDescending(p => p.Count).Take(5))
                {
                    Console.WriteLine($"    {count,4}  {file}");
                }

                string choice = AskChoice("Issues found. [1] Abort, [2] Push Anyway (MASTER OVERRIDE) (Enter = 2): ", "1", "2", "");
                if (choice == "1") return false;
            }
            else
            {
                Console.WriteLine("No TODOs found. Clean.");
            }

            return true;
        }

        static bool IsProbablyBinary(string path)
        {
            var buffer = new byte[8000];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int read = fs.Read(buffer, 0, buffer.Length);
                return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
            }
        }
    }
}
