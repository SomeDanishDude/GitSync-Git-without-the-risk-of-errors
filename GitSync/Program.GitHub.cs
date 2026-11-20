using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GitSync
{
    // What GitHub said when GitSync asked it to create a repository
    internal sealed class GitHubRepoResult
    {
        public string? Url;          // the repository's address (https://github.com/<you>/<name>.git)
        public bool AlreadyExists;   // the account already has a repository with that name; Url points at it
        public string? Error;        // in plain words, when it didn't work
    }

    public partial class Program
    {
        private const string GitHubApi = "https://api.github.com";
        // Where GitSync itself lives - its releases tell everyone's copy that there is a newer version
        private const string GitSyncRepository = "SomeDanishDude/GitSync-Git-without-the-risk-of-errors";

        // Swappable, so the tests can stand in for GitHub
        internal static Func<string, GitHubRepoResult> CreateGitHubRepository = CreateGitHubRepositoryWithApi;

        // =================================================================================
        // Creating the project's repository on GitHub
        // =================================================================================

        // A GitHub repository name from a folder name: letters, digits, '-', '_' and '.'; everything else becomes '-'
        internal static string SuggestRepositoryName(string folderName)
        {
            string name = Regex.Replace(folderName.Trim(), @"[^A-Za-z0-9._-]+", "-");
            name = Regex.Replace(name, "-{2,}", "-").Trim('-', '.');
            if (name.Length > 100) name = name.Substring(0, 100).TrimEnd('-', '.');
            return name.Length > 0 ? name : "my-project";
        }

        // The project isn't on GitHub yet: instead of a trip to github.com, GitSync creates a private repository for it on the
        // user's account - one question. Returns the repository's address, or null to paste an address instead.
        internal static string? OfferToCreateGitHubRepository()
        {
            string folder = sessionFolder ?? Environment.CurrentDirectory;
            string name = SuggestRepositoryName(Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)));
            WriteLineColor(ConsoleColor.Cyan, "\nThis project isn't on GitHub yet.");
            string question = $"Create a private GitHub repository called '{name}' for it?";
            if (!(UsualAnswer(question, "yes") || AskYesNo(question, true))) return null;

            Console.WriteLine("Asking GitHub (a sign-in window may pop up)...");
            var result = CreateGitHubRepository(name);
            if (result.Url != null && !result.AlreadyExists)
            {
                WriteLineColor(ConsoleColor.Green, $"Created {ToWebUrl(result.Url) ?? result.Url} - private, so only you can see it (that can be changed on GitHub).");
                Log($"Created the GitHub repository {MaskUrl(result.Url)}");
                return result.Url;
            }
            if (result.Url != null)
            {
                WriteLineColor(ConsoleColor.Yellow, $"You already have a repository called '{name}' on GitHub: {ToWebUrl(result.Url) ?? result.Url}");
                // Always asked, also in a batch: a repository with the same name can be a different project
                return AskYesNo("Use that one?", true) ? result.Url : null;
            }
            WriteLineColor(ConsoleColor.Yellow, $"GitHub didn't create it: {result.Error}");
            Console.WriteLine("You can create it on github.com yourself and paste its address below.");
            return null;
        }

        // Creates a private, empty repository on the signed-in user's account through GitHub's API
        static GitHubRepoResult CreateGitHubRepositoryWithApi(string name)
        {
            // Scripted test runs: a folder plays GitHub, so running GitSync's scenarios never creates real repositories
            string? stand_in = Environment.GetEnvironmentVariable("GITSYNC_TEST_GITHUB_FOLDER");
            if (!string.IsNullOrEmpty(stand_in))
            {
                string repo = Path.Combine(stand_in, name + ".git");
                if (Directory.Exists(repo)) return new GitHubRepoResult { AlreadyExists = true, Url = repo };
                var init = RunGit(true, "init", "--quiet", "--bare", "-b", "main", repo);
                return init.Success ? new GitHubRepoResult { Url = repo } : new GitHubRepoResult { Error = init.AllText };
            }

            string? login = GitHubSignIn();
            string? token = login == null ? null : CredentialField(login, "password");
            if (string.IsNullOrEmpty(token))
            {
                return new GitHubRepoResult { Error = "GitSync couldn't get your GitHub sign-in. Sign in once (the first push of any project asks for it), then try again." };
            }

            try
            {
                using var http = GitHubClient(token);
                string body = JsonSerializer.Serialize(new Dictionary<string, object> { ["name"] = name, ["private"] = true, ["auto_init"] = false });
                using var response = http.PostAsync($"{GitHubApi}/user/repos", new StringContent(body, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
                string text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (response.StatusCode == HttpStatusCode.Created)
                {
                    TellCredentialHelper("approve", login!); // keep a sign-in that just worked
                    using var created = JsonDocument.Parse(text);
                    return new GitHubRepoResult { Url = created.RootElement.GetProperty("clone_url").GetString() };
                }
                if (response.StatusCode == HttpStatusCode.UnprocessableEntity && text.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                {
                    TellCredentialHelper("approve", login!);
                    using var user = http.GetAsync($"{GitHubApi}/user").GetAwaiter().GetResult();
                    if (!user.IsSuccessStatusCode) return new GitHubRepoResult { Error = "a repository with that name already exists on your account" };
                    using var me = JsonDocument.Parse(user.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                    return new GitHubRepoResult { AlreadyExists = true, Url = $"https://github.com/{me.RootElement.GetProperty("login").GetString()}/{name}.git" };
                }
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    TellCredentialHelper("reject", login!); // a sign-in GitHub refuses is forgotten, so the next attempt asks again
                    return new GitHubRepoResult { Error = "GitHub didn't accept the saved sign-in. It was forgotten - you'll be asked to sign in again next time." };
                }
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                {
                    return new GitHubRepoResult { Error = "the saved GitHub sign-in isn't allowed to create repositories (a token with too few permissions?)." };
                }
                return new GitHubRepoResult { Error = $"GitHub answered {(int)response.StatusCode}: {ApiMessage(text)}" };
            }
            catch (Exception ex)
            {
                return new GitHubRepoResult { Error = $"GitHub couldn't be reached ({ex.Message})." };
            }
        }

        static HttpClient GitHubClient(string? token, int timeoutSeconds = 30)
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GitSync");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            if (token != null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return http;
        }

        static string ApiMessage(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "";
            }
            catch
            {
                return json.Length > 200 ? json.Substring(0, 200) : json;
            }
        }

        // The sign-in Git uses for github.com ("protocol=..., username=..., password=<token>"), from the credential helper - the
        // same as for a push, and it may open the same sign-in window. Never shown or logged; only handed to GitHub's own API.
        static string? GitHubSignIn()
        {
            var (ok, output) = RunGitPrivately(new[] { "credential", "fill" }, "protocol=https\nhost=github.com\n\n");
            return ok && output.Contains("password=") ? output : null;
        }

        static void TellCredentialHelper(string verb, string signIn) => RunGitPrivately(new[] { "credential", verb }, signIn.TrimEnd('\n') + "\n\n");

        static string? CredentialField(string signIn, string field) =>
            signIn.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith(field + "="))?.Substring(field.Length + 1);

        // Runs git with something secret going in or out: nothing of it is printed or written to the log - only that it ran
        static (bool Success, string Output) RunGitPrivately(string[] args, string input)
        {
            try
            {
                using var p = new Process { StartInfo = CreateGitStartInfo(args) };
                p.Start();
                var output = p.StandardOutput.ReadToEndAsync();
                var error = p.StandardError.ReadToEndAsync();
                try
                {
                    p.StandardInput.Write(input);
                    p.StandardInput.Close();
                }
                catch (IOException) { }
                p.WaitForExit();
                Log($"$ git {string.Join(" ", args)}  (exit {p.ExitCode})");
                return (p.ExitCode == 0, output.Result);
            }
            catch (Win32Exception)
            {
                return (false, "");
            }
        }

        // =================================================================================
        // A newer GitSync on GitHub - so friends who run a copy of the exe hear about fixes
        // =================================================================================

        internal static Version CurrentVersion => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);

        // "v1.2.0" -> 1.2.0.0; null for a tag that isn't a version number
        internal static Version? ParseReleaseVersion(string tag)
        {
            var match = Regex.Match(tag.Trim(), @"^v?(\d+(\.\d+){1,3})$", RegexOptions.IgnoreCase);
            return match.Success && Version.TryParse(match.Groups[1].Value, out var version) ? Full(version) : null;
        }

        static Version Full(Version v) => new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

        internal static bool IsNewerThanThis(Version latest) => Full(latest) > Full(CurrentVersion);

        // At startup: one line when GitHub has a newer GitSync - nothing at all otherwise (offline, no releases yet, scripted runs)
        static void CheckForNewerVersion()
        {
            if (Console.IsInputRedirected || Environment.GetEnvironmentVariable("GITSYNC_NO_UPDATE_CHECK") == "1") return;
            try
            {
                using var http = GitHubClient(token: null, timeoutSeconds: 3);
                using var response = http.GetAsync($"{GitHubApi}/repos/{GitSyncRepository}/releases/latest").GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode) return;
                using var release = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                string tag = release.RootElement.GetProperty("tag_name").GetString() ?? "";
                string page = release.RootElement.GetProperty("html_url").GetString() ?? "";
                var latest = ParseReleaseVersion(tag);
                if (latest == null || !IsNewerThanThis(latest)) return;
                WriteLineColor(ConsoleColor.Yellow, $"A newer GitSync ({tag}) is available - download GitSync.exe here: {page}");
                Log($"Newer version available: {tag} (this is {CurrentVersion})");
            }
            catch { }
        }
    }
}
