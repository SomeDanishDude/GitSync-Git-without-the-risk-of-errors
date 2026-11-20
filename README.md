# GitSync — Git without the risk of errors

A small Windows console tool that puts a folder on GitHub, keeps it in sync, pulls the latest changes or clones a
project — with safety checks first, and plain-language explanations when something goes wrong.

## What it does

| Mode | What happens |
| --- | --- |
| **[1] Sync Local Folder** | Turns any folder into a Git repository (or uses the one that is there), creates its GitHub repository if it has none yet, suggests a `.gitignore`, LICENSE and README, checks for files over GitHub's 100 MB limit and secrets (`.env` files, API keys, Discord/Telegram bot tokens, passwords in database addresses, private keys), then commits, merges anything new from the remote and pushes. Before pushing, it also checks the commits that haven't been pushed yet - including ones made in VS Code or plain git - for large files and secrets. Pick a folder that holds several projects, and it uploads each to its own repository. |
| **[2] Pull Latest Changes** | Brings a repository up to date. Uncommitted work is committed first, so nothing gets overwritten; local commits can be pushed right after. |
| **[3] Clone & Setup** | Clones a repository into a folder you choose and offers the matching setup command (`npm install`, `pip install -r requirements.txt`, `dotnet restore`, `cargo build`, ...). |
| **[4] Status** | "Am I up to date?" — branch, last commit, commits waiting on the remote or not pushed yet, uncommitted files, and work that only this computer has (other branches, stashes). |
| **[5] Safe to delete?** | Compares a folder with its GitHub repository, file by file, and gives a clear verdict: SAFE TO DELETE, or NOT SAFE with exactly what would be lost and what to do about it. Only reads the folder. |
| **[6] Open Log File** | Every git command GitSync ran, with its full output — useful when you want to know what happened last time. |

- A daily Sync of a project that is already on GitHub asks almost nothing: pick the folder, type what you changed,
  confirm. The branch and remote questions only come up the first time.
- A project that isn't on GitHub yet doesn't need a trip to github.com: GitSync asks once, then creates a private
  repository named after the folder on your account (with the sign-in Git already uses) and uploads to it.
- Pick a folder that holds several projects (like `VS Projects`), and GitSync lists them, asks once, and uploads each
  to its own repository - questions that have a usual answer are answered for you along the way, anything that needs
  your judgment (passwords, big files, conflicts) is still asked. Then it checks all of them with [5].
- Files over GitHub's 100 MB limit are one question for all of them: store them with Git LFS (the default) or leave
  them out. A folder used as the remote (a NAS or USB drive) has no limit, so nothing is asked there.
- TODO/FIXME comments are mentioned in one line - they never stop an upload.
- When the same file was changed on your computer *and* on the remote, GitSync asks which version to keep
  (yours, the remote's, file by file, or edit them yourself) instead of failing.
- Typing another branch at the branch question - `master` while your newest work is on `phase6` - doesn't quietly switch
  your folder back to older files: GitSync says which commits that branch lacks and offers to upload your newest work to
  it (nothing in your folder changes). Branches that went their own way are only switched after asking.
- The `.gitignore` suggestion knows Unity, Unreal, Godot, Visual Studio, C++/CMake, Python, Jupyter, Node.js, Java/Maven,
  Gradle/Android, Rust, Go, Flutter, Ruby, Swift, PHP, PlatformIO and LaTeX projects.
- If your folder is on `master` but the GitHub repository uses `main`, GitSync puts your work on `main` instead of
  creating a second branch that GitHub doesn't show. A branch that is linked to a differently named remote branch
  (e.g. made with `git checkout -b work origin/main`) is asked about once, and the answer is remembered.
- It checks the remote URL before using it, and asks before joining two unrelated projects (e.g. a pasted wrong URL).
- If the `.gitignore` leaves out files the project needs — a rule that is too broad, like `*.txt` taking `CMakeLists.txt`
  along, or `bin/` hiding a Unity plugin's DLLs — GitSync names the files and the rule, and asks once whether to upload
  them anyway. It leaves alone what is meant to stay out: generated files in build folders (also CMake builds and
  Python environments, whatever their name), downloaded packages, files a rule names on purpose (`local_settings.py`),
  and passwords and keys. Answer no, and it doesn't ask about those files again.
- A file that only lives on your computer (ignored, like `.env`) is never overwritten when the remote starts tracking a
  file with the same name: your copy is moved to `.gitsync-backup\` first, and GitSync tells you where it is.
- A file or secret that has to stay out of commits that aren't pushed yet is taken out by rebuilding only those
  commits - never anything that is already on the remote, and never on top of work a teammate pushed meanwhile.
- Typing `abort`, `cancel` or `exit` at any prompt cancels the current step and puts the folder back the way it was.
  Once something is committed, your work is always kept, and lines you added yourself to a file GitSync changed stay.
- If a push fails you can try again, keep everything for later, switch to a different remote URL, or undo what
  GitSync did to a brand-new repository.
- Recent folders are offered first (Enter = the latest one). You can also drag a folder onto `GitSync.exe`.

## Before you delete a folder: [5] Safe to delete?

Pick the folder; GitSync works out the rest and only asks what it can't tell by itself (the GitHub address of a folder
that isn't linked to GitHub). It downloads what GitHub has into a temporary folder and compares every file:

- **On GitHub:** identical files (also when only Windows/Linux line endings differ), files GitHub has a *newer* version of,
  and files that moved to another folder, name or branch there.
- **Not on GitHub:** edited and new files, ignored files such as `.env` (save passwords and keys in a password manager),
  commits on any branch that were never pushed, stashes, tags, unfinished merges, other work folders using the
  repository, your own Git hook scripts, and large (Git LFS) files whose upload never happened.
- **Doesn't count:** build output and downloaded packages (`node_modules`, `vcpkg_installed`, CMake's and CLion's build
  folders, an ignored `bin/`...), also the data files a build copies into them, and an empty Visual Studio
  `.vcxproj.user` - all recreated by building or installing. Long lists are summed up by folder.
- **Flagged separately:** files the project needs that its `.gitignore` leaves out (so they were never uploaded), with
  the rule that does it — Sync then offers to upload them.
- Works for a clone, a copy or ZIP download without `.git` (also inside a `project-main\` wrapper folder), part of a
  project, a folder holding several projects (each checked against its own GitHub repository), and Git LFS files — which
  it downloads once to prove they were really uploaded.

When something is missing, GitSync offers only the steps that fit — upload the missing large files, the branches and
tags GitHub doesn't have yet (after the same password and size checks Sync makes), or the changes with Sync — and then
checks again. It never deletes anything itself: move the folder to the Recycle Bin, so you can still get it back.

## Requirements

- Windows 10 or 11
- [Git for Windows](https://git-scm.com/download/win) — it includes Git Credential Manager, which opens the GitHub
  sign-in window the first time you push. If it's missing, GitSync offers to install it with Windows' own installer
  (winget).

## Building and testing

```bash
dotnet build
dotnet test
```

Every build also publishes a self-contained single-file `GitSync/bin/SingleFile/GitSync.exe` that runs without .NET
installed. The integration tests run real git against temporary repositories, so git must be on `PATH`. GitHub runs
them too, on every pull request and every change to `master` (`.github/workflows/tests.yml`).

## Publishing a new version

On GitHub: **Releases > Draft a new release**, create a tag like `v1.1.0`, and **Publish release**. GitHub then builds
`GitSync.exe` for that version and attaches it to the release (`.github/workflows/release.yml`). Everyone who runs
GitSync sees one line when it starts: a newer version is available, with the link to download it. Also set
`<Version>` in `GitSync/GitSync.csproj` to the same number, so your own builds don't call themselves out of date.

## Where GitSync keeps its files

`%LOCALAPPDATA%\GitSync\` holds `gitsync_log.txt` (the log) and `recent_folders.txt`. Set the `GITSYNC_DATA_DIR`
environment variable to keep them somewhere else.

## When something goes wrong

GitSync shows git's own message plus what it means. The most common ones:

| Git says | What to do |
| --- | --- |
| `Authentication failed` / `Invalid username or password` | GitHub no longer accepts your account password for Git. Sign in through the browser window, or use a personal access token. GitSync can forget a wrong saved login for you. |
| `Repository not found` | Check the URL for typos, and make sure you're signed in with an account that can see it (private repositories look "not found" to other accounts). A project that has no GitHub address at all gets its repository created by Sync, and so does your own repository that was deleted on GitHub. |
| `pack exceeds maximum allowed size` | One push is over GitHub's 2 GB limit. Keep build output and downloaded packages out with `.gitignore`, or store big assets with Git LFS, then Sync again. |
| `[rejected] ... (fetch first)` | Someone pushed in the meantime. GitSync offers to merge their commits and try again. |
| `Permission denied (publickey)` / `Host key verification failed` | The SSH address needs an SSH key set up with GitHub. GitSync offers to switch to the HTTPS address, which signs in through the browser instead. |
| `detected dubious ownership` | The folder belongs to another Windows user (common on USB drives). GitSync offers to mark it as safe. |
| `exceeds GitHub's file size limit` | A file over 100 MB is in your commits. Track it with Git LFS (GitSync offers this before committing) or remove it. |
