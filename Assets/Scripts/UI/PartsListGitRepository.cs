using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Protobot.UI {
    // No Unity APIs here: Git and file I/O run off the editor/game thread.
    public sealed class PartsListGitRepository {
        public const string SnapshotFile = "parts-list.txt";
        public string Root { get; private set; }
        public string Branch { get; private set; }
        public string Remote { get; private set; }

        public async Task Connect(string folder) {
            Root = Path.GetFullPath(folder);
            var top = (await Git("rev-parse", "--show-toplevel")).Trim();
            Root = Path.GetFullPath(top);
            if ((await Git("rev-parse", "--is-bare-repository")).Trim() != "false")
                throw new InvalidOperationException("Choose a normal working repository, not a bare repository.");
            await RefreshConnection();
        }

        public static async Task<PartsListGitRepository> Clone(string url, string parentFolder) {
            // Only GitHub HTTPS URLs; credentials stay in Git's credential manager.
            var match = Regex.Match(url.Trim(), @"^https://github\.com/([A-Za-z0-9_-]+)/([A-Za-z0-9_.-]+?)(?:\.git)?/?$");
            if (!match.Success)
                throw new InvalidOperationException("Enter a GitHub URL such as https://github.com/owner/repository (no token in the URL).");
            var name = match.Groups[2].Value;
            if (name == "." || name == "..") throw new InvalidOperationException("Invalid repository name.");
            var target = Path.Combine(Path.GetFullPath(parentFolder), name);
            if (Directory.Exists(target) || File.Exists(target))
                throw new InvalidOperationException("That folder already exists. Select it with Choose Local Repository, or choose a different clone location.");
            await Run(parentFolder, "clone", "--", url.Trim(), target);
            var repo = new PartsListGitRepository();
            await repo.Connect(target);
            return repo;
        }

        private async Task RefreshConnection() {
            var branch = (await Git("branch", "--show-current")).Trim();
            var remote = (await Git("remote", "get-url", "--push", "--all", "origin")).Trim();
            // Accept one explicit GitHub destination, over HTTPS or SSH.
            if (remote.Contains("\n"))
                throw new InvalidOperationException("Choose a repository with one origin push URL so the destination is unambiguous.");
            if (!Regex.IsMatch(remote, @"^(https://github\.com/|git@github\.com:|ssh://git@github\.com/)[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/?$"))
                throw new InvalidOperationException("The repository needs an origin remote pointing to GitHub (HTTPS or SSH).");
            if (string.IsNullOrWhiteSpace(branch))
                throw new InvalidOperationException("Check out a branch before committing.");
            Branch = branch;
            Remote = remote;
        }

        public async Task<string> CommitAndPush(string snapshot, string message, Action<string> progress) {
            if (string.IsNullOrWhiteSpace(message))
                throw new InvalidOperationException("Enter a commit message.");
            var expectedBranch = Branch;
            var expectedRemote = Remote;
            await RefreshConnection();
            if (Branch != expectedBranch || Remote != expectedRemote) {
                Branch = expectedBranch;
                Remote = expectedRemote;
                throw new InvalidOperationException("The branch or GitHub remote changed. Choose Local Repository again to review the new destination.");
            }
            // Refuse in-progress operations before writing the snapshot.
            foreach (var marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply" }) {
                var gitPath = (await Git("rev-parse", "--git-path", marker)).Trim();
                var path = Path.IsPathRooted(gitPath) ? gitPath : Path.Combine(Root, gitPath);
                if (File.Exists(path) || Directory.Exists(path))
                    throw new InvalidOperationException("Finish the repository's merge, rebase, or cherry-pick before committing a parts list.");
            }
            if (!string.IsNullOrWhiteSpace(await Git("diff", "--cached", "--name-only")))
                throw new InvalidOperationException("This repository has staged changes. Commit or unstage them in Git first, then try again.");

            var file = Path.Combine(Root, SnapshotFile);
            if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("parts-list.txt must be a regular file, not a symbolic link.");
            progress("Saving the current parts list...");
            File.WriteAllText(file, snapshot.Replace("\r\n", "\n"), new UTF8Encoding(false));
            await Git("add", "--", SnapshotFile);
            string commit;
            if (string.IsNullOrWhiteSpace(await Git("diff", "--cached", "--name-only", "--", SnapshotFile))) {
                commit = "Parts list unchanged; no new commit created.";
            }
            else {
                progress("Committing parts-list.txt...");
                // An explicit path limits this commit even if another tool stages a file.
                await Git("commit", "--only", "-m", message.Trim(), "--", SnapshotFile);
                commit = "Committed parts-list.txt (" + (await Git("rev-parse", "--short", "HEAD")).Trim() + ").";
            }
            progress(commit + " Pushing to GitHub...");
            try {
                // Normal push: never force, pull, reset, or rewrite history.
                await Git("push", "--set-upstream", "origin", "HEAD:refs/heads/" + Branch);
            }
            catch (Exception e) {
                throw new InvalidOperationException(commit + " Push failed; local commits are preserved. " +
                    "Fix GitHub access or synchronize the branch in Git, then click Commit & Push again.\n" + e.Message, e);
            }
            return commit + " Pushed to origin/" + Branch + ".";
        }

        private Task<string> Git(params string[] args) => Run(Root, args);

        private static async Task<string> Run(string folder, params string[] args) {
            var start = new ProcessStartInfo {
                FileName = "git", WorkingDirectory = folder,
                Arguments = string.Join(" ", Array.ConvertAll(args, Quote)),
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true
            };
            // GCM may open a browser for GitHub sign-in, but never wait for terminal input.
            start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            start.EnvironmentVariables["GIT_LITERAL_PATHSPECS"] = "1";
            using (var process = new Process { StartInfo = start }) {
                try { process.Start(); }
                catch (System.ComponentModel.Win32Exception e) {
                    throw new InvalidOperationException("Git could not start. Install Git, then restart Unity or Protobot so Git is available on PATH.", e);
                }
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                var completion = Task.Run(() => process.WaitForExit());
                if (await Task.WhenAny(completion, Task.Delay(TimeSpan.FromMinutes(5))) != completion) {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    throw new TimeoutException("Git timed out. Check GitHub sign-in and your connection. Any local commit is preserved; check git status before retrying.");
                }
                await completion;
                var stdout = await output;
                var stderr = await error;
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("Git " + args[0] + " failed:\n" + stderr.Trim() + "\n" + stdout.Trim());
                return stdout;
            }
        }

        // Windows command-line escaping, including embedded quotes and trailing slashes.
        private static string Quote(string value) {
            if (value.IndexOf('\0') >= 0) throw new ArgumentException("Invalid Git argument.");
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (var character in value) {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                result.Append(character);
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            return result.Append('"').ToString();
        }
    }
}
