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
        internal static readonly Dictionary<string, List<string>> GitIgnoreTemplates = new Dictionary<string, List<string>>
        {
            { "Python", new List<string> { "*.pyc", "__pycache__/", "venv/", ".venv/", "env/", "*.egg-info/" } },
            { "Node.js", new List<string> { "node_modules/", "npm-debug.log", "dist/", ".env" } },
            // Anchored to the project root: Assets/ may legitimately contain folders called Library, Build, ...
            { "Unity", new List<string> { "/[Ll]ibrary/", "/[Tt]emp/", "/[Oo]bj/", "/[Bb]uild/", "/[Bb]uilds/", "/[Ll]ogs/", "/[Uu]ser[Ss]ettings/", "*.csproj", "*.sln" } },
            // Build/ minus what's made by hand there - the game's icons and pak blacklists (as GitHub's own Unreal template does)
            { "Unreal", new List<string> { "Binaries/", "Build/*", "!Build/*/", "Build/*/**", "!Build/*/PakBlacklist*.txt", "!Build/**/*.ico", "Intermediate/", "Saved/", "DerivedDataCache/", "*.sln" } },
            { "VisualStudio", new List<string> { ".vs/", "bin/", "obj/", "[Dd]ebug/", "[Rr]elease/", "*.user", "*.suo", "*.pdb", "*.ipch", "TestResults/" } },
            { "Java", new List<string> { "*.class", "*.jar", "target/", ".gradle/", "build/" } },
            { "Rust", new List<string> { "target/", "**/*.rs.bk" } },
            // .vs/ and out/ are what Visual Studio's CMake / "Open Folder" mode creates (with no .sln to trigger the VisualStudio template)
            { "C++", new List<string> { "build/", "bin/", "out/", ".vs/", "*.o", "*.obj", "*.exe", "*.pdb", "*.ilk", "CMakeCache.txt", "CMakeFiles/" } },
            { "Flutter", new List<string> { ".dart_tool/", "build/", ".packages", ".pub/", "ios/Flutter/Generated.xcconfig", "android/.gradle" } },
            // Not vendor/: a Go project that has one keeps it on purpose (GitHub's own template leaves that line commented out)
            { "Go", new List<string> { "bin/", "*.exe", "*.test" } },
            { "Ruby", new List<string> { "vendor/bundle/", "log/", "tmp/", ".byebug_history" } },
            { "Swift", new List<string> { ".build/", "DerivedData/", "xcuserdata/", "*.xcuserstate" } },
            { "Godot", new List<string> { ".godot/", ".import/", "export.cfg", "export_presets.cfg" } },
            // Last, so their "!" lines come after any rule they make an exception to. The Gradle wrapper's jar is needed to build:
            // "*.jar" rules would hide it otherwise.
            { "Gradle", new List<string> { ".gradle/", "**/build/", "!**/src/**/build/", "local.properties", ".kotlin/", ".cxx/", ".externalNativeBuild/", "captures/", "!gradle-wrapper.jar" } },
            { "PHP", new List<string> { "/vendor/", "composer.phar" } },
            { "Jupyter", new List<string> { ".ipynb_checkpoints/" } },
            { "PlatformIO", new List<string> { ".pio/", ".vscode/.browse.c_cpp.db*", ".vscode/c_cpp_properties.json", ".vscode/launch.json", ".vscode/ipch" } },
            { "LaTeX", new List<string> { "*.aux", "*.log", "*.toc", "*.out", "*.fls", "*.fdb_latexmk", "*.synctex.gz", "*.bbl", "*.blg", "*.bcf", "*.run.xml", "*.dvi", "*.xdv", "*.lof", "*.lot", "*.nav", "*.snm" } },
        };

        // Dependency and build output folders: huge, never contain project markers worth finding
        private static readonly HashSet<string> SkipDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "node_modules", "venv", ".venv", "__pycache__", "bin", "obj", "target", "build", "dist", ".gradle",
            ".vs", ".idea", "Library", "Temp", "Intermediate", "Binaries", "DerivedDataCache", "Saved", ".dart_tool",
            "Pods", "vendor", ".godot", ".import", ".ipynb_checkpoints", ".pio", ".kotlin"
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
                    if (new[] { "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts" }.Contains(name, StringComparer.OrdinalIgnoreCase)) detected.Add("Gradle");
                    if (string.Equals(name, "composer.json", StringComparison.OrdinalIgnoreCase)) detected.Add("PHP");
                    if (string.Equals(ext, ".ipynb", StringComparison.OrdinalIgnoreCase)) detected.Add("Jupyter");
                    if (string.Equals(name, "platformio.ini", StringComparison.OrdinalIgnoreCase)) detected.Add("PlatformIO");
                    // A LaTeX document at the top - not a paper.tex somewhere in another kind of project's docs
                    if (isTopLevel && string.Equals(ext, ".tex", StringComparison.OrdinalIgnoreCase)) detected.Add("LaTeX");
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
            // Always asked, also in a batch: new rules can leave out folders (bin/, build/) that hold someone's real work
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

            if (UsualAnswer("\nNo LICENSE file - add one?", "no")) return;
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

            if (!(UsualAnswer("\nCreate default README.md?", "yes") || AskYesNo("\nCreate default README.md?", true))) return;

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

        // =================================================================================
        // Files the project needs that a .gitignore leaves out
        // =================================================================================

        // Project and build definitions: never build output, and a project can't be opened or built without them
        private static readonly HashSet<string> CoreFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CMakeLists.txt", "CMakePresets.json", "Makefile", "GNUmakefile", "meson.build", "configure.ac", "package.json",
            "tsconfig.json", "pyproject.toml", "setup.py", "setup.cfg", "requirements.txt", "Pipfile", "Cargo.toml", "go.mod",
            "pom.xml", "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts", "vcpkg.json",
            "vcpkg-configuration.json", "conanfile.txt", "conanfile.py", "project.godot", "Gemfile", "composer.json",
            "pubspec.yaml", "Podfile", "Package.swift", "Dockerfile", "AndroidManifest.xml", "Directory.Build.props",
            "Directory.Build.targets", "Directory.Packages.props", "global.json", "gradlew", "gradlew.bat", "gradle-wrapper.jar",
            "gradle-wrapper.properties", "platformio.ini",
        };
        private static readonly HashSet<string> CoreFileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".sln", ".slnx", ".vcxproj", ".filters", ".csproj", ".fsproj", ".vbproj", ".vcproj", ".uproject", ".uplugin", ".pro", ".pri",
        };
        // Source code is written by hand: part of the project wherever it is - except in build and dependency folders
        private static readonly HashSet<string> SourceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".c", ".cc", ".cpp", ".cxx", ".c++", ".h", ".hh", ".hpp", ".hxx", ".inl", ".ixx", ".cppm", ".cs", ".py", ".js", ".jsx",
            ".mjs", ".ts", ".tsx", ".java", ".kt", ".go", ".rs", ".swift", ".m", ".mm", ".rb", ".php", ".lua", ".gd", ".gdshader",
            ".shader", ".hlsl", ".glsl", ".vue", ".svelte", ".dart", ".scala", ".hs", ".fs", ".vb", ".sh", ".ps1", ".cmake",
            ".proto", ".xaml", ".razor", ".cu", ".cuh", ".ino", ".ipynb", ".tex",
        };

        // What kind of project a folder holds - it decides which of the files its .gitignore leaves out are the project's own
        internal sealed class ProjectShape
        {
            public string Root = "";
            public bool Unity, Unreal, Godot;
            public bool InSourceCMake;      // CMake built right in the project folder: its Makefiles sit next to the sources
            public Dictionary<string, string?> GeneratedTrees = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        internal static ProjectShape ReadProjectShape(string root)
        {
            var shape = new ProjectShape { Root = root };
            try
            {
                shape.Unity = Directory.Exists(Path.Combine(root, "Assets")) && Directory.Exists(Path.Combine(root, "ProjectSettings"));
                shape.Unreal = Directory.EnumerateFiles(root, "*.uproject").Any();
                shape.Godot = File.Exists(Path.Combine(root, "project.godot"));
                shape.InSourceCMake = File.Exists(Path.Combine(root, "CMakeCache.txt"));
            }
            catch { }
            return shape;
        }

        // A folder that a tool fills completely, recognised by the marker it leaves at its top: a CMake build folder
        // (CMakeCache.txt), a Python virtual environment (pyvenv.cfg) or a conda environment (conda-meta) - whatever its name
        internal static string? GeneratedTreeKind(string folder)
        {
            try
            {
                if (File.Exists(Path.Combine(folder, "CMakeCache.txt"))) return "CMake build folder";
                if (File.Exists(Path.Combine(folder, "pyvenv.cfg"))) return "Python environment";
                if (Directory.Exists(Path.Combine(folder, "conda-meta"))) return "conda environment";
            }
            catch { }
            return null;
        }

        // What CMake writes when it builds inside the project folder itself (next to every CMakeLists.txt)
        static bool IsCMakeOutputName(string name) =>
            Regex.IsMatch(name, @"^(Makefile|CMakeCache\.txt|cmake_install\.cmake|CTestTestfile\.cmake|CPack(Source)?Config\.cmake|compile_commands\.json|build\.ninja|\.ninja_(deps|log))$|\.(sln|vcxproj|filters)$", RegexOptions.IgnoreCase);

        // Why a file the .gitignore leaves out is part of the project itself - or null when it isn't: build output,
        // dependencies, leftovers, and passwords and keys (those stay out on purpose). path: relative to the project's top
        // folder, "/" separated.
        internal static string? CoreFileReason(ProjectShape shape, string path)
        {
            var parts = path.Split('/');
            string name = parts[^1];
            string ext = Path.GetExtension(name);
            var folders = parts.Take(parts.Length - 1).ToList();
            if (IsSensitiveFileName(name) || IsJunkFileName(name)) return null;
            if (folders.Count > 0 && folders[0].Equals(BackupFolderName, StringComparison.OrdinalIgnoreCase)) return null; // GitSync's own copies
            if (folders.Any(IsAlwaysGeneratedFolder) || InGeneratedTree(shape, folders) != null) return null;
            if (shape.InSourceCMake && IsCMakeOutputName(name)) return null;

            // What a game engine's project is made of
            string top = folders.Count > 0 ? folders[0] : "";
            if (shape.Unity && (top.Equals("Assets", StringComparison.OrdinalIgnoreCase) || top.Equals("ProjectSettings", StringComparison.OrdinalIgnoreCase)
                    || path.Equals("Packages/manifest.json", StringComparison.OrdinalIgnoreCase) || path.Equals("Packages/packages-lock.json", StringComparison.OrdinalIgnoreCase)))
            {
                // Unity's own folders: the Unity rule decides, also when it says "left out on purpose"
                return UnityLeavesOutOnPurpose(path) ? null : "part of the Unity project";
            }
            if (shape.Unreal && new[] { "Source", "Content", "Config" }.Contains(top, StringComparer.OrdinalIgnoreCase)) return "part of the Unreal project";
            // Build/ is mostly generated, but the game's icons and pak blacklists in it are made by hand
            if (shape.Unreal && top.Equals("Build", StringComparison.OrdinalIgnoreCase)
                && (ext.Equals(".ico", StringComparison.OrdinalIgnoreCase) || name.StartsWith("PakBlacklist", StringComparison.OrdinalIgnoreCase))) return "part of the Unreal project (its icon or pak list)";
            if (shape.Godot && new[] { ".gd", ".tscn", ".tres", ".gdshader", ".cs", ".godot" }.Contains(ext, StringComparer.OrdinalIgnoreCase)) return "part of the Godot project";

            // Anywhere but in build output: a Visual Studio or CMake build folder holds generated copies of these
            if (folders.Any(f => IgnoredGeneratedFolders.Contains(f))) return null;
            if (CoreFileNames.Contains(name) || CoreFileExtensions.Contains(ext))
            {
                if (shape.Unity && folders.Count == 0 && (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase) || ext.Equals(".sln", StringComparison.OrdinalIgnoreCase))) return null; // Unity writes those
                if (shape.Unreal && new[] { ".sln", ".vcxproj", ".filters" }.Contains(ext, StringComparer.OrdinalIgnoreCase)) return null;  // so does Unreal
                return "a project or build file";
            }
            if (SourceExtensions.Contains(ext) && !LooksGenerated(name)) return "source code";
            return null;
        }

        // Code that tools write: x.g.cs, x.generated.h, x.gen.cpp, x.pb.cc, moc_x.cpp, AssemblyInfo.cs...
        static bool LooksGenerated(string name) =>
            Regex.IsMatch(name, @"\.(g|g\.i|generated|gen|pb)\.[^.]+$|_generated\.[^.]+$|Assembly(Info|Attributes)\.cs$|^(moc|ui|qrc)_", RegexOptions.IgnoreCase);

        // What Unity's standard .gitignore leaves out of Assets on purpose: debug symbols, store tools, Addressables builds
        static bool UnityLeavesOutOnPurpose(string path) =>
            Regex.IsMatch(path, @"(^|/)(AssetStoreTools|Plugins/Editor/JetBrains)|\.(pidb|pdb|mdb)(\.meta)?$|(^|/)(sysinfo\.txt|crashlytics-build\.properties)$|AddressableAssetsData/.*\.bin|StreamingAssets/aa(/|\.meta$)", RegexOptions.IgnoreCase);

        // Inside a folder a tool fills completely (see GeneratedTreeKind): "build/ (CMake build folder)", or null
        internal static string? InGeneratedTree(ProjectShape shape, List<string> folders)
        {
            for (int i = 1; i <= folders.Count; i++)
            {
                string dir = string.Join("/", folders.Take(i));
                if (!shape.GeneratedTrees.TryGetValue(dir, out string? kind))
                {
                    shape.GeneratedTrees[dir] = kind = GeneratedTreeKind(Path.Combine(shape.Root, dir.Replace('/', Path.DirectorySeparatorChar)));
                }
                if (kind != null) return $"{dir}/ ({kind})";
            }
            return null;
        }

        // Which ignore rule leaves each file out (where it is written, its line, the pattern). where: how git finds the project
        // (-C <folder>, or --git-dir/--work-tree for a plain folder)
        internal static Dictionary<string, (string Source, string Line, string Pattern)> IgnoreRules(IEnumerable<string> paths, params string[] where)
        {
            var args = where.Concat(new[] { "check-ignore", "-v", "--no-index", "-z", "--stdin" }).ToArray();
            var result = ExecuteGit(args, silent: true, live: false, input: string.Concat(paths.Select(p => p + "\0")));
            var fields = result.Output.Split('\0');
            var rules = new Dictionary<string, (string Source, string Line, string Pattern)>();
            // "<source>\0<line>\0<pattern>\0<path>\0" for every path a rule matches
            for (int i = 0; i + 3 < fields.Length; i += 4)
            {
                if (fields[i + 3] != "") rules[fields[i + 3]] = (fields[i], fields[i + 1], fields[i + 2]);
            }
            return rules;
        }

        internal static string DescribeRule((string Source, string Line, string Pattern) rule) => $"\"{rule.Pattern}\" ({rule.Source}, line {rule.Line})";

        // A rule that names exactly this one file ("local_settings.py", "/src/config_local.h") was written for it on purpose -
        // unlike "*.txt" or "src/", which take along files nobody meant
        internal static bool IsRuleForThisFileOnly(string pattern, string path)
        {
            string p = pattern.Trim();
            if (p.Length == 0 || p.StartsWith("!") || p.EndsWith("/") || p.IndexOfAny(new[] { '*', '?', '[' }) >= 0) return false;
            p = p.TrimStart('/');
            string name = path.Substring(path.LastIndexOf('/') + 1);
            return p.Equals(name, StringComparison.OrdinalIgnoreCase) || p.Equals(path, StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/" + p, StringComparison.OrdinalIgnoreCase);
        }

        // Files the user chose to keep out although they look like part of the project - not asked about again. Kept in the
        // repository's own .git folder, so it never leaves this computer.
        static string KeepOutListPath(string root) => GitPathOf(root, "--git-path", "gitsync-keep-out.txt");

        internal static HashSet<string> ReadKeepOutList(string root)
        {
            try
            {
                string file = KeepOutListPath(root);
                return file != "" && File.Exists(file)
                    ? new HashSet<string>(File.ReadAllLines(file).Where(l => l.Length > 0), StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
            }
            catch
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }
        }

        // The project's own files that its .gitignore leaves out by mistake (root: the repository's top folder) - not the ones
        // a rule names on purpose, nor ones the user already chose to keep out
        internal static List<(string Path, string Why, string Rule)> FindIgnoredCoreFiles(string root)
        {
            var found = new List<(string Path, string Why, string Rule)>();
            var ignored = SplitNul(RunGit(true, "-C", root, "ls-files", "-z", "--others", "--ignored", "--exclude-standard").Output);
            if (ignored.Count == 0) return found;
            var shape = ReadProjectShape(root);
            var keepOut = ReadKeepOutList(root);
            var candidates = ignored.Where(p => !keepOut.Contains(p)).Select(p => (Path: p, Why: CoreFileReason(shape, p))).Where(c => c.Why != null).ToList();
            if (candidates.Count == 0) return found;
            var rules = IgnoreRules(candidates.Select(c => c.Path), "-C", root);
            foreach (var (path, why) in candidates)
            {
                bool hasRule = rules.TryGetValue(path, out var rule);
                if (hasRule && IsRuleForThisFileOnly(rule.Pattern, path)) continue;
                found.Add((path, why!, hasRule ? DescribeRule(rule) : ""));
            }
            return found;
        }

        // Files the project needs that its .gitignore leaves out would never be uploaded - usually a rule that is too broad
        // ("*.txt" takes CMakeLists.txt along, "bin/" a plugin's DLLs). Names the rule, and offers to upload them anyway: once
        // they're in Git, the rule no longer applies to them. A "no" is remembered.
        internal static void IncludeIgnoredCoreFiles(string root)
        {
            var core = FindIgnoredCoreFiles(root);
            if (core.Count == 0) return;
            WriteLineColor(ConsoleColor.Yellow, $"\n[WARNING] Your .gitignore leaves out {core.Count} file(s) the project needs - they'd never be uploaded:");
            foreach (var (path, why, rule) in core.Take(10))
            {
                Console.WriteLine($"    {path}  ({why}" + (rule != "" ? $" - left out by {rule})" : ")"));
            }
            if (core.Count > 10) Console.WriteLine($"    ... and {core.Count - 10} more");
            // Always asked, also in a batch: it overrides the project's own ignore rules
            if (!AskYesNo("Upload them anyway?", true))
            {
                try
                {
                    string list = KeepOutListPath(root);
                    if (list != "") File.AppendAllLines(list, core.Select(c => c.Path));
                }
                catch { }
                Console.WriteLine("OK - they stay out, and GitSync won't ask about them again.");
                return;
            }

            var add = ExecuteGit(new[] { "-C", root, "--literal-pathspecs", "add", "-f", "--pathspec-from-file=-", "--pathspec-file-nul" },
                silent: true, live: false, input: string.Concat(core.Select(c => c.Path + "\0")));
            if (add.Success) LogVerbose($"Added {core.Count} file(s) the .gitignore left out - from now on they're uploaded like the rest.");
            else ExplainGitFailure(add, "Adding the files");
        }

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

        // GitHub refuses files over 100 MB. A folder used as the remote (a NAS, a USB drive) takes any size, and other hosts
        // have their own rules - so only a GitHub remote needs the check.
        internal static bool RemoteIsGitHub()
        {
            string? web = HasRemote("origin") ? ToWebUrl(GetRemoteUrl()) : null;
            return web != null && Uri.TryCreate(web, UriKind.Absolute, out var uri)
                && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase));
        }

        // Files over GitHub's 100 MB limit: one question for all of them, with the answer that fits as the default - stored
        // with Git LFS when it's installed (they're uploaded as usual), otherwise left out (they stay on disk)
        internal static void CheckLargeFiles(string folderPath)
        {
            if (!RemoteIsGitHub()) return;
            WriteLineColor(ConsoleColor.Cyan, "\nScanning for large files (>100MB)...");
            long limit = 100L * 1024 * 1024;

            // Only files that would actually be committed (respects .gitignore, so node_modules etc. are skipped) and that
            // aren't stored with Git LFS already
            var tracked = GetTrackedFiles();
            var largeFiles = new List<(string RelPath, long Size)>();
            foreach (var rel in GetFilesToCommit())
            {
                try
                {
                    var info = new FileInfo(Path.Combine(folderPath, rel));
                    if (!info.Exists || info.Length <= limit) continue;
                    if (RunGit(true, "check-attr", "filter", "--", rel).Output.Contains("filter: lfs")) continue;
                    largeFiles.Add((rel, info.Length));
                }
                catch { }
            }
            if (largeFiles.Count == 0)
            {
                WriteLineColor(ConsoleColor.Green, "No large files found.");
                return;
            }

            WriteLineColor(ConsoleColor.Yellow, $"\n[WARNING] {largeFiles.Count} file(s) are over GitHub's 100 MB limit, which it refuses:");
            foreach (var (rel, size) in largeFiles.Take(10)) Console.WriteLine($"    {rel} ({Math.Round(size / 1024.0 / 1024.0, 1)} MB)");
            if (largeFiles.Count > 10) Console.WriteLine($"    ... and {largeFiles.Count - 10} more");

            bool lfsAvailable = RunGit(true, "lfs", "version").Output.Contains("git-lfs");
            bool useLfs;
            if (lfsAvailable)
            {
                Console.WriteLine("Git LFS uploads files this big as usual (GitHub keeps them apart from the code).");
                string choice = AskChoice("Enter = store them with Git LFS (recommended), s = leave them out (they stay on your disk), c = cancel: ", "", "s", "c");
                if (choice == "c") throw new UserAbortException("Cancelled at the large file check.");
                useLfs = choice == "";
            }
            else
            {
                Console.WriteLine("Files this big need Git LFS (it comes with Git for Windows). Without it they can only be left out for now.");
                if (AskChoice("Enter = leave them out (they stay on your disk), c = cancel: ", "", "c") == "c") throw new UserAbortException("Cancelled at the large file check.");
                useLfs = false;
            }

            string ignorePath = Path.Combine(folderPath, ".gitignore");
            foreach (var (rel, _) in largeFiles)
            {
                if (useLfs)
                {
                    // --filename escapes wildcards; the leading "/" keeps a name like "!old.mp4" from reading as a negation
                    var track = RecordAppendBy(Path.Combine(folderPath, ".gitattributes"), () => RunGit(true, "lfs", "track", "--filename", "/" + rel));
                    if (track.Success && RunGit(true, "check-attr", "filter", "--", rel).Output.Contains("filter: lfs"))
                    {
                        UntrackIfTracked(rel, tracked); // a file committed earlier must be added again for LFS to take over
                        lfsTrackedCount++;
                        continue;
                    }
                    WriteLineColor(ConsoleColor.Yellow, $"Git LFS didn't take {rel} - it's left out instead (it stays on your disk).");
                }
                AppendToFile(ignorePath, ToGitIgnoreEntry(rel) + Environment.NewLine);
                UntrackIfTracked(rel, tracked);
                if (!RunGit(true, "check-ignore", "-q", "--no-index", "--", rel).Success)
                {
                    WriteLineColor(ConsoleColor.Yellow, $".gitignore doesn't seem to cover {rel} (another rule may bring it back) - check .gitignore before pushing.");
                }
                filesIgnoredCount++;
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
            // (?![\w-]) instead of \b at the end: these tokens can end in '-', which \b doesn't see as the end of a word
            ("a Discord bot token", new Regex(@"(?<![\w-])[MNO][A-Za-z\d_-]{22,25}\.[A-Za-z\d_-]{6}\.[A-Za-z\d_-]{27,38}(?![\w-])")),
            ("a Discord webhook link", new Regex(@"(?i)\bdiscord(?:app)?\.com/api/webhooks/\d+/[A-Za-z0-9_-]{50,}")),
            ("a Telegram bot token", new Regex(@"(?<![\w-])\d{8,10}:[A-Za-z0-9_-]{35}(?![\w-])")),
            ("an npm token", new Regex(@"\bnpm_[A-Za-z0-9]{36}\b|(?i)_authToken\s*=\s*([^\s$""']{8,})")),
            ("a Hugging Face token", new Regex(@"\bhf_[A-Za-z0-9]{34,}\b")),
            ("a GitLab token", new Regex(@"\bglpat-[A-Za-z0-9_-]{20,}")),
            ("a SendGrid key", new Regex(@"\bSG\.[A-Za-z0-9_-]{22}\.[A-Za-z0-9_-]{43}(?![\w-])")),
            // mongodb+srv://app:Hunter2@cluster0... - the password sits right in the address
            ("a database address with a password", new Regex(@"(?i)\b(?:mongodb(?:\+srv)?|postgres(?:ql)?|mysql|mariadb|redis|rediss|amqps?|mssql|sqlserver)://(?<user>[^\s:/@""']*):([^\s@/""']+)@")),
            ("a hard-coded password or key", new Regex(@"(?i)\b(?:password|passwd|pwd|secret|api[_-]?key|access[_-]?token|auth[_-]?token|client[_-]?secret)\b[""']?\s*[:=]\s*[""']([^""'\s]{6,})[""']")),
            // "Server=db;User Id=sa;Password=...;" - one of the ';'-separated pairs, on a line that also names a server, database
            // or user. Code such as "BOOL password = FALSE;" or connect(host=h, password=p) isn't a connection string.
            ("a password in a connection string", new Regex(@"(?i)^(?=.*\b(?:server|data\s+source|host|address|addr|database|initial\s+catalog|user\s*id|uid|user|username|dsn)\s*=\s*[^\s;=]).*?(?:^|[;""'])\s*(?:password|pwd)\s*=\s*([^;""'\s]{4,})\s*(?:;|[""']|$)")),
        };

        // "your_api_key_here", "${API_KEY}", "$password", "{pw}", "[YOUR-PASSWORD]", "<password>", "changeme", ... are
        // examples or variables, not secrets
        private static readonly Regex PlaceholderValue = new Regex(
            @"(?i)^(your|my|example|sample|placeholder|changeme|change_me|dummy|fake|test|xxx|\*|<|\$\{|\$[a-z_(]|\{|\[|%|todo|none|null|password|secret|process\.env|os\.environ)",
            RegexOptions.Compiled);

        // The well-known passwords of local development databases and brokers (postgres:postgres, guest:guest, root:root)
        private static readonly HashSet<string> DevelopmentDefaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "postgres", "root", "admin", "guest", "pass", "sa", "user", "mysql", "redis", "mongo", "rabbitmq",
        };

        // Cheap first pass: a line without any of these can't match a secret pattern, so most lines skip the full check
        private static readonly Regex SecretHint = new Regex(
            @"(?i)key|token|secret|pass|pwd|BEGIN|AKIA|ASIA|gh[pousr]_|github_pat|AIza|xox[abposr]-|[rs]k_live|sk-|://|npm_|hf_|glpat-|SG\.|webhooks|\d{8,10}:|(?-i:[MNO][A-Za-z\d_-]{22,25}\.)",
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
                    if (match.Groups.Count > 1 && match.Groups[1].Success)
                    {
                        string value = match.Groups[1].Value;
                        if (PlaceholderValue.IsMatch(value) || DevelopmentDefaults.Contains(value)) continue;
                        // "app:app@" - the user name repeated as the password: a local default, not a secret
                        if (match.Groups["user"].Success && string.Equals(match.Groups["user"].Value, value, StringComparison.OrdinalIgnoreCase)) continue;
                    }
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
        // tip: the branch or tag whose commits are checked (the checked-out branch unless told otherwise)
        internal static List<(string File, string Blob, string Reason)> FindSecretsInUnpushedCommits(string tip = "HEAD")
        {
            var flagged = new List<(string File, string Blob, string Reason)>();
            var objects = RunGit(true, "rev-list", "--objects", tip, "--not", "--remotes=origin");
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

        // TODO/FIXME comments are worth knowing about, never a reason to stop: one line, no question.
        // onlyChangedFiles: TODOs in files that haven't changed since the last sync were already mentioned before.
        static void NoteTodoComments(string folder, bool onlyChangedFiles)
        {
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

            int total = perFile.Sum(p => p.Count);
            if (total == 0) return;
            var most = perFile.OrderByDescending(p => p.Count).Take(3).Select(p => $"{p.File} ({p.Count})");
            LogVerbose($"Note: {total} TODO/FIXME comment(s) in what's being uploaded - most in {string.Join(", ", most)}.");
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
