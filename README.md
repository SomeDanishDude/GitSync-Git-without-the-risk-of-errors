# GitSync — Git without the risk of errors

A small Windows console tool that puts a folder on GitHub, keeps it in sync, pulls the latest changes or clones a
project — with safety checks first, and plain-language explanations when something goes wrong.

## What it does

| Mode | What happens |
| --- | --- |
| **[1] Sync Local Folder** | Turns any folder into a Git repository (or uses the one that is there), suggests a `.gitignore`, LICENSE and README, checks for files over GitHub's 100 MB limit, secrets (`.env` files, API keys, private keys) and TODOs, then commits, merges anything new from the remote and pushes. Before pushing, it also checks the commits that haven't been pushed yet - including ones made in VS Code or plain git - for large files and secrets. |
| **[2] Pull Latest Changes** | Brings a repository up to date. Uncommitted work is committed first, so nothing gets overwritten; local commits can be pushed right after. |
| **[3] Clone & Setup** | Clones a repository into a folder you choose and offers the matching setup command (`npm install`, `pip install -r requirements.txt`, `dotnet restore`, `cargo build`, ...). |
| **[4] Status** | "Am I up to date?" — branch, last commit, commits waiting on the remote or not pushed yet, and uncommitted files. |
| **[5] Open Log File** | Every git command GitSync ran, with its full output — useful when you want to know what happened last time. |

- A daily Sync of a project that is already on GitHub asks almost nothing: pick the folder, type what you changed,
  confirm. The branch and remote questions only come up the first time.
- When the same file was changed on your computer *and* on the remote, GitSync asks which version to keep
  (yours, the remote's, file by file, or edit them yourself) instead of failing.
- If your folder is on `master` but the GitHub repository uses `main`, GitSync puts your work on `main` instead of
  creating a second branch that GitHub doesn't show. A branch that is linked to a differently named remote branch
  (e.g. made with `git checkout -b work origin/main`) is asked about once, and the answer is remembered.
- It checks the remote URL before using it, and asks before joining two unrelated projects (e.g. a pasted wrong URL).
- A file or secret that has to stay out of commits that aren't pushed yet is taken out by rebuilding only those
  commits - never anything that is already on the remote, and never on top of work a teammate pushed meanwhile.
- Typing `abort`, `cancel` or `exit` at any prompt cancels the current step and puts the folder back the way it was.
  Once something is committed, your work is always kept, and lines you added yourself to a file GitSync changed stay.
- If a push fails you can try again, keep everything for later, switch to a different remote URL, or undo what
  GitSync did to a brand-new repository.
- Recent folders are offered first (Enter = the latest one). You can also drag a folder onto `GitSync.exe`.

## Requirements

- Windows 10 or 11
- [Git for Windows](https://git-scm.com/download/win) — it includes Git Credential Manager, which opens the GitHub
  sign-in window the first time you push.

## Building and testing

```bash
dotnet build
dotnet test
```

Every build also publishes a self-contained single-file `GitSync/bin/SingleFile/GitSync.exe` that runs without .NET
installed. The integration tests run real git against temporary repositories, so git must be on `PATH`.

## Where GitSync keeps its files

`%LOCALAPPDATA%\GitSync\` holds `gitsync_log.txt` (the log) and `recent_folders.txt`. Set the `GITSYNC_DATA_DIR`
environment variable to keep them somewhere else.

## When something goes wrong

GitSync shows git's own message plus what it means. The most common ones:

| Git says | What to do |
| --- | --- |
| `Authentication failed` / `Invalid username or password` | GitHub no longer accepts your account password for Git. Sign in through the browser window, or use a personal access token. GitSync can forget a wrong saved login for you. |
| `Repository not found` | Create the repository on GitHub first, check the URL, and make sure you're signed in with an account that can see it. |
| `[rejected] ... (fetch first)` | Someone pushed in the meantime. GitSync offers to merge their commits and try again. |
| `Permission denied (publickey)` / `Host key verification failed` | The SSH address needs an SSH key set up with GitHub. GitSync offers to switch to the HTTPS address, which signs in through the browser instead. |
| `detected dubious ownership` | The folder belongs to another Windows user (common on USB drives). GitSync offers to mark it as safe. |
| `exceeds GitHub's file size limit` | A file over 100 MB is in your commits. Track it with Git LFS (GitSync offers this before committing) or remove it. |
