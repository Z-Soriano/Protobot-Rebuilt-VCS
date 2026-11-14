using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        private async Task RefreshConnection(bool acceptBranchChange = false) {
            var branch = (await Git("branch", "--show-current")).Trim();
            var remote = (await Git("remote", "get-url", "--push", "--all", "origin")).Trim();
            // Accept one explicit GitHub destination, over HTTPS or SSH.
            if (remote.Contains("\n"))
                throw new InvalidOperationException("Choose a repository with one origin push URL so the destination is unambiguous.");
            if (!Regex.IsMatch(remote, @"^(https://github\.com/|git@github\.com:|ssh://git@github\.com/)[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/?$"))
                throw new InvalidOperationException("The repository needs an origin remote pointing to GitHub (HTTPS or SSH).");
            if (string.IsNullOrWhiteSpace(branch))
                throw new InvalidOperationException("Check out a branch before committing.");
            if (!acceptBranchChange && Branch != null && (branch != Branch || remote != Remote))
                throw new InvalidOperationException("The checked-out branch or GitHub destination changed outside this dialog. Reconnect and explicitly select the branch again. Nothing was pushed.");
            Branch = branch;
            Remote = remote;
        }

        public async Task Fetch() {
            await Git("fetch", "--prune", "origin", "+refs/heads/*:refs/remotes/origin/*");
        }

        public async Task<string[]> GetBranches() {
            var names = await Git("for-each-ref", "--format=%(refname:short)", "refs/heads/", "refs/remotes/origin/");
            return names.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(n => n != "origin/HEAD")
                .Select(n => n.StartsWith("origin/", StringComparison.Ordinal) ? n.Substring(7) : n)
                .Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }

        public async Task SwitchBranch(string name, bool create) {
            name = name.Trim();
            await Git("check-ref-format", "--branch", name);
            if (create) await Git("switch", "-c", name);
            else {
                await RequireClean();
                var locals = (await Git("for-each-ref", "--format=%(refname:short)", "refs/heads/")).Split('\n');
                if (locals.Any(n => n.Trim() == name)) await Git("switch", name);
                else await Git("switch", "--track", "-c", name, "refs/remotes/origin/" + name);
            }
            await RefreshConnection(true);
        }

        private async Task RequireClean() {
            if (!string.IsNullOrWhiteSpace(await Git("status", "--porcelain", "--untracked-files=all")))
                throw new InvalidOperationException("Save and commit your work or move untracked files outside this repository before switching branches or updating. Nothing was replaced.");
        }

        public async Task<string> CheckUpdates() {
            await Fetch();
            await RefreshConnection();
            var remoteRef = "refs/remotes/origin/" + Branch;
            var exists = await Git("for-each-ref", "--format=%(refname)", remoteRef);
            if (!exists.Split('\n').Any(line => line.Trim() == remoteRef))
                return "Branch '" + Branch + "' has not been published yet. Commit & Push will create origin/" + Branch + ".";
            var counts = (await Git("rev-list", "--left-right", "--count", "HEAD..." + remoteRef)).Trim()
                .Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var ahead = int.Parse(counts[0]);
            var behind = int.Parse(counts[1]);
            if (behind == 0) return ahead == 0 ? "Up to date with GitHub." : "Your branch has local commits to push.";
            return ahead == 0
                ? "GitHub has " + behind + " newer commit(s). Update before editing, or choose Edit Anyway."
                : "Both your branch and GitHub have new commits. They have diverged; synchronize manually or save a separate copy.";
        }

        public async Task UpdateBranch() {
            await Fetch();
            await RefreshConnection();
            // Git itself refuses overwrites; unrelated local files need not block an update.
            await Git("merge", "--ff-only", "refs/remotes/origin/" + Branch);
        }

        public async Task<string> BackupAndUpdate(string path, byte[] liveDesign, string livePath, string backupParent,
                                                  Action<string> progress) {
            var relative = DesignRelativePath(path);
            var partsPath = Path.Combine(Root, SnapshotFile);
            if (File.Exists(partsPath) && (File.GetAttributes(partsPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("parts-list.txt must be a regular file to back up and update.");
            var parent = Path.GetFullPath(backupParent).TrimEnd(Path.DirectorySeparatorChar);
            var root = Root.TrimEnd(Path.DirectorySeparatorChar);
            if (parent.Equals(root, StringComparison.OrdinalIgnoreCase) || parent.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose a backup folder outside this Git repository.");
            await Fetch();
            await RefreshConnection();
            var remote = (await Git("rev-parse", "refs/remotes/origin/" + Branch)).Trim();
            var head = (await Git("rev-parse", "HEAD")).Trim();
            if ((await Git("merge-base", head, remote)).Trim() != head)
                throw new InvalidOperationException("Your branch has commits that GitHub does not include. Use Save My Version to New Branch first; this update will not remove local commits.");
            // Preserve staged work rather than silently changing the user's index.
            if (!string.IsNullOrWhiteSpace(await Git("diff", "--cached", "--name-only")))
                throw new InvalidOperationException("There are staged changes. Use Save My Version to New Branch to preserve your design; an update cannot replace staged work.");
            var backup = Path.Combine(parent, "Protobot-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(backup);
            progress("Backing up local work to " + backup);
            var paths = new[] { relative, SnapshotFile }.Distinct().ToArray();
            var disk = paths.ToDictionary(name => name, name => File.Exists(Path.Combine(Root, name)) ? File.ReadAllBytes(Path.Combine(Root, name)) : null);
            foreach (var item in disk) {
                if (item.Value != null) VerifiedBackup(Path.Combine(backup, item.Key == relative ? "design-on-disk.pbb" : SnapshotFile), item.Value);
            }
            if (liveDesign != null) VerifiedBackup(Path.Combine(backup, "current-editor-design.pbb"), liveDesign);
            VerifiedBackup(Path.Combine(backup, "backup-info.txt"), Encoding.UTF8.GetBytes(
                "Repository: " + Root + "\nBranch: " + Branch + "\nStarting commit: " + head +
                "\nUpdated commit: " + remote + "\nFile on disk: " + path + "\nEditor design: " + livePath +
                "\nAbsent local files: " + string.Join(", ", disk.Where(item => item.Value == null).Select(item => item.Key)) + "\n"));
            var replaced = new Dictionary<string, byte[]>();
            try {
                await RefreshConnection();
                if ((await Git("rev-parse", "HEAD")).Trim() != head) throw new InvalidOperationException("The branch changed during backup. Retry the update.");
                // Do not overwrite a file edited by another tool while the backup was being written.
                foreach (var item in disk) {
                    var full = Path.Combine(Root, item.Key);
                    var now = File.Exists(full) ? File.ReadAllBytes(full) : null;
                    if (!(now == null ? item.Value == null : item.Value != null && now.SequenceEqual(item.Value)))
                        throw new InvalidOperationException("A local file changed during backup. Retry the update.");
                }
                // Only the two backed-up application files are restored; never reset the repository.
                foreach (var name in paths) {
                    if (!string.IsNullOrWhiteSpace(await Git("ls-tree", "HEAD", "--", name)))
                        await Git("restore", "--source=HEAD", "--worktree", "--", name);
                    else if (File.Exists(Path.Combine(Root, name)))
                        File.Move(Path.Combine(Root, name), Path.Combine(backup, name == relative ? "untracked-design-original.pbb" : "untracked-parts-list-original.txt"));
                    replaced[name] = File.Exists(Path.Combine(Root, name)) ? File.ReadAllBytes(Path.Combine(Root, name)) : null;
                }
                progress("Backup saved. Updating the branch...");
                await Git("merge", "--ff-only", remote);
            }
            catch (Exception e) {
                // If the merge did not move HEAD, put the original working files back as well.
                if ((await Git("rev-parse", "HEAD")).Trim() == head)
                    foreach (var item in replaced) {
                        var full = Path.Combine(Root, item.Key);
                        var now = File.Exists(full) ? File.ReadAllBytes(full) : null;
                        var unchanged = now == null ? item.Value == null : item.Value != null && now.SequenceEqual(item.Value);
                        if (unchanged && disk[item.Key] != null) File.WriteAllBytes(full, disk[item.Key]);
                    }
                throw new InvalidOperationException("Update stopped. Your backup is at " + backup + ".\n" + e.Message, e);
            }
            return backup;
        }

        private static void VerifiedBackup(string path, byte[] bytes) {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(bytes, 0, bytes.Length);
            if (!File.ReadAllBytes(path).SequenceEqual(bytes)) throw new IOException("Could not verify backup: " + path);
        }

        public async Task<string> HeadRevision() {
            var refs = await Git("for-each-ref", "--format=%(objectname)", "refs/heads/" + Branch);
            return string.IsNullOrWhiteSpace(refs) ? null : (await Git("rev-parse", "HEAD")).Trim();
        }

        public async Task<DesignUpdateResult> CheckDesign(string path, string localFingerprint, string startingRevision = null) {
            try {
                var relative = DesignRelativePath(path);
                await Fetch();
                await RefreshConnection();
                var remoteRef = "refs/remotes/origin/" + Branch;
                var refs = (await Git("for-each-ref", "--format=%(refname)", remoteRef)).Split('\n');
                if (!refs.Any(line => line.Trim() == remoteRef))
                    return DesignComparison.Result(DesignUpdateState.NewBranch, "Branch '" + Branch + "' has not been published yet. Your first push will publish this robot there.");
                // Freeze the remote revision; later network updates cannot mix comparison versions.
                var remoteCommit = (await Git("rev-parse", remoteRef)).Trim();
                var baselineCommit = (await Git("merge-base", startingRevision ?? "HEAD", remoteCommit)).Trim();
                var baseline = DesignComparison.Fingerprint(await ReadDesignAt(baselineCommit, relative));
                var remote = DesignComparison.Fingerprint(await ReadDesignAt(remoteCommit, relative));
                return DesignComparison.Compare(baseline, localFingerprint, remote);
            }
            catch (Exception e) {
                return DesignComparison.Result(DesignUpdateState.Unknown, "Couldn't check this robot for updates. Editing is allowed; pushing requires a successful check.\n" + e.Message);
            }
        }

        private async Task<byte[]> ReadDesignAt(string revision, string relative) {
            if (string.IsNullOrWhiteSpace(await Git("ls-tree", "-z", revision, "--", relative))) return null;
            return await RunBytes(Root, "show", revision + ":" + relative);
        }

        public async Task<bool> TracksFile(string path) {
            var full = Path.GetFullPath(path);
            var prefix = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            var relative = full.Substring(prefix.Length).Replace('\\', '/');
            return !string.IsNullOrWhiteSpace(await Git("ls-files", "--", relative));
        }

        public string DesignRelativePath(string path) {
            var full = Path.GetFullPath(path);
            var prefix = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(full), ".pbb", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Save the current .pbb design inside the selected repository using File > Save As, then retry.");
            for (var item = full; item.Length > Root.Length; item = Path.GetDirectoryName(item)) {
                if (Path.GetFileName(item).Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                    ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0))
                    throw new InvalidOperationException("Choose a regular .pbb file inside the repository, outside .git and symbolic links.");
            }
            return full.Substring(prefix.Length).Replace('\\', '/');
        }

        public async Task<string> CommitAndPush(string snapshot, string message, Action<string> progress,
                                               string designPath = null, byte[] designSnapshot = null, string targetBranch = null,
                                               string startingRevision = null) {
            if (string.IsNullOrWhiteSpace(message))
                throw new InvalidOperationException("Enter a commit message.");
            var expectedBranch = targetBranch ?? Branch;
            await Git("check-ref-format", "--branch", expectedBranch);
            if (expectedBranch != Branch)
                throw new InvalidOperationException("The entered branch is not active. Click Create Branch or Switch Branch first. Nothing was pushed.");
            var expectedRemote = Remote;
            await RefreshConnection();
            if (Branch != expectedBranch || Remote != expectedRemote) {
                Branch = expectedBranch;
                Remote = expectedRemote;
                throw new InvalidOperationException("The branch or GitHub remote changed. Choose Local Repository again to review the new destination.");
            }
            if (designPath != null) {
                var designCheck = await CheckDesign(designPath, DesignComparison.Fingerprint(designSnapshot), startingRevision);
                if (designCheck.Warn) throw new InvalidOperationException(designCheck.Message + " No files were committed or pushed.");
            }
            var updateStatus = await CheckUpdates();
            if (updateStatus.Contains("newer commit") || updateStatus.Contains("diverged"))
                throw new InvalidOperationException("The design check passed, but the branch history still needs synchronization (possibly changes to other files). Update first, or create a new branch. No commit or push was attempted.");
            // Refuse in-progress operations before writing the snapshot.
            foreach (var marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply" }) {
                var gitPath = (await Git("rev-parse", "--git-path", marker)).Trim();
                var path = Path.IsPathRooted(gitPath) ? gitPath : Path.Combine(Root, gitPath);
                if (File.Exists(path) || Directory.Exists(path))
                    throw new InvalidOperationException("Finish the repository's merge, rebase, or cherry-pick before committing a parts list.");
            }
            var design = designPath == null ? null : DesignRelativePath(designPath);
            if (design != null && designSnapshot == null) throw new ArgumentException("Missing design snapshot.");
            var files = design == null ? new[] { SnapshotFile } : new[] { SnapshotFile, design };
            await RefreshConnection();

            var file = Path.Combine(Root, SnapshotFile);
            if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("parts-list.txt must be a regular file, not a symbolic link.");
            progress("Saving the current parts list...");
            File.WriteAllText(file, snapshot.Replace("\r\n", "\n"), new UTF8Encoding(false));
            if (design != null) File.WriteAllBytes(Path.Combine(Root, design), designSnapshot);
            await Git(new[] { "add", "--" }.Concat(files).ToArray());
            string commit;
            if (string.IsNullOrWhiteSpace(await Git(new[] { "diff", "--cached", "--name-only", "--" }.Concat(files).ToArray()))) {
                commit = "Design and parts list unchanged; no new commit created.";
            }
            else {
                progress("Committing design and parts list...");
                // An explicit path limits this commit even if another tool stages a file.
                await Git(new[] { "commit", "--only", "-m", message.Trim(), "--" }.Concat(files).ToArray());
                commit = "Committed snapshot (" + (await Git("rev-parse", "--short", "HEAD")).Trim() + ").";
            }
            await RefreshConnection();
            // Freeze the source commit and destination before starting the network operation.
            // Never let a later checkout or mutable UI state redirect this push.
            var commitId = (await Git("rev-parse", "refs/heads/" + expectedBranch)).Trim();
            progress(commit + " Pushing " + commitId.Substring(0, 7) + " to origin/" + expectedBranch + "...");
            try {
                // Normal push: never force, pull, reset, or rewrite history.
                await Git("push", "--porcelain", "origin", commitId + ":refs/heads/" + expectedBranch);
            }
            catch (Exception e) {
                throw new InvalidOperationException(commit + " Push failed; local commits are preserved. " +
                    "Fix GitHub access or synchronize the branch in Git, then click Commit & Push again.\n" + e.Message, e);
            }
            return commit + " Pushed " + commitId.Substring(0, 7) + " to origin/" + expectedBranch + ".";
        }

        private Task<string> Git(params string[] args) => Run(Root, args);

        private static async Task<string> Run(string folder, params string[] args) {
            return Encoding.UTF8.GetString(await RunBytes(folder, args));
        }

        private static async Task<byte[]> RunBytes(string folder, params string[] args) {
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
                using (var buffer = new MemoryStream()) {
                var output = process.StandardOutput.BaseStream.CopyToAsync(buffer);
                var error = process.StandardError.ReadToEndAsync();
                var completion = Task.Run(() => process.WaitForExit());
                if (await Task.WhenAny(completion, Task.Delay(TimeSpan.FromMinutes(5))) != completion) {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    throw new TimeoutException("Git timed out. Check GitHub sign-in and your connection. Any local commit is preserved; check git status before retrying.");
                }
                await completion;
                await output;
                var stdout = buffer.ToArray();
                var stderr = await error;
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("Git " + args[0] + " failed:\n" + stderr.Trim());
                return stdout;
                }
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
