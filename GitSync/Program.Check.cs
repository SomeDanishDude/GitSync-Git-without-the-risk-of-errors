using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GitSync
{
    // What a file in the checked folder is, compared with the GitHub repository
    internal enum FileKind
    {
        // On GitHub, or nothing to lose
        Same, SameExceptLineEndings, LfsConfirmed, Elsewhere, NewerOnGitHub, InHistory, Generated, Empty,
        // Most likely on GitHub, but not confirmed
        LfsUnconfirmed,
        // Only here: deleting the folder would lose these
        LfsMissing, Different, OnlyHere, Unreadable,
    }

    // Who can fix something that only exists here: Sync (it uploads the current branch), a direct upload (other branches
    // and tags GitHub doesn't have yet), or only the user
    internal enum ProblemFix { User, Sync, Upload }

    internal sealed class CheckedFile
    {
        public string Path = "";            // relative to the checked folder, "/" separated
        public FileKind Kind;
        public string Detail = "";
        public long Size;
        public string? RemotePath;          // where it is (or was compared) on GitHub
        public string? LfsOid, LfsBranch;
        public long LfsSize;                // the large file's own size (the folder may only hold its small placeholder)
        public bool Ignored;                // the project's ignore rules leave it out, so Sync wouldn't upload it
        public bool Core;                   // ...although the project needs it (Sync offers to upload it anyway)
        public bool Blocks => Kind is FileKind.LfsMissing or FileKind.Different or FileKind.OnlyHere or FileKind.Unreadable;
    }

    internal sealed class FolderCheck
    {
        public string Folder = "";
        public string Url = "";
        public string? RepoRoot;            // the Git repository this folder is (part of), if any
        public bool Bare;
        public string? Branch;              // the GitHub branch it was compared with
        public string Prefix = "";          // the folder on GitHub this folder matches ("" = the top)
        public string LocalBase = "";       // the folder in here that holds the project (a ZIP's wrapper folder)
        public string? TempRepo;
        public string? ReadError;           // GitHub's side couldn't be read: then nothing is confirmed
        public string? LocalLfsObjects;     // this repository's Git LFS store, where large files live next to their placeholders
        public int OnlyOnGitHub;            // files GitHub has that this folder doesn't - nothing to lose
        public List<CheckedFile> Files = new List<CheckedFile>();
        public List<(string Text, ProblemFix Fix)> Problems = new List<(string Text, ProblemFix Fix)>(); // other things only here
        public List<(string Source, string Target, string Label)> Uploads = new List<(string Source, string Target, string Label)>(); // branches/tags GitHub can just get
        public List<string> Notes = new List<string>();
        public List<FolderCheck> Nested = new List<FolderCheck>();

        public IEnumerable<FolderCheck> All() => new[] { this }.Concat(Nested.SelectMany(n => n.All()));
        public bool HasBlockers => All().Any(c => c.ReadError != null || c.Problems.Count > 0 || c.Files.Any(f => f.Blocks));
        public bool Unconfirmed => All().Any(c => c.Files.Any(f => f.Kind == FileKind.LfsUnconfirmed));
        public bool Safe => !HasBlockers && !Unconfirmed;
    }

    public partial class Program
    {
        private const string CheckTempPrefix = "GitSync-check-";
        private static readonly List<string> checkTempRepos = new List<string>();
        private static readonly byte[] hashBuffer = new byte[1 << 20];

        // Dependency and cache folders: always recreated by installing or building, whatever the project's rules say
        private static readonly HashSet<string> AlwaysGeneratedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // JavaScript & co.
            "node_modules", "bower_components", "jspm_packages", ".pnpm-store", ".next", ".nuxt", ".parcel-cache", ".turbo",
            ".svelte-kit", ".angular", ".sass-cache", ".nyc_output", "elm-stuff",
            // Python
            "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox", ".eggs",
            // C/C++: vcpkg's installed packages (vcpkg install brings them back), CMake's and Visual Studio's working folders
            "vcpkg_installed", "CMakeFiles", "ipch", ".vs",
            // Java/Android, Dart, Haskell, Terraform
            ".gradle", ".cxx", ".externalNativeBuild", ".dart_tool", ".stack-work", ".terraform",
            // Game engines and Xcode
            "DerivedDataCache", ".godot", ".import", "DerivedData",
        };

        static bool IsAlwaysGeneratedFolder(string name) =>
            AlwaysGeneratedFolders.Contains(name)
            || name.StartsWith("cmake-build-", StringComparison.OrdinalIgnoreCase)   // CLion: cmake-build-debug, cmake-build-release...
            || name.EndsWith(".egg-info", StringComparison.OrdinalIgnoreCase);       // Python packaging

        // Build output - but only when the project's ignore rules leave it out: a folder called "build" can hold real sources
        private static readonly HashSet<string> IgnoredGeneratedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", "build", "builds", "_build", ".build", "dist", "out", "target", "Library", "Temp", "Intermediate",
            "Binaries", "vendor", "packages", "venv", ".venv", "Debug", "Release", "x64", "x86", "TestResults", "Pods",
            "coverage", "htmlcov", ".cache", "buildtrees",
        };

        sealed class LocalScan
        {
            public List<string> Files = new List<string>();
            public List<(string Path, string Folder)> Generated = new List<(string Path, string Folder)>(); // inside node_modules & co.
            public List<string> NestedRepos = new List<string>();
        }

        // GitHub's side, read from a temporary bare clone
        sealed class RemoteSide
        {
            public string Tmp = "";
            public bool Sha256;
            public string? Branch;
            public Dictionary<string, (string Id, long Size)> Tree = new Dictionary<string, (string Id, long Size)>(StringComparer.Ordinal);
            public Dictionary<string, string> TreeCi = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // any case -> path
            public HashSet<string> Dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, (string Branch, string Path)> Latest = new Dictionary<string, (string Branch, string Path)>();
            public Dictionary<string, List<(string Branch, string Id)>> OtherBranches = new Dictionary<string, List<(string Branch, string Id)>>(StringComparer.Ordinal); // path -> newest version on other branches
            public HashSet<string> History = new HashSet<string>();
            public Dictionary<string, HashSet<string>> PathHistory = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal); // path -> every version it ever had
            public Dictionary<long, List<(string Oid, string Branch, string Path)>> LfsBySize = new Dictionary<long, List<(string Oid, string Branch, string Path)>>();
            public Dictionary<string, (string Oid, long Size)> LfsAtPath = new Dictionary<string, (string Oid, long Size)>(StringComparer.Ordinal);
            public List<(string Id, string Branch, string Path)> PointerCandidates = new List<(string Id, string Branch, string Path)>();
        }

        // =================================================================================
        // [5] Safe to delete?
        // =================================================================================

        static void HandleSafeToDelete()
        {
            RemoveOldCheckTempRepos();
            string? picked = PickFolder("Select the folder you want to check");
            if (picked == null)
            {
                Console.WriteLine("No folder selected.");
                return;
            }

            string folder = FullFolderPath(picked);
            OpenSessionFolder(folder);
            WriteLineColor(ConsoleColor.Cyan, $"\nChecking: {folder}");
            Console.WriteLine("GitSync only reads this folder and compares it with GitHub - nothing in it is changed.");
            if (IsTooBroadFolder(folder) && !AskYesNo("That's a whole drive or one of your main folders, so this can take a long time. Continue?", false)) return;
            if (!EnsureFolderTrusted(folder)) return;

            // The situation decides everything else: the user is only asked what GitSync can't work out itself
            var (repoRoot, prefix) = AssessFolder(folder);
            string url = "";
            string origin = repoRoot != null ? GetOriginOf(repoRoot) : "";
            if (origin != "")
            {
                WriteLineColor(ConsoleColor.Cyan, $"GitHub address: {MaskUrl(origin)}");
                url = VerifyRemoteAccess(origin, quiet: true) ?? AskCheckUrl();
            }
            else if (repoRoot != null || HasLooseFiles(folder))
            {
                // Only when the folder doesn't say it itself (a folder that just holds other projects needs no address)
                url = AskCheckUrl();
            }

            try
            {
                var check = CheckFolder(folder, url, repoRoot, prefix);
                ConfirmLfsUploads(check);
                PrintCheckReport(check);
                SaveCheckReport(check);
                OfferNextStep(check);
            }
            finally
            {
                DeleteCheckTempRepos();
            }
        }

        static string FullFolderPath(string path)
        {
            string full = Path.GetFullPath(path);
            return full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar) : full; // "C:\" stays "C:\"
        }

        static bool SamePath(string a, string b) =>
            string.Equals(FullFolderPath(a), FullFolderPath(b), StringComparison.OrdinalIgnoreCase);

        static string GetOriginOf(string dir)
        {
            var result = RunGit(true, "-C", dir, "remote", "get-url", "origin");
            return result.Success ? result.Output.Trim() : "";
        }

        // Repository root (and the folder's place in it), or null for a folder that isn't in a repository on GitHub. Asked of
        // git itself, never by comparing paths: a junction or link spells the same folder differently, and a repository taken
        // for one of its own subfolders would skip every check of its history.
        internal static (string? RepoRoot, string Prefix) AssessFolder(string folder)
        {
            if (RunGit(true, "-C", folder, "rev-parse", "--is-bare-repository").Output.Trim() == "true")
            {
                if (RunGit(true, "-C", folder, "rev-parse", "--git-dir").Output.Trim() != ".") return (null, ""); // inside one: just files
                LogVerbose("This is a bare repository (Git's storage without project files) - its branches are compared with GitHub.");
                return (folder, "");
            }
            var inside = RunGit(true, "-C", folder, "rev-parse", "--is-inside-work-tree");
            if (!inside.Success || inside.Output.Trim() != "true") return (null, "");

            // "" at the top of the repository, "src/" inside it. Can't tell? Then the stricter way: as the repository itself.
            var prefix = RunGit(true, "-C", folder, "rev-parse", "--show-prefix");
            if (!prefix.Success || prefix.Output.Trim() == "") return (folder, "");

            string root = FullFolderPath(RunGit(true, "-C", folder, "rev-parse", "--show-toplevel").Output.Trim());
            // Inside a repository that isn't on GitHub at all (e.g. a 'git init' made by mistake in the user folder): a plain folder
            if (GetOriginOf(root) == "") return (null, "");
            LogVerbose($"This folder is part of the repository in {root}, so it's compared with '{prefix.Output.Trim()}' there.");
            return (root, prefix.Output.Trim());
        }

        static string AskCheckUrl()
        {
            while (true)
            {
                string answer = GetUserInput("\nPaste the GitHub address of this project (Enter = it isn't on GitHub): ").Trim('"');
                if (answer == "") return "";
                string? url = NormalizeRemoteUrl(answer);
                if (url == null)
                {
                    Console.WriteLine("That doesn't look like a GitHub address. Copy it from the green 'Code' button on the project's GitHub page.");
                    continue;
                }
                Console.WriteLine($"Connecting to {MaskUrl(url)} (a sign-in window may pop up)...");
                string? verified = VerifyRemoteAccess(url, quiet: true);
                if (verified != null) return verified;
            }
        }

        // Any file that isn't inside another Git repository? (A folder that only holds projects needs no address of its own.)
        static bool HasLooseFiles(string folder)
        {
            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
            var pending = new Stack<string>();
            pending.Push(folder);
            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                IEnumerable<FileSystemInfo> entries;
                try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options).ToList(); }
                catch { return true; } // can't tell: ask
                foreach (var entry in entries)
                {
                    if (entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry is FileInfo && !IsJunkFileName(entry.Name)) return true;
                    if (entry is DirectoryInfo d && !Directory.Exists(Path.Combine(d.FullName, ".git")) && !File.Exists(Path.Combine(d.FullName, ".git")))
                    {
                        try { if (d.LinkTarget != null) continue; } catch { continue; }
                        pending.Push(d.FullName);
                    }
                }
            }
            return false;
        }

        // =================================================================================
        // The comparison (reads only: this folder, its repository and a temporary clone of GitHub's)
        // =================================================================================

        // url "" = no GitHub repository to compare with. repoRoot: the repository this folder is or belongs to.
        // prefix: the folder's path inside that repository ("" = the folder is the repository itself).
        internal static FolderCheck CheckFolder(string folder, string url, string? repoRoot, string prefix)
        {
            // Git may refresh a repository's index while reading it; a check must not write anything into the user's folder
            string? locks = Environment.GetEnvironmentVariable("GIT_OPTIONAL_LOCKS");
            Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", "0");
            try
            {
                return CheckFolder(folder, url, repoRoot, prefix, depth: 0);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", locks);
            }
        }

        static FolderCheck CheckFolder(string folder, string url, string? repoRoot, string prefix, int depth)
        {
            folder = FullFolderPath(folder);
            var check = new FolderCheck { Folder = folder, Url = url, RepoRoot = repoRoot, Prefix = prefix };
            check.Bare = repoRoot != null && RunGit(true, "-C", repoRoot, "rev-parse", "--is-bare-repository").Output.Trim() == "true";
            if (repoRoot != null) check.LocalLfsObjects = LocalLfsObjectsDir(repoRoot);

            var local = check.Bare ? new LocalScan() : ScanFolder(folder, check);
            RemoteSide? remote = null;
            if (url != "")
            {
                remote = ReadRemote(check, repoRoot);
                if (remote == null) return check; // couldn't be read: ReadError says so, and nothing counts as checked
            }
            else if (local.Files.Count > 0 || repoRoot != null)
            {
                check.Notes.Add("There's no GitHub repository to compare with, so nothing here counts as being on GitHub.");
            }
            if (local.NestedRepos.Count > 0 && local.Files.Count == 0 && repoRoot == null)
            {
                check.Notes.Add($"This folder holds {local.NestedRepos.Count} Git project(s) - each was compared with its own GitHub repository.");
            }

            if (remote != null && repoRoot == null) FindWhereFolderFits(check, remote, local.Files);
            CompareFiles(check, remote, local.Files);
            foreach (var (path, generatedFolder) in local.Generated)
            {
                check.Files.Add(new CheckedFile { Path = path, Kind = FileKind.Generated, Detail = generatedFolder });
            }
            MarkGeneratedAndIgnored(check);
            if (repoRoot != null) CheckRepositoryHistory(check, remote);
            DescribeOnlyHereFiles(check, remote);

            foreach (var nested in local.NestedRepos.Take(30))
            {
                string rel = RelativeTo(folder, nested);
                if (depth >= 4)
                {
                    check.Problems.Add(($"{rel}/ is another Git repository, nested too deep to check", ProblemFix.User));
                    continue;
                }
                string nestedUrl = GetOriginOf(nested);
                if (nestedUrl == "")
                {
                    var none = new FolderCheck { Folder = nested, RepoRoot = nested };
                    none.Problems.Add(("it's a Git repository of its own that isn't linked to GitHub (no 'origin') - its files and history only exist here", ProblemFix.Sync));
                    check.Nested.Add(none);
                    continue;
                }
                LogVerbose($"{rel}/ is a Git repository of its own - comparing it with {MaskUrl(nestedUrl)}...");
                check.Nested.Add(CheckFolder(nested, nestedUrl, nested, "", depth + 1));
            }
            if (local.NestedRepos.Count > 30) check.Problems.Add(($"{local.NestedRepos.Count - 30} more Git repositories inside weren't checked", ProblemFix.User));
            return check;
        }

        // This repository's Git LFS store (.git\lfs\objects, or where lfs.storage points), if it has one
        static string? LocalLfsObjectsDir(string repoRoot)
        {
            string gitDir = GitPathOf(repoRoot, "--git-common-dir");
            if (gitDir == "") return null;
            string storage = RunGit(true, "-C", repoRoot, "config", "--get", "lfs.storage").Output.Trim();
            string lfs = storage == "" ? Path.Combine(gitDir, "lfs") : Path.IsPathRooted(storage) ? storage : Path.Combine(gitDir, storage);
            string objects = Path.Combine(lfs, "objects");
            return Directory.Exists(objects) ? objects : null;
        }

        static string LfsObjectPath(string objectsDir, string oid) => Path.Combine(objectsDir, oid.Substring(0, 2), oid.Substring(2, 2), oid);

        // A path git prints for a repository (--git-common-dir, --git-path hooks), made absolute. Without --path-format, which
        // Git before 2.31 doesn't know: there it would come out as text, and the path would silently not be found.
        static string GitPathOf(string repo, params string[] query)
        {
            var result = RunGit(true, new[] { "-C", repo, "rev-parse" }.Concat(query).ToArray());
            string path = result.Success ? result.Output.Trim() : "";
            if (path == "" || path.Contains('\n')) return "";
            try { return Path.GetFullPath(Path.Combine(repo, path.Replace('/', Path.DirectorySeparatorChar))); } catch { return ""; }
        }

        // A Git LFS placeholder file ("version https://git-lfs.github.com/spec/v1", "oid sha256:...", "size ...")
        static bool TryReadLfsPointer(string path, out string oid, out long size)
        {
            oid = "";
            size = 0;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 1024) return false;
                string text = File.ReadAllText(path);
                if (!text.StartsWith("version https://git-lfs.github.com/spec/")) return false;
                var o = Regex.Match(text, @"^oid sha256:([0-9a-f]{64})\s*$", RegexOptions.Multiline);
                var s = Regex.Match(text, @"^size (\d+)\s*$", RegexOptions.Multiline);
                if (!o.Success || !s.Success || !long.TryParse(s.Groups[1].Value, out size)) return false;
                oid = o.Groups[1].Value;
                return true;
            }
            catch
            {
                return false;
            }
        }

        static string RelativeTo(string folder, string path) => Path.GetRelativePath(folder, path).Replace('\\', '/');

        static bool IsJunkFileName(string name) =>
            name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) || name.StartsWith("~$");

        // Every file in the folder (hidden ones too), without going into other Git repositories or links. A folder that can't be
        // opened is a problem, not something to skip quietly - its files might be the ones that matter.
        static LocalScan ScanFolder(string folder, FolderCheck check)
        {
            var scan = new LocalScan();
            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
            var pending = new Stack<string>();
            pending.Push(folder);
            var progress = Stopwatch.StartNew();
            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                List<FileSystemInfo> entries;
                try
                {
                    entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options).ToList();
                }
                catch (Exception ex)
                {
                    string where = SamePath(dir, folder) ? "The folder" : RelativeTo(folder, dir) + "/";
                    check.Problems.Add(($"{where} couldn't be opened ({ex.Message}), so the files in it couldn't be checked", ProblemFix.User));
                    continue;
                }

                foreach (var entry in entries)
                {
                    // This repository's own storage; a nested repository is recognised by its folder, below
                    if (entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                    {
                        if (check.RepoRoot == null && SamePath(dir, folder))
                        {
                            check.Problems.Add((".git holds a Git repository that Git can't read (damaged?) - it may have history that isn't on GitHub", ProblemFix.User));
                        }
                        continue;
                    }
                    string rel = RelativeTo(folder, entry.FullName);
                    // Can't tell? Then it's treated as a normal file or folder - which gets checked (or reported as unreadable)
                    bool isLink;
                    try { isLink = entry.LinkTarget != null; } catch { isLink = false; }
                    if (isLink)
                    {
                        check.Notes.Add($"{rel} is a link to another place - not checked (moving this folder to the Recycle Bin only removes the link, not what it points to).");
                        continue;
                    }

                    if (entry is DirectoryInfo d)
                    {
                        if (Regex.IsMatch(d.Name, @"^\.git(-broken-|-gitsync-backup-|\.gitsync-delete-)"))
                        {
                            check.Problems.Add(($"{rel}/ is an old Git repository folder GitSync set aside - it may hold history that isn't on GitHub", ProblemFix.User));
                            continue;
                        }
                        // Git's storage without project files (a "bare" repository, e.g. a backup copy): one thing, not its hundreds of internal files
                        if (File.Exists(Path.Combine(d.FullName, "HEAD")) && Directory.Exists(Path.Combine(d.FullName, "objects")) && Directory.Exists(Path.Combine(d.FullName, "refs")))
                        {
                            check.Problems.Add(($"{rel}/ is a Git repository's storage (a \"bare\" repository) - check that folder on its own with [5]", ProblemFix.User));
                            continue;
                        }
                        // node_modules & co., and folders a tool fills completely (a CMake build folder, a Python environment -
                        // whatever they're called): tens of thousands of files that building or installing recreates -
                        // counted, not compared
                        string? treeKind = IsAlwaysGeneratedFolder(d.Name) ? "" : GeneratedTreeKind(d.FullName);
                        if (treeKind != null)
                        {
                            string label = rel + "/" + (treeKind != "" ? $" ({treeKind})" : "");
                            try
                            {
                                var all = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = true };
                                foreach (var f in d.EnumerateFiles("*", all))
                                {
                                    // A password or key file is never just "build output", wherever it is
                                    if (IsSensitiveFileName(f.Name)) scan.Files.Add(RelativeTo(folder, f.FullName));
                                    else scan.Generated.Add((RelativeTo(folder, f.FullName), label));
                                }
                            }
                            catch { }
                            continue;
                        }
                        if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))
                        {
                            scan.NestedRepos.Add(d.FullName);
                            continue;
                        }
                        pending.Push(d.FullName);
                    }
                    else
                    {
                        scan.Files.Add(rel);
                        if (progress.ElapsedMilliseconds > 1000 && !Console.IsOutputRedirected)
                        {
                            lock (ConsoleLock) Console.Write($"\r    Looking through the folder: {scan.Files.Count} files...");
                            progress.Restart();
                        }
                    }
                }
            }
            if (!Console.IsOutputRedirected) lock (ConsoleLock) Console.Write("\r" + new string(' ', 60) + "\r");
            scan.Files.Sort(StringComparer.Ordinal);
            return scan;
        }

        // A temporary bare clone of the GitHub repository. For a local repository it borrows the objects this computer already
        // has (--reference), so only what's new is downloaded - and the user's repository is never written to.
        static RemoteSide? ReadRemote(FolderCheck check, string? repoRoot)
        {
            string tmp = Path.Combine(Path.GetTempPath(), CheckTempPrefix + Guid.NewGuid().ToString("N").Substring(0, 8));
            checkTempRepos.Add(tmp);
            check.TempRepo = tmp;

            var args = new List<string> { "-c", "gc.auto=0", "clone", "--bare", "--progress", "--no-hardlinks" };
            if (repoRoot != null)
            {
                string common = GitPathOf(repoRoot, "--git-common-dir");
                if (common != "")
                {
                    args.Add("--reference-if-able");
                    args.Add(common);
                }
            }
            args.Add(check.Url);
            args.Add(tmp);
            LogVerbose($"Reading what GitHub has for {MaskUrl(check.Url)} (into a temporary folder)...");
            var clone = RunGitLive(args.ToArray());
            if (!clone.Success)
            {
                string why = GetFriendlyHint(clone.AllText) ?? clone.AllText.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
                check.ReadError = $"GitHub's copy ({MaskUrl(check.Url)}) couldn't be read, so nothing could be compared: {MaskUrl(why)}";
                return null;
            }

            var remote = new RemoteSide { Tmp = tmp, Sha256 = RunGit(true, "-C", tmp, "rev-parse", "--show-object-format").Output.Trim() == "sha256" };
            var branches = RunGit(true, "-C", tmp, "for-each-ref", "--format=%(refname:strip=2)", "refs/heads/").Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(b => b.Trim()).Where(b => b.Length > 0).ToList();
            if (branches.Count == 0)
            {
                check.Notes.Add("The GitHub repository is empty - nothing from this folder is on it yet.");
                return remote;
            }

            remote.Branch = ChooseCheckBranch(tmp, branches, repoRoot);
            check.Branch = remote.Branch;
            ReadRemoteTree(remote, remote.Branch, compared: true);
            var others = branches.Where(b => b != remote.Branch).ToList();
            foreach (var b in others.Take(20)) ReadRemoteTree(remote, b, compared: false);
            if (others.Count > 20) check.Notes.Add($"GitHub has {branches.Count} branches: the newest version of 21 was compared, the others through their history.");

            // Every version GitHub has ever had (all branches and tags): a file matching one of them can always be got back
            foreach (var line in RunGit(true, "-C", tmp, "rev-list", "--objects", "--all").Output.Split('\n'))
            {
                int space = line.IndexOf(' ');
                string id = (space < 0 ? line : line.Substring(0, space)).Trim();
                if (id.Length >= 40) remote.History.Add(id);
            }

            // ...and which versions each path has had, so an edit that happens to match some other file isn't mistaken for an
            // older version of this one. ":<mode> <mode> <old id> <new id> <status>" then the path; -m includes merge results.
            var changes = SplitNul(RunGit(true, "-C", tmp, "log", "--all", "-m", "--format=", "--raw", "--no-abbrev", "--no-renames", "-z").Output);
            for (int i = 0; i + 1 < changes.Count; i++)
            {
                if (!changes[i].StartsWith(":")) continue;
                var meta = changes[i].Split(' ');
                string path = changes[++i];
                if (meta.Length < 5) continue;
                if (!remote.PathHistory.TryGetValue(path, out var versions)) remote.PathHistory[path] = versions = new HashSet<string>();
                foreach (var version in new[] { meta[2], meta[3] }) if (version.Trim('0').Length > 0) versions.Add(version);
            }
            ReadLfsPointers(remote);
            return remote;
        }

        // The branch this folder belongs to: the one it syncs with, else GitHub's main branch
        static string ChooseCheckBranch(string tmp, List<string> branches, string? repoRoot)
        {
            if (repoRoot != null)
            {
                var upstream = RunGit(true, "-C", repoRoot, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
                string up = upstream.Success ? upstream.Output.Trim() : "";
                if (up.StartsWith("origin/") && branches.Contains(up.Substring(7))) return up.Substring(7);
                string current = RunGit(true, "-C", repoRoot, "symbolic-ref", "--quiet", "--short", "HEAD").Output.Trim();
                if (current != "")
                {
                    string target = RunGit(true, "-C", repoRoot, "config", "--get", $"branch.{current}.gitsynctarget").Output.Trim();
                    if (target != "" && branches.Contains(target)) return target;
                    if (branches.Contains(current)) return current;
                }
            }
            string head = RunGit(true, "-C", tmp, "symbolic-ref", "--quiet", "--short", "HEAD").Output.Trim();
            if (branches.Contains(head)) return head;
            return branches.Contains("main") ? "main" : branches.Contains("master") ? "master" : branches[0];
        }

        static void ReadRemoteTree(RemoteSide remote, string branch, bool compared)
        {
            foreach (var entry in SplitNul(RunGit(true, "-C", remote.Tmp, "ls-tree", "-r", "-l", "-z", "refs/heads/" + branch).Output))
            {
                int tab = entry.IndexOf('\t');
                if (tab < 0) continue;
                var meta = entry.Substring(0, tab).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (meta.Length < 4 || meta[1] != "blob") continue;
                string path = entry.Substring(tab + 1);
                long.TryParse(meta[3], out long size);
                string id = meta[2];
                if (compared)
                {
                    remote.Tree[path] = (id, size);
                    remote.TreeCi.TryAdd(path, path);
                    for (int i = path.IndexOf('/'); i >= 0; i = path.IndexOf('/', i + 1)) remote.Dirs.Add(path.Substring(0, i + 1));
                }
                else
                {
                    if (!remote.OtherBranches.TryGetValue(path, out var list)) remote.OtherBranches[path] = list = new List<(string Branch, string Id)>();
                    list.Add((branch, id));
                }
                remote.Latest.TryAdd(id, (branch, path));
                // Git LFS stores a ~130-byte "pointer" in place of the file
                if (size >= 100 && size <= 400) remote.PointerCandidates.Add((id, branch, path));
            }
        }

        static void ReadLfsPointers(RemoteSide remote)
        {
            var ids = remote.PointerCandidates.Select(p => p.Id).Distinct().ToList();
            if (ids.Count == 0) return;
            var (ok, data) = RunGitForBytes(new[] { "-C", remote.Tmp, "cat-file", "--batch" }, string.Concat(ids.Select(i => i + "\n")));
            if (!ok) return;

            var pointers = new Dictionary<string, (string Oid, long Size)>();
            int pos = 0;
            foreach (var id in ids)
            {
                int headerEnd = Array.IndexOf(data, (byte)'\n', pos);
                if (headerEnd < 0) break;
                var header = Encoding.ASCII.GetString(data, pos, headerEnd - pos).Split(' ');
                pos = headerEnd + 1;
                if (header.Length < 3) continue; // "<id> missing": no content follows
                if (!int.TryParse(header[2], out int size) || pos + size > data.Length) break;
                string text = Encoding.ASCII.GetString(data, pos, size);
                pos += size + 1;
                if (!text.StartsWith("version https://git-lfs.github.com/spec/")) continue;
                var oid = Regex.Match(text, @"^oid sha256:([0-9a-f]{64})\s*$", RegexOptions.Multiline);
                var length = Regex.Match(text, @"^size (\d+)\s*$", RegexOptions.Multiline);
                if (oid.Success && length.Success && long.TryParse(length.Groups[1].Value, out long fileSize)) pointers[id] = (oid.Groups[1].Value, fileSize);
            }

            foreach (var (id, branch, path) in remote.PointerCandidates)
            {
                if (!pointers.TryGetValue(id, out var p)) continue;
                if (!remote.LfsBySize.TryGetValue(p.Size, out var list)) remote.LfsBySize[p.Size] = list = new List<(string Oid, string Branch, string Path)>();
                list.Add((p.Oid, branch, path));
                if (branch == remote.Branch) remote.LfsAtPath[path] = p;
            }
        }

        // A plain folder doesn't always line up with GitHub's top folder: a ZIP download has a wrapper folder ("project-main\"),
        // and a kept "src" folder is only part of the project. Picks the alignment that matches the most file names.
        static void FindWhereFolderFits(FolderCheck check, RemoteSide remote, List<string> files)
        {
            var sample = files.Where(f => !IsJunkFileName(f.Substring(f.LastIndexOf('/') + 1))).Take(3000).ToList();
            if (sample.Count == 0 || remote.Tree.Count == 0) return;

            int Matches(string localBase, string remotePrefix) => sample.Count(f =>
            {
                if (localBase != "" && !f.StartsWith(localBase + "/")) return false;
                string key = localBase == "" ? f : f.Substring(localBase.Length + 1);
                return remote.TreeCi.ContainsKey(remotePrefix + key);
            });

            string bestBase = "", bestPrefix = "";
            int best = Matches("", "");
            var firstParts = sample.Select(f => f.Contains('/') ? f.Substring(0, f.IndexOf('/')) : null).Distinct().ToList();
            if (firstParts.Count == 1 && firstParts[0] != null)
            {
                int m = Matches(firstParts[0]!, "");
                if (m > best) (bestBase, best) = (firstParts[0]!, m);
            }
            if (best * 2 < sample.Count) // fewer than half found at the top: try GitHub's folders
            {
                foreach (var dir in remote.Dirs.Where(d => d.Count(c => c == '/') <= 4).Take(2000))
                {
                    int m = Matches(bestBase, dir);
                    if (m > best) (bestPrefix, best) = (dir, m);
                }
            }

            check.LocalBase = bestBase;
            check.Prefix = bestPrefix;
            if (bestBase != "") check.Notes.Add($"The project is inside '{bestBase}/' here (like a ZIP download) - that's what was compared with GitHub's top folder.");
            if (bestPrefix != "") check.Notes.Add($"This folder matches the folder '{bestPrefix}' on GitHub, so it was compared with that.");
        }

        static void CompareFiles(FolderCheck check, RemoteSide? remote, List<string> files)
        {
            var matchedRemote = new HashSet<string>(StringComparer.Ordinal);
            var progress = Stopwatch.StartNew();
            int done = 0;
            foreach (var rel in files)
            {
                var file = new CheckedFile { Path = rel };
                check.Files.Add(file);
                string full = Path.Combine(check.Folder, rel.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    ClassifyFile(check, remote, file, full, matchedRemote);
                }
                catch (Exception ex)
                {
                    file.Kind = FileKind.Unreadable;
                    file.Detail = ex.Message;
                }

                done++;
                if (progress.ElapsedMilliseconds > 1000 && !Console.IsOutputRedirected)
                {
                    lock (ConsoleLock) Console.Write($"\r    Comparing files: {done} of {files.Count}...");
                    progress.Restart();
                }
            }
            if (!Console.IsOutputRedirected) lock (ConsoleLock) Console.Write("\r" + new string(' ', 60) + "\r");

            if (remote != null)
            {
                check.OnlyOnGitHub = remote.Tree.Keys.Count(p => p.StartsWith(check.Prefix, StringComparison.OrdinalIgnoreCase) && !matchedRemote.Contains(p));
            }
        }

        static void ClassifyFile(FolderCheck check, RemoteSide? remote, CheckedFile file, string full, HashSet<string> matchedRemote)
        {
            var info = new FileInfo(full);
            file.Size = info.Length;
            if (remote == null)
            {
                file.Kind = file.Size == 0 ? FileKind.Empty : FileKind.OnlyHere;
                return;
            }

            // The path GitHub would have it at (none for files outside a ZIP's wrapper folder: those are matched by content only).
            // The exact spelling first: GitHub can hold README.md and readme.md side by side.
            string? key = check.LocalBase == "" ? file.Path
                : file.Path.StartsWith(check.LocalBase + "/") ? file.Path.Substring(check.LocalBase.Length + 1) : null;
            string? atPath = null;
            if (key != null)
            {
                string wanted = check.Prefix + key;
                atPath = remote.Tree.ContainsKey(wanted) ? wanted : remote.TreeCi.TryGetValue(wanted, out var actual) ? actual : null;
            }
            if (atPath != null)
            {
                matchedRemote.Add(atPath);
                file.RemotePath = atPath;
            }

            string raw;
            using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
            {
                raw = GitBlobId(stream, file.Size, remote.Sha256);
            }

            // Only Git LFS's small placeholder is in the folder, and the file itself sits in this repository's LFS store:
            // deleting the folder deletes that copy, so GitHub must really have the file (confirmed by downloading it)
            if (check.LocalLfsObjects != null && file.Size <= 1024 && TryReadLfsPointer(full, out string placeholderOid, out long placeholderSize)
                && File.Exists(LfsObjectPath(check.LocalLfsObjects, placeholderOid)))
            {
                bool listedHere = atPath != null && (remote.Tree[atPath].Id == raw || (remote.LfsAtPath.TryGetValue(atPath, out var listed) && listed.Oid == placeholderOid));
                var listedElsewhere = remote.LfsBySize.TryGetValue(placeholderSize, out var sameSize) ? sameSize.FirstOrDefault(c => c.Oid == placeholderOid) : default;
                file.LfsSize = placeholderSize;
                if (listedHere || listedElsewhere.Oid != null)
                {
                    (file.Kind, file.LfsOid) = (FileKind.LfsUnconfirmed, placeholderOid);
                    (file.LfsBranch, file.RemotePath) = listedHere ? (remote.Branch, atPath) : (listedElsewhere.Branch, listedElsewhere.Path);
                    file.Detail = "the folder only has a placeholder - the file itself is in .git, so GitHub must have it";
                }
                else
                {
                    file.Kind = atPath != null ? FileKind.Different : FileKind.OnlyHere;
                    file.Detail = "a large file (Git LFS) whose only copy is in .git here";
                }
                return;
            }
            if (atPath != null && remote.Tree[atPath].Id == raw)
            {
                file.Kind = FileKind.Same;
                return;
            }
            if (file.Size == 0)
            {
                file.Kind = FileKind.Empty;
                return;
            }

            // Stored with Git LFS on GitHub: its pointer names the file's SHA-256
            string? sha256 = null;
            string Sha() => sha256 ??= FileSha256(full);
            if (atPath != null && remote.LfsAtPath.TryGetValue(atPath, out var pointer) && pointer.Size == file.Size && Sha() == pointer.Oid)
            {
                (file.Kind, file.LfsOid, file.LfsSize, file.LfsBranch) = (FileKind.LfsUnconfirmed, pointer.Oid, pointer.Size, remote.Branch);
                return;
            }

            // The same text with Windows (CRLF) or Linux (LF) line endings is the same file
            var ids = new List<string> { raw };
            if (file.Size <= 50L * 1024 * 1024 && !IsProbablyBinary(full))
            {
                foreach (var variant in LineEndingVariants(File.ReadAllBytes(full))) ids.Add(GitBlobId(variant, remote.Sha256));
            }
            if (atPath != null && ids.Contains(remote.Tree[atPath].Id))
            {
                file.Kind = FileKind.SameExceptLineEndings;
                return;
            }

            // A large file that matches one GitHub keeps with Git LFS (by its SHA-256 - no coincidence there)
            if (remote.LfsBySize.TryGetValue(file.Size, out var candidates))
            {
                var match = candidates.FirstOrDefault(c => c.Oid == Sha());
                if (match.Oid != null)
                {
                    (file.Kind, file.LfsOid, file.LfsSize, file.LfsBranch, file.RemotePath) = (FileKind.LfsUnconfirmed, match.Oid, file.Size, match.Branch, match.Path);
                    return;
                }
            }

            if (atPath != null)
            {
                // GitHub has this file, with other content. It only counts as being on GitHub if THIS file had this exact
                // content there - on another branch, or earlier. (Content that matches some other file means it was edited here.)
                var sameOnBranch = remote.OtherBranches.TryGetValue(atPath, out var onOtherBranches)
                    ? onOtherBranches.FirstOrDefault(o => ids.Contains(o.Id)) : default;
                if (sameOnBranch.Branch != null)
                {
                    file.Kind = FileKind.Elsewhere;
                    file.Detail = $"the same as on the branch '{sameOnBranch.Branch}'";
                    return;
                }
                if (remote.PathHistory.TryGetValue(atPath, out var versions) && ids.Any(versions.Contains))
                {
                    file.Kind = FileKind.NewerOnGitHub; // this exact version was on GitHub once: GitHub's latest is a later one
                    return;
                }
                file.Kind = FileKind.Different;
                return;
            }

            // Not at this path on GitHub: moved or renamed there, or removed since (its history still has it)
            var where = ids.Select(id => remote.Latest.TryGetValue(id, out var w) ? w : ((string Branch, string Path)?)null).FirstOrDefault(w => w != null);
            if (where is { } found)
            {
                file.Kind = FileKind.Elsewhere;
                file.RemotePath = found.Path;
                file.Detail = found.Branch == remote.Branch ? $"as {found.Path}" : $"as {found.Path} on the branch '{found.Branch}'";
                return;
            }
            file.Kind = ids.Any(remote.History.Contains) ? FileKind.InHistory : FileKind.OnlyHere;
        }

        // Of the files that aren't on GitHub: which are only build output or leftovers, and which the project's ignore rules
        // leave out (Sync won't upload those, so the user has to save them some other way)
        static void MarkGeneratedAndIgnored(FolderCheck check)
        {
            var blocking = check.Files.Where(f => f.Kind is FileKind.Different or FileKind.OnlyHere).ToList();
            if (blocking.Count == 0) return;

            HashSet<string> ignored;
            string[] where;      // how git reads this project's ignore rules
            string toGit = "";   // what goes in front of a file's path for git
            if (check.RepoRoot != null && !check.Bare)
            {
                where = new[] { "-C", check.RepoRoot };
                toGit = check.Prefix; // the folder's place in its repository
                var result = ExecuteGit(where.Concat(new[] { "check-ignore", "--no-index", "-z", "--stdin" }).ToArray(),
                    silent: true, live: false, input: string.Concat(blocking.Select(f => toGit + f.Path + "\0")));
                ignored = new HashSet<string>(SplitNul(result.Output).Select(p => p.Substring(Math.Min(toGit.Length, p.Length))));
            }
            else
            {
                // A plain folder: its own .gitignore files decide, read through a Git folder elsewhere (nothing is written here)
                string gitDir = check.TempRepo != null && Directory.Exists(check.TempRepo) ? check.TempRepo : NewScratchRepo();
                where = new[] { "--git-dir=" + gitDir, "--work-tree=" + check.Folder };
                var result = ExecuteGit(where.Concat(new[] { "check-ignore", "--no-index", "-z", "--stdin" }).ToArray(),
                    silent: true, live: false, input: string.Concat(blocking.Select(f => f.Path + "\0")));
                ignored = new HashSet<string>(SplitNul(result.Output));
            }

            // Files the project needs although its .gitignore leaves them out (a too-broad rule): never "build output", and
            // Sync offers to upload them. Judged from the project's own top folder.
            string projectRoot = check.RepoRoot ?? (check.LocalBase != "" ? Path.Combine(check.Folder, check.LocalBase.Replace('/', Path.DirectorySeparatorChar)) : check.Folder);
            string? InProject(CheckedFile f) => check.RepoRoot != null ? check.Prefix + f.Path
                : check.LocalBase == "" ? f.Path
                : f.Path.StartsWith(check.LocalBase + "/") ? f.Path.Substring(check.LocalBase.Length + 1) : null;
            var shape = ReadProjectShape(projectRoot);
            var core = new List<(CheckedFile File, string Why)>();
            foreach (var file in blocking.Where(f => ignored.Contains(f.Path)))
            {
                string? inProject = InProject(file);
                string? why = inProject == null ? null : CoreFileReason(shape, inProject);
                if (why != null) core.Add((file, why));
            }
            if (core.Count > 0)
            {
                var rules = IgnoreRules(core.Select(c => toGit + c.File.Path), where);
                var keepOut = check.RepoRoot != null ? ReadKeepOutList(check.RepoRoot) : new HashSet<string>();
                foreach (var (file, why) in core)
                {
                    bool hasRule = rules.TryGetValue(toGit + file.Path, out var rule);
                    // A rule written for exactly this file, or a file the user chose to keep out: left out on purpose
                    if ((hasRule && IsRuleForThisFileOnly(rule.Pattern, toGit + file.Path)) || keepOut.Contains(toGit + file.Path)) continue;
                    file.Core = true;
                    file.Detail = $"{why} - your .gitignore leaves it out" + (hasRule ? $" with {DescribeRule(rule)}" : "");
                }
            }

            string? self = Environment.ProcessPath;
            foreach (var file in blocking)
            {
                file.Ignored = ignored.Contains(file.Path);
                if (file.Core) continue;
                string full = Path.Combine(check.Folder, file.Path.Replace('/', Path.DirectorySeparatorChar));
                string? reason = GeneratedReason(file.Path, file.Ignored);
                // CMake built inside the project folder: its Makefiles and caches sit right next to the sources
                string name = file.Path.Substring(file.Path.LastIndexOf('/') + 1);
                if (reason == null && shape.InSourceCMake && IsCMakeOutputName(name)) reason = "CMake's own output (it rewrites these on every build)";
                if (self != null && string.Equals(Path.GetFullPath(full), self, StringComparison.OrdinalIgnoreCase)) reason = "GitSync itself";
                if (reason == null) continue;
                file.Kind = FileKind.Generated;
                file.Detail = reason;
            }
        }

        static string? GeneratedReason(string path, bool ignored)
        {
            var parts = path.Split('/');
            if (IsSensitiveFileName(parts[^1])) return null; // passwords and keys always count, wherever they are
            if (IsJunkFileName(parts[^1])) return "Windows/Office leftovers";
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (IsAlwaysGeneratedFolder(parts[i])) return string.Join("/", parts.Take(i + 1)) + "/";
            }
            if (!ignored) return null;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (IgnoredGeneratedFolders.Contains(parts[i])) return string.Join("/", parts.Take(i + 1)) + "/";
            }
            return null;
        }

        // What only the repository itself holds: commits that aren't on GitHub, stashes, tags, unfinished merges, other work
        // folders. Not for a folder inside a repository: deleting that leaves the repository (and its history) in place.
        static void CheckRepositoryHistory(FolderCheck check, RemoteSide? remote)
        {
            string repo = check.RepoRoot!;
            if (check.Prefix != "")
            {
                check.Notes.Add($"This folder is part of the repository in {repo}. Deleting it is a change too: the next Sync uploads the deletion, " +
                                "so the folder disappears from GitHub's newest version as well (its history keeps it).");
                return;
            }

            string gitDir = RunGit(true, "-C", repo, "rev-parse", "--absolute-git-dir").Output.Trim();
            foreach (var (marker, what) in new[] { ("MERGE_HEAD", "a merge"), ("rebase-merge", "a rebase"), ("rebase-apply", "a rebase"), ("CHERRY_PICK_HEAD", "a cherry-pick"), ("REVERT_HEAD", "a revert") })
            {
                if (gitDir != "" && (File.Exists(Path.Combine(gitDir, marker)) || Directory.Exists(Path.Combine(gitDir, marker))))
                {
                    check.Problems.Add(($"{what} was left unfinished here - finish or cancel it first (run [1] Sync)", ProblemFix.Sync));
                    break;
                }
            }

            // Commits GitHub doesn't have: counted in the temporary clone, which can see this repository's objects
            int NotOnGitHub(string commit)
            {
                var count = remote != null
                    ? RunGit(true, "-C", remote.Tmp, "rev-list", "--count", commit, "--not", "--all")
                    : RunGit(true, "-C", repo, "rev-list", "--count", commit);
                return count.Success && int.TryParse(count.Output.Trim(), out int n) ? n : -1;
            }
            // GitHub can simply get it: it doesn't have that branch yet, or only an older state of it (nothing there is overwritten)
            bool CanAdd(string remoteRef, string commit) => remote != null && (
                !RunGit(true, "-C", remote.Tmp, "rev-parse", "--verify", "--quiet", remoteRef).Success
                || RunGit(true, "-C", remote.Tmp, "merge-base", "--is-ancestor", remoteRef, commit).Success);

            // The checked-out branch is Sync's job; any other one can be uploaded as it is
            string current = check.Bare ? "" : RunGit(true, "-C", repo, "symbolic-ref", "--quiet", "--short", "HEAD").Output.Trim();
            foreach (var line in RunGit(true, "-C", repo, "for-each-ref", "--format=%(refname:strip=2)%09%(objectname)", "refs/heads/").Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split('\t');
                if (parts.Length < 2) continue;
                string name = parts[0], commit = parts[1];
                int n = NotOnGitHub(commit);
                if (n < 0)
                {
                    check.Problems.Add(($"the branch '{name}' couldn't be compared with GitHub", ProblemFix.User));
                    continue;
                }
                if (n == 0) continue;
                string text = $"the branch '{name}' has {n} commit(s) that aren't on GitHub";
                if (name == current) check.Problems.Add((text, ProblemFix.Sync));
                else if (CanAdd("refs/heads/" + name, commit))
                {
                    check.Problems.Add((text, ProblemFix.Upload));
                    check.Uploads.Add((commit, "refs/heads/" + name, $"the branch '{name}'"));
                }
                else check.Problems.Add((text + " - and GitHub's version of it went another way", ProblemFix.User));
            }
            if (current == "" && !check.Bare)
            {
                string head = RunGit(true, "-C", repo, "rev-parse", "--verify", "--quiet", "HEAD").Output.Trim();
                if (head != "" && NotOnGitHub(head) > 0) check.Problems.Add(("the commit checked out right now (not on any branch) isn't on GitHub", ProblemFix.Sync));
            }

            var stashes = RunGit(true, "-C", repo, "log", "-g", "--format=%H", "refs/stash").Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (stashes.Length > 0) check.Problems.Add(($"{stashes.Length} set-aside change(s) made with 'git stash' - those never go to GitHub", ProblemFix.User));

            // Tags: a missing one can be added; one GitHub has with other content is the user's call
            static Dictionary<string, string> Tags(string dir) => RunGit(true, "-C", dir, "for-each-ref", "--format=%(refname)%09%(objectname)", "refs/tags/").Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().Split('\t')).Where(p => p.Length == 2)
                .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]);
            var remoteTags = remote == null ? new Dictionary<string, string>() : Tags(remote.Tmp);
            var newTags = new List<string>();
            var otherTags = new List<string>();
            foreach (var (tagRef, id) in Tags(repo))
            {
                if (remoteTags.TryGetValue(tagRef, out var there) && there == id) continue;
                string tag = tagRef.Substring("refs/tags/".Length);
                if (remote != null && !remoteTags.ContainsKey(tagRef))
                {
                    newTags.Add(tag);
                    check.Uploads.Add((tagRef, tagRef, $"the tag '{tag}'"));
                }
                else otherTags.Add(tag);
            }
            string Some(List<string> tags) => string.Join(", ", tags.Take(5)) + (tags.Count > 5 ? ", ..." : "");
            if (newTags.Count > 0) check.Problems.Add(($"tag(s) that aren't on GitHub: {Some(newTags)}", ProblemFix.Upload));
            if (otherTags.Count > 0) check.Problems.Add((remote == null ? $"tag(s) that aren't on GitHub: {Some(otherTags)}" : $"tag(s) that mean something else on GitHub: {Some(otherTags)}", ProblemFix.User));

            // Git names this folder by its real path - which differs from the one the user picked when that goes through a link
            string top = check.Bare ? "" : GitPathOf(repo, "--show-toplevel");
            foreach (var line in RunGit(true, "-C", repo, "worktree", "list", "--porcelain").Output.Split('\n'))
            {
                if (!line.StartsWith("worktree ")) continue;
                string other = line.Substring("worktree ".Length).Trim().Replace('/', '\\');
                // One whose folder is gone already (git calls it "prunable") can't be broken any more
                if (SamePath(other, repo) || (top != "" && SamePath(other, top)) || SamePath(other, gitDir) || !Directory.Exists(other)) continue;
                check.Problems.Add(($"another work folder uses this repository ({other}) - deleting this folder breaks it", ProblemFix.User));
            }

            // Hook scripts someone wrote themselves live only in .git\hooks (not the samples, nor ones a tool installs again)
            string hooks = GitPathOf(repo, "--git-path", "hooks");
            if (hooks != "" && RunGit(true, "-C", repo, "config", "--get", "core.hooksPath").Output.Trim() == "" && Directory.Exists(hooks))
            {
                var own = new List<string>();
                foreach (var hook in Directory.EnumerateFiles(hooks))
                {
                    if (hook.EndsWith(".sample", StringComparison.OrdinalIgnoreCase)) continue;
                    string text;
                    try { text = new FileInfo(hook).Length > 256 * 1024 ? "" : File.ReadAllText(hook); } catch { text = ""; }
                    if (Regex.IsMatch(text, @"git[ -]lfs|husky|pre-commit\.com|lefthook", RegexOptions.IgnoreCase)) continue;
                    own.Add(Path.GetFileName(hook));
                }
                if (own.Count > 0) check.Problems.Add(($"your own Git hook script(s) in .git\\hooks ({string.Join(", ", own.Take(5))}) - they aren't on GitHub", ProblemFix.User));
            }
        }

        // Concrete words for the files that aren't on GitHub, so it's clear what each one is
        static void DescribeOnlyHereFiles(FolderCheck check, RemoteSide? remote)
        {
            int described = 0;
            foreach (var file in check.Files.Where(f => f.Blocks))
            {
                if (file.Core) continue; // already says what it is and which .gitignore rule leaves it out
                string name = file.Path.Substring(file.Path.LastIndexOf('/') + 1);
                if (file.Path.StartsWith(BackupFolderName + "/"))
                {
                    file.Detail = "a copy of yours GitSync kept when a merge brought GitHub's version";
                }
                else if (IsSensitiveFileName(name))
                {
                    file.Detail = "holds passwords or keys - Sync keeps it off GitHub on purpose, so save it somewhere safe (e.g. a password manager)";
                }
                else if (file.Kind == FileKind.Different && described++ < 20)
                {
                    if (check.RepoRoot != null && !check.Bare)
                    {
                        bool edited = !RunGit(true, "-C", check.RepoRoot, "--literal-pathspecs", "diff", "--quiet", "HEAD", "--", check.Prefix + file.Path).Success;
                        file.Detail = edited ? "edited here and not uploaded" : "committed here, but that commit isn't on GitHub";
                    }
                    else if (remote != null && file.RemotePath != null)
                    {
                        string full = Path.Combine(check.Folder, file.Path.Replace('/', Path.DirectorySeparatorChar));
                        DateTime here = File.GetLastWriteTime(full);
                        string there = RunGit(true, "-C", remote.Tmp, "log", "-1", "--format=%cs", "refs/heads/" + remote.Branch, "--", file.RemotePath).Output.Trim();
                        file.Detail = $"saved here {here:yyyy-MM-dd}, last changed on GitHub {there}";
                        if (DateTime.TryParse(there, out var thereDate) && here.Date > thereDate.Date) file.Detail += " - so probably newer here";
                    }
                }
                if (file.Ignored && file.Kind != FileKind.Different && !IsSensitiveFileName(name) && file.Detail == "")
                {
                    file.Detail = "left out by .gitignore, so Sync won't upload it";
                }
            }
        }

        // =================================================================================
        // Git LFS: GitHub listing a file doesn't prove the file itself was uploaded
        // =================================================================================

        static void ConfirmLfsUploads(FolderCheck root)
        {
            var pending = root.All().SelectMany(c => c.Files.Where(f => f.Kind == FileKind.LfsUnconfirmed).Select(f => (Check: c, File: f))).ToList();
            if (pending.Count == 0) return;

            double mb = Math.Round(pending.Sum(p => p.File.LfsSize) / 1024.0 / 1024.0, 1);
            WriteLineColor(ConsoleColor.Yellow, $"\n{pending.Count} large file(s) ({mb} MB) are stored on GitHub with Git LFS. GitHub lists them, but to be");
            WriteLineColor(ConsoleColor.Yellow, "sure the files themselves were uploaded, GitSync has to download them once (into a temporary folder).");
            if (!AskYesNo("Download them to check?", true)) return;
            if (!RunGit(true, "lfs", "version").Output.Contains("git-lfs"))
            {
                WriteLineColor(ConsoleColor.Yellow, "Git LFS isn't installed, so they can't be checked.");
                return;
            }

            // The temporary clone borrows this computer's Git objects - and Git LFS would take the files from there too, which
            // proves nothing about GitHub. So it gets its own copy of the objects first, and stops borrowing.
            foreach (var tmp in pending.Select(p => p.Check.TempRepo!).Distinct())
            {
                string alternates = Path.Combine(tmp, "objects", "info", "alternates");
                if (!File.Exists(alternates)) continue;
                if (RunGit(true, "-C", tmp, "repack", "-a", "-d", "-q").Success) File.Delete(alternates);
            }

            foreach (var group in pending.GroupBy(p => (p.Check.TempRepo, p.File.LfsBranch)))
            {
                string tmp = group.Key.TempRepo!;
                if (File.Exists(Path.Combine(tmp, "objects", "info", "alternates"))) continue; // couldn't stop borrowing: can't confirm
                var paths = group.Select(p => p.File.RemotePath!).Distinct().ToList();
                var args = new List<string> { "-C", tmp, "lfs", "fetch", "origin", group.Key.LfsBranch! };
                if (paths.All(p => p.IndexOfAny(new[] { ',', '*', '?', '[', '\\' }) < 0)) args.Add("--include=" + string.Join(",", paths));
                var fetch = RunGitLive(args.ToArray());
                bool unexplained = false;
                foreach (var (_, file) in group)
                {
                    string oid = file.LfsOid!;
                    string stored = Path.Combine(tmp, "lfs", "objects", oid.Substring(0, 2), oid.Substring(2, 2), oid);
                    // Git LFS names an object the server doesn't have ("remote missing object <id>", GitHub: "does not exist")
                    bool reportedMissing = fetch.AllText.Split('\n').Any(l => l.Contains(oid) && Regex.IsMatch(l, @"missing|does not exist|not found|\b404\b", RegexOptions.IgnoreCase));
                    if (File.Exists(stored) && new FileInfo(stored).Length == file.LfsSize)
                    {
                        file.Kind = FileKind.LfsConfirmed;
                    }
                    else if (reportedMissing || fetch.Success)
                    {
                        file.Kind = FileKind.LfsMissing;
                        file.Detail = "GitHub lists it, but the file itself was never uploaded";
                    }
                    else
                    {
                        unexplained = true; // stays "not confirmed": a failed download says nothing about GitHub
                    }
                }
                if (unexplained) ExplainGitFailure(fetch, "Downloading the large files", outputAlreadyShown: true);
            }
        }

        // =================================================================================
        // Report, verdict and the one next step that fits
        // =================================================================================

        static string KindLabel(FileKind kind) => kind switch
        {
            FileKind.Same => "identical to GitHub",
            FileKind.SameExceptLineEndings => "identical apart from line endings (Windows vs. Linux style - not a real difference)",
            FileKind.LfsConfirmed => "large files on GitHub (Git LFS) - checked by downloading them",
            FileKind.Elsewhere => "on GitHub in another place (other folder, name or branch)",
            FileKind.NewerOnGitHub => "older versions - GitHub has a newer one",
            FileKind.InHistory => "removed or renamed on GitHub since - its history still has them",
            FileKind.Generated => "build output, downloaded packages and leftovers - recreated when needed",
            FileKind.Empty => "empty files - nothing to lose",
            FileKind.LfsUnconfirmed => "large files (Git LFS) GitHub lists - their upload wasn't checked",
            FileKind.LfsMissing => "large files (Git LFS) whose upload is missing on GitHub",
            FileKind.Different => "different from GitHub - this version isn't on GitHub",
            FileKind.OnlyHere => "only in this folder - not on GitHub at all",
            FileKind.Unreadable => "couldn't be read, so they couldn't be checked",
            _ => kind.ToString(),
        };

        static void PrintCheckReport(FolderCheck check)
        {
            foreach (var (color, text) in BuildCheckReport(check, full: false))
            {
                if (color is { } c) WriteLineColor(c, text);
                else Console.WriteLine(text);
            }
        }

        static List<(ConsoleColor? Color, string Text)> BuildCheckReport(FolderCheck root, bool full)
        {
            var lines = new List<(ConsoleColor? Color, string Text)>();
            void Add(string text, ConsoleColor? color = null) => lines.Add((color, text));
            int limit = full ? int.MaxValue : 15;

            Add("");
            Add("==========================================", ConsoleColor.Cyan);
            Add("     IS IT SAFE TO DELETE THIS FOLDER?", ConsoleColor.Cyan);
            Add("==========================================", ConsoleColor.Cyan);

            foreach (var check in root.All())
            {
                if (check != root) Add($"\n--- Inside: {RelativeTo(root.Folder, check.Folder)}/ (a Git repository of its own) ---", ConsoleColor.Cyan);
                Add($"Folder:  {check.Folder}");
                if (check.Url != "") Add($"GitHub:  {MaskUrl(check.Url)}" + (check.Branch != null ? $" (branch '{check.Branch}', plus its other branches and history)" : ""));

                var counts = check.Files.GroupBy(f => f.Kind == FileKind.SameExceptLineEndings ? FileKind.Same : f.Kind).OrderBy(g => g.Key).ToList();
                if (counts.Count > 0) Add("");
                foreach (var group in counts)
                {
                    var color = group.First().Blocks ? ConsoleColor.Red : group.Key == FileKind.LfsUnconfirmed ? ConsoleColor.Yellow : (ConsoleColor?)null;
                    string label = KindLabel(group.Key);
                    int lineEndings = group.Count(f => f.Kind == FileKind.SameExceptLineEndings);
                    if (group.Key == FileKind.Same && lineEndings > 0) label += $" ({lineEndings} of them only differ in Windows/Linux line endings)";
                    Add($"{group.Count(),7}  {label}", color);
                }
                if (check.OnlyOnGitHub > 0) Add($"{check.OnlyOnGitHub,7}  on GitHub but not in this folder (nothing to lose here)", ConsoleColor.DarkGray);

                // What isn't on GitHub, file by file
                foreach (var kind in new[] { FileKind.Different, FileKind.OnlyHere, FileKind.LfsMissing, FileKind.Unreadable, FileKind.LfsUnconfirmed })
                {
                    var files = check.Files.Where(f => f.Kind == kind).ToList();
                    if (files.Count == 0) continue;
                    Add($"\n{char.ToUpper(KindLabel(kind)[0])}{KindLabel(kind).Substring(1)}:", kind == FileKind.LfsUnconfirmed ? ConsoleColor.Yellow : ConsoleColor.Red);
                    foreach (var line in DescribeFiles(files, limit)) Add(line);
                }
                if (full)
                {
                    foreach (var kind in new[] { FileKind.NewerOnGitHub, FileKind.Elsewhere, FileKind.InHistory, FileKind.Generated, FileKind.Empty, FileKind.LfsConfirmed, FileKind.SameExceptLineEndings, FileKind.Same })
                    {
                        var files = check.Files.Where(f => f.Kind == kind).ToList();
                        if (files.Count == 0) continue;
                        Add($"\n{char.ToUpper(KindLabel(kind)[0])}{KindLabel(kind).Substring(1)}:");
                        foreach (var f in files) Add($"    {f.Path}" + (f.Detail != "" ? $"  ({f.Detail})" : ""));
                    }
                }
                else
                {
                    var generated = check.Files.Where(f => f.Kind == FileKind.Generated).GroupBy(f => f.Detail).OrderByDescending(g => g.Count()).ToList();
                    if (generated.Count > 0) Add($"Not on GitHub, but recreated when needed: {string.Join(", ", generated.Take(6).Select(g => $"{g.Key} ({g.Count()})"))}", ConsoleColor.DarkGray);
                }

                if (check.ReadError != null) Add($"\n{check.ReadError}", ConsoleColor.Red);
                if (check.Problems.Count > 0)
                {
                    Add("\nAlso only here:", ConsoleColor.Red);
                    foreach (var (text, _) in check.Problems) Add($"    - {text}");
                }
                foreach (var note in check.Notes) Add($"Note: {note}", ConsoleColor.DarkGray);
            }

            AddVerdict(root, Add);
            return lines;
        }

        // A file per line - or, for a long list, a line per folder ("AntColony/vcpkg_installed/  (7374 files)"), so what matters
        // stands out instead of 15 random names. Password and key files always get a line of their own. The saved full list
        // has every file.
        internal static List<string> DescribeFiles(List<CheckedFile> files, int limit)
        {
            static string Line(CheckedFile f) => $"    {f.Path}" + (f.Detail != "" ? $"  ({f.Detail})" : "");
            if (files.Count <= limit) return files.Select(Line).ToList();

            // Passwords/keys and files the project needs stand out on their own line; the rest is summed up by folder
            var secrets = files.Where(f => f.Core || IsSensitiveFileName(f.Path.Substring(f.Path.LastIndexOf('/') + 1))).ToList();
            var lines = secrets.Take(limit).Select(Line).ToList();
            var groups = files.Except(secrets).GroupBy(f => FolderOf(f.Path)).OrderByDescending(g => g.Count()).ToList();
            int room = Math.Max(5, limit - lines.Count);
            foreach (var group in groups.Take(room))
            {
                var inFolder = group.ToList();
                lines.Add(inFolder.Count == 1 ? Line(inFolder[0])
                    : $"    {group.Key}  ({inFolder.Count} files{(inFolder.All(f => f.Ignored) ? ", left out by .gitignore" : "")})");
            }
            int rest = groups.Skip(room).Sum(g => g.Count()) + Math.Max(0, secrets.Count - limit);
            if (rest > 0) lines.Add($"    ... and {rest} more file(s) in other places (see the full list)");
            return lines;
        }

        // The folder a file is grouped under in a long list: its first two folders ("data/raw/"), or the file itself at the top
        static string FolderOf(string path)
        {
            var parts = path.Split('/');
            return parts.Length == 1 ? path : string.Join("/", parts.Take(Math.Min(parts.Length - 1, 2))) + "/";
        }

        static void AddVerdict(FolderCheck root, Action<string, ConsoleColor?> add)
        {
            add("\n==========================================", ConsoleColor.Cyan);
            var all = root.All().ToList();
            var blocking = all.SelectMany(c => c.Files.Where(f => f.Blocks)).ToList();
            int problems = all.Sum(c => c.Problems.Count);
            int generated = all.Sum(c => c.Files.Count(f => f.Kind == FileKind.Generated));

            if (root.Safe)
            {
                add("VERDICT: SAFE TO DELETE", ConsoleColor.Green);
                add("Everything in this folder is on GitHub - or GitHub has a newer version of it.", ConsoleColor.Green);
                if (generated > 0) add($"({generated} file(s) of build output and downloaded packages aren't, but they're recreated when you build or install the project.)", null);
                add("GitSync never deletes anything itself. Move the folder to the Recycle Bin, so you can still get it back.", null);
                return;
            }
            if (!root.HasBlockers)
            {
                add("VERDICT: ALMOST CERTAINLY SAFE - NOT FULLY CONFIRMED", ConsoleColor.Yellow);
                add("Everything is on GitHub, except that the upload of some large (Git LFS) files wasn't checked.", ConsoleColor.Yellow);
                add("Run this check again and let it download them to be sure.", null);
                return;
            }

            add("VERDICT: NOT SAFE TO DELETE YET", ConsoleColor.Red);
            string what = blocking.Count > 0 && problems > 0 ? $"{blocking.Count} file(s) and {problems} other thing(s)"
                        : blocking.Count > 0 ? $"{blocking.Count} file(s)" : $"{problems} thing(s)";
            var unread = all.Where(c => c.ReadError != null).ToList();
            if (blocking.Count > 0 || problems > 0) add($"{what} would be lost - they only exist on this computer.", ConsoleColor.Red);
            if (unread.Count > 0) add($"GitHub's copy of {(unread.Count == 1 && unread[0] == root ? "this folder" : $"{unread.Count} project(s)")} couldn't be read, so that part couldn't be checked at all.", ConsoleColor.Red);

            // What to do about it - only the parts that apply
            var secrets = blocking.Where(f => IsSensitiveFileName(f.Path.Substring(f.Path.LastIndexOf('/') + 1))).ToList();
            var core = blocking.Where(f => f.Core).ToList();
            var ignored = blocking.Where(f => f.Ignored && !f.Core && !secrets.Contains(f)).ToList();
            add("\nWhat to do:", null);
            if (unread.Count > 0) add("  - Check the GitHub address and your internet connection (and that you're signed in), then check again.", null);
            if (blocking.Any(f => f.Kind == FileKind.LfsMissing)) add("  - Upload the missing large files again (GitSync offers to do it now).", null);
            var toSync = SyncCandidates(root);
            if (toSync.Count > 0)
            {
                var names = toSync.Select(c => RelativeTo(root.Folder, SyncFolderFor(c))).Where(n => n != ".").Select(n => n + "/").ToList();
                add("  - Upload the changes with [1] Sync" + (names.Count > 0 ? $" (in {string.Join(", ", names)})" : "") + ", then check again.", null);
            }
            if (core.Count > 0)
            {
                add($"  - Your .gitignore leaves out {core.Count} file(s) the project needs (e.g. {string.Join(", ", core.Take(3).Select(f => f.Path))}),", null);
                add("    so they were never uploaded. Sync names the rule that does it and offers to upload them anyway.", null);
            }
            foreach (var part in all.Where(c => MatchesOnlyPartOfGitHub(c) && c.Files.Any(f => f.Blocks)))
            {
                add($"  - This folder is only the '{part.Prefix}' part of the GitHub project: copy its changed files into that folder of a clone", null);
                add("    of the project ([3] Clone), Sync the clone, then check again.", null);
            }
            if (all.Any(c => c.Uploads.Count > 0 && CanUploadTo(c))) add("  - Upload the branches and tags GitHub doesn't have yet (GitSync offers to do it now).", null);
            if (secrets.Count > 0) add($"  - Save {string.Join(", ", secrets.Take(3).Select(f => f.Path))} somewhere safe (a password manager) - passwords and keys don't belong on GitHub.", null);
            if (ignored.Count > 0)
            {
                string where = string.Join(", ", ignored.GroupBy(f => FolderOf(f.Path)).OrderByDescending(g => g.Count()).Take(3).Select(g => g.Key));
                add($"  - {ignored.Count} file(s) are left out by .gitignore, so Sync won't upload them (in {where}).", null);
                add("    If that's build output or downloaded packages, losing it is fine - anything else, copy it somewhere safe first.", null);
            }
            if (all.Any(c => c.Problems.Any(p => p.Fix == ProblemFix.User || (p.Fix == ProblemFix.Upload && !CanUploadTo(c)))))
            {
                add("  - GitSync can't upload the other things under \"Also only here\": sort them out yourself (e.g. in VS Code), or keep the folder.", null);
            }
            if (blocking.Any(f => f.Kind == FileKind.Unreadable)) add("  - Close programs that have files open here, then check again.", null);
        }

        // Branches and tags are uploaded to the repository's own 'origin' - only when that's the GitHub repository it was compared with
        static bool CanUploadTo(FolderCheck c) => c.RepoRoot != null && c.Url != "" && SameRepository(GetOriginOf(c.RepoRoot), c.Url);

        static void SaveCheckReport(FolderCheck check)
        {
            try
            {
                string dir = Path.Combine(DataDir, "checks");
                Directory.CreateDirectory(dir);
                string name = string.Concat(Path.GetFileName(check.Folder.TrimEnd('\\')).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                string file = Path.Combine(dir, $"{(name == "" ? "drive" : name)}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                File.WriteAllLines(file, BuildCheckReport(check, full: true).Select(l => l.Text));
                WriteLineColor(ConsoleColor.DarkGray, $"The full list is saved in {file}");
                Log($"Safe-to-delete check of {check.Folder}: {(check.Safe ? "safe" : check.HasBlockers ? "not safe" : "not confirmed")} - {file}");
            }
            catch { }
        }

        // Only the steps that fit what was found - each one a single yes/no - and then the check runs again by itself
        static void OfferNextStep(FolderCheck check)
        {
            if (!check.HasBlockers) return;
            int pushesBefore = successfulPushes;

            // 1. Large files GitHub lists but never got: a push only sends them along with new commits, so they're sent directly
            var lfsRepos = check.All().Where(c => c.Files.Any(f => f.Kind == FileKind.LfsMissing) && CanUploadTo(c)).ToList();
            if (lfsRepos.Count > 0 && AskYesNo("\nUpload the missing large files to GitHub now?", true))
            {
                foreach (var c in lfsRepos)
                {
                    var push = RunGitLive("-C", c.RepoRoot!, "lfs", "push", "--all", "origin");
                    if (push.Success) successfulPushes++;
                    else ExplainGitFailure(push, "Uploading the large files", outputAlreadyShown: true);
                }
            }

            // 2. Other branches and tags GitHub doesn't have yet: added as they are, nothing on GitHub is overwritten
            var refRepos = check.All().Where(c => c.Uploads.Count > 0 && CanUploadTo(c)).ToList();
            if (refRepos.Count > 0)
            {
                var labels = refRepos.SelectMany(c => c.Uploads.Select(u => u.Label)).ToList();
                string what = labels.Count <= 3 ? string.Join(" and ", labels) : $"{labels.Count} branches and tags";
                if (AskYesNo($"\nUpload {what} to GitHub now (they show up there next to your other branches)?", true))
                {
                    foreach (var c in refRepos) UploadBranchesAndTags(c);
                }
            }

            // 3. Changed and new files, and the checked-out branch: that's what Sync does
            var toSync = SyncCandidates(check);
            bool syncQueued = false;
            if (toSync.Count > 0)
            {
                string question = toSync.Count > 1
                    ? $"\nUpload what's missing in these {toSync.Count} projects now with [1] Sync, one after the other (then GitSync checks again)?"
                    : toSync[0] != check ? $"\nUpload what's missing in {RelativeTo(check.Folder, SyncFolderFor(toSync[0]))}/ now with [1] Sync (then GitSync checks again)?"
                    : check.Url == "" && check.RepoRoot == null ? "\nPut this folder on GitHub now with [1] Sync (then GitSync checks again)?"
                    : "\nUpload what's missing now with [1] Sync (then GitSync checks again)?";
                if (AskYesNo(question, true))
                {
                    foreach (var c in toSync)
                    {
                        // A plain folder becomes a repository linked to the address it was compared with, without asking for it again
                        Action sync = c.Url != "" && c.RepoRoot == null ? () => { suggestedRemoteUrl = c.Url; HandleLocalSync(); } : HandleLocalSync;
                        QueueMode("Sync", sync, SyncFolderFor(c), onlyIf: null);
                    }
                    syncQueued = true;
                }
            }

            // 4. Then the same check again - after Sync, or right away when something was uploaded here
            if (syncQueued) QueueMode("Check", HandleSafeToDelete, check.Folder, onlyIf: () => successfulPushes > pushesBefore);
            else if (successfulPushes > pushesBefore)
            {
                WriteLineColor(ConsoleColor.Green, "Uploaded. Checking again...");
                QueueMode("Check", HandleSafeToDelete, check.Folder, onlyIf: null);
            }
        }

        // Pushes the branches and tags GitHub can simply get - after the same checks Sync makes before every push: nothing
        // that looks like a password or key, nothing over GitHub's 100 MB limit (those need Sync, which helps take them out)
        internal static void UploadBranchesAndTags(FolderCheck c)
        {
            string previous = Environment.CurrentDirectory;
            try
            {
                Directory.SetCurrentDirectory(c.RepoRoot!);
                // "Already on GitHub" must be true right now for the scans below (like Sync's push does)
                RefreshRemoteBranches();
                foreach (var (source, target, label) in c.Uploads)
                {
                    var secrets = FindSecretsInUnpushedCommits(source);
                    var large = FindLargeBlobsToPush(source);
                    if (secrets.Count > 0 || large.Count > 0)
                    {
                        string why = secrets.Count > 0 ? $"a file that looks like it holds a password or key ({secrets[0].File})" : $"a file over GitHub's 100 MB limit ({large[0].Path})";
                        WriteLineColor(ConsoleColor.Yellow, $"\nNot uploaded: {label} has {why}.");
                        Console.WriteLine("Switch to it in your Git tool and run [1] Sync - it helps you take such files out before uploading.");
                        continue;
                    }
                    var push = RunGitLive("push", "origin", $"{source}:{target}");
                    if (push.Success)
                    {
                        successfulPushes++;
                        WriteLineColor(ConsoleColor.Green, $"Uploaded {label}.");
                    }
                    else ExplainGitFailure(push, $"Uploading {label}", outputAlreadyShown: true);
                }
            }
            finally
            {
                try { Directory.SetCurrentDirectory(previous); } catch { }
            }
        }

        // The checked folders whose missing parts Sync would upload: changed or new files that aren't ignored or secret, and
        // commits of the checked-out branch. (Secrets, ignored files, stashes and the like are for the user to handle.)
        static List<FolderCheck> SyncCandidates(FolderCheck root)
        {
            return root.All().Where(c => !c.Bare && c.ReadError == null && !MatchesOnlyPartOfGitHub(c) && (
                c.Files.Any(f => f.Kind is FileKind.Different or FileKind.OnlyHere && (!f.Ignored || f.Core) && !IsSensitiveFileName(f.Path.Substring(f.Path.LastIndexOf('/') + 1)))
                || c.Problems.Any(p => p.Fix == ProblemFix.Sync))).ToList();
        }

        // A plain folder that matched a folder inside the GitHub project (a kept "src"): Sync would put its files at the top
        // of the repository instead, so it isn't offered for it
        static bool MatchesOnlyPartOfGitHub(FolderCheck c) => c.RepoRoot == null && c.Prefix != "";

        // Where Sync has to run: the repository, or - for a ZIP download - the wrapper folder that holds the project itself
        // (syncing the outer folder would upload a second copy of the project into a subfolder)
        static string SyncFolderFor(FolderCheck c) =>
            c.RepoRoot ?? (c.LocalBase != "" ? Path.Combine(c.Folder, c.LocalBase.Replace('/', Path.DirectorySeparatorChar)) : c.Folder);

        // =================================================================================
        // Helpers
        // =================================================================================

        // Git's id for a file's content ("blob <size>\0<content>", SHA-1 - or SHA-256 for repositories that use it)
        internal static string GitBlobId(Stream content, long length, bool sha256)
        {
            using var hash = IncrementalHash.CreateHash(sha256 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1);
            hash.AppendData(Encoding.ASCII.GetBytes($"blob {length}\0"));
            long total = 0;
            int read;
            while ((read = content.Read(hashBuffer, 0, hashBuffer.Length)) > 0)
            {
                hash.AppendData(hashBuffer, 0, read);
                total += read;
            }
            if (total != length) throw new IOException("the file changed while it was being read");
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        internal static string GitBlobId(byte[] content, bool sha256)
        {
            using var stream = new MemoryStream(content, writable: false);
            return GitBlobId(stream, content.Length, sha256);
        }

        static string FileSha256(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }

        // The same text with the other kind of line endings (Git converts between them, so they count as the same file)
        internal static IEnumerable<byte[]> LineEndingVariants(byte[] data)
        {
            if (Array.IndexOf(data, (byte)0, 0, Math.Min(data.Length, 8000)) >= 0) yield break; // binary
            string text = Encoding.Latin1.GetString(data);
            string lf = text.Replace("\r\n", "\n");
            if (lf != text) yield return Encoding.Latin1.GetBytes(lf);
            string crlf = lf.Replace("\n", "\r\n");
            if (crlf != text) yield return Encoding.Latin1.GetBytes(crlf);
        }

        // An empty Git folder elsewhere, for reading a plain folder's .gitignore rules without writing anything into it
        static string NewScratchRepo()
        {
            string tmp = Path.Combine(Path.GetTempPath(), CheckTempPrefix + Guid.NewGuid().ToString("N").Substring(0, 8));
            checkTempRepos.Add(tmp);
            RunGit(true, "init", "--bare", "--quiet", tmp);
            return tmp;
        }

        internal static void DeleteCheckTempRepos()
        {
            foreach (var dir in checkTempRepos) DeleteTempRepo(dir);
            checkTempRepos.Clear();
        }

        // Only ever a GitSync-check-* folder directly in the temp folder
        static void DeleteTempRepo(string dir)
        {
            try
            {
                string full = Path.GetFullPath(dir);
                string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (!Path.GetFileName(full).StartsWith(CheckTempPrefix)) return;
                if (!string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase)) return;
                if (!Directory.Exists(full)) return;
                foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(full, true);
            }
            catch { }
        }

        // Left behind when a check was interrupted (window closed)
        static void RemoveOldCheckTempRepos()
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), CheckTempPrefix + "*"))
                {
                    if (Directory.GetLastWriteTime(dir) < DateTime.Now.AddHours(-12)) DeleteTempRepo(dir);
                }
            }
            catch { }
        }
    }
}
