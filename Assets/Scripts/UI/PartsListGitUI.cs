using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using Protobot.Builds;
using SFB;
using UnityEngine;
using UnityEngine.UI;

namespace Protobot.UI {
    public class PartsListGitUI : MonoBehaviour {
        [SerializeField] private PartListOutput exporter;
        private const string FolderKey = "PartsListGit.RepositoryFolder";
        private GameObject window;
        private InputField url;
        private InputField message;
        private Text connection;
        private Text status;
        private Button commitButton;
        private PartsListGitRepository repository;
        private bool busy;
        private volatile string progress;
        private Font font;
        private InputField branchField;
        private Text branches;
        private GameObject editAnyway;
        private TaskCompletionSource<bool> openDecision;
        private BuildsManager builds;
        private DesignUpdateResult designCheck;
        private string pendingOpenPath;
        private InputField recoveryBranch;
        private Text recoveryInfo;
        private readonly Dictionary<string, string> startingRevisions = new Dictionary<string, string>(StringComparer.Ordinal);

        private string BaselineKey(string path) => repository.Root + "\n" + repository.Branch + "\n" + Path.GetFullPath(path);

        private async Task<string> StartingRevision(string path, bool reset = false) {
            var key = BaselineKey(path);
            if (reset || !startingRevisions.ContainsKey(key))
                startingRevisions[key] = await Task.Run(() => repository.HeadRevision());
            return startingRevisions[key];
        }

        private void Awake() {
            builds = FindObjectOfType<BuildsManager>();
            BuildsManager.ConfirmRepositoryOpen = ConfirmOpen;
        }

        public void Open() {
            if (window == null) CreateWindow();
            window.SetActive(true);
            UpdateCommitButton();
        }

        private void Update() {
            if (status != null && progress != null) status.text = progress;
        }

        private void OnDestroy() {
            if (BuildsManager.ConfirmRepositoryOpen == ConfirmOpen) BuildsManager.ConfirmRepositoryOpen = null;
            openDecision?.TrySetResult(false);
            if (window != null) Destroy(window);
        }

        private void CreateWindow(bool restoreConnection = true) {
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            window = new GameObject("Parts List GitHub", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = window.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            var scaler = window.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1000, 1020);
            scaler.matchWidthOrHeight = 1f;

            var shade = Box("Modal Background", window.transform, new Color(0, 0, 0, 0.7f));
            Stretch(shade.rectTransform);
            var panel = Box("Panel", shade.transform, new Color(0.12f, 0.13f, 0.15f));
            var rect = panel.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(760, 950);

            Label(panel.transform, "Commit & Push Design", 24, 18, 640, 30, 22);
            MakeButton(panel.transform, "Close", 650, 18, 86, () => {
                if (!busy) { openDecision?.TrySetResult(false); window.SetActive(false); }
            });
            Label(panel.transform, "Choose a GitHub repository", 24, 62, 700, 24, 17);
            url = Field(panel.transform, "https://github.com/owner/repository", 24, 94, 550);
            MakeButton(panel.transform, "Clone URL...", 586, 94, 150, () => { if (!busy) Clone(); });
            MakeButton(panel.transform, "Choose Local Repository...", 24, 138, 250, () => { if (!busy) ChooseLocal(); });
            Label(panel.transform, "Git must be installed. A browser may open for GitHub sign-in.", 24, 180, 710, 26, 14);
            connection = Label(panel.transform, "No repository connected.", 24, 214, 710, 76, 14);
            branchField = Field(panel.transform, "Existing or new branch name", 24, 294, 390);
            branchField.onValueChanged.AddListener(_ => {
                UpdateCommitButton();
                if (!busy && repository != null && branchField.text.Trim() != repository.Branch)
                    progress = "Branch name entered but not activated. Click Create Branch or Switch Branch first. Active branch: " + repository.Branch + ".";
            });
            MakeButton(panel.transform, "Switch Branch", 424, 294, 150, () => { if (!busy) ChangeBranch(false); });
            MakeButton(panel.transform, "Create Branch", 584, 294, 152, () => { if (!busy) ChangeBranch(true); });
            branches = Label(panel.transform, "Branches appear after Check Updates.", 24, 336, 710, 44, 14);
            Label(panel.transform, "Commit message", 24, 384, 700, 24, 16);
            message = Field(panel.transform, "Describe your design changes", 24, 414, 712);
            message.text = "Update parts list";
            Label(panel.transform,
                "Commit includes the saved .pbb and parts-list.txt. Save the design inside this repository first.\n" +
                "Push sends all pending commits on the shown branch.",
                24, 460, 710, 48, 14);
            commitButton = MakeButton(panel.transform, "Commit & Push", 24, 518, 200, () => { if (!busy) CommitAndPush(); });
            commitButton.interactable = false;
            MakeButton(panel.transform, "Check Updates", 236, 518, 200, () => { if (!busy) CheckUpdates(); });
            MakeButton(panel.transform, "Update & Reload", 448, 518, 200, () => { if (!busy) UpdateBranch(); });
            status = Label(panel.transform, "Choose a local clone, or paste a URL and choose where to clone it.", 24, 566, 710, 116, 14);
            Label(panel.transform, "Editing an older copy may conflict with teammates' changes. Save a separate copy if needed; binary .pbb designs cannot be merged automatically.", 24, 684, 710, 42, 14);
            editAnyway = MakeButton(panel.transform, "Edit Anyway", 24, 730, 200, () => {
                openDecision?.TrySetResult(true);
                window.SetActive(false);
            }).gameObject;
            editAnyway.SetActive(false);
            recoveryInfo = Label(panel.transform, "Keep your local version, or back it up before replacing it with GitHub's version.", 24, 772, 710, 42, 14);
            MakeButton(panel.transform, "Keep Editing My Version", 236, 730, 250, () => {
                if (busy) return;
                openDecision?.TrySetResult(true);
                window.SetActive(false);
            });
            MakeButton(panel.transform, "Back Up My Version & Update", 24, 820, 310, () => { if (!busy) BackupAndUpdate(); });
            recoveryBranch = Field(panel.transform, "New branch for my version", 24, 866, 350);
            recoveryBranch.text = "my-version-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            MakeButton(panel.transform, "Save My Version to New Branch", 388, 866, 348, () => { if (!busy) SaveVersionToBranch(); });

            // Paths are local preferences; no GitHub credentials are stored by this UI.
            var saved = PlayerPrefs.GetString(FolderKey, "");
            if (restoreConnection && !string.IsNullOrEmpty(saved)) Connect(saved);
        }

        private void ChooseLocal() {
            var paths = StandaloneFileBrowser.OpenFolderPanel("Choose a local GitHub repository", "", false);
            if (paths.Length > 0 && !string.IsNullOrEmpty(paths[0])) Connect(paths[0]);
        }

        private async void Connect(string path) {
            await Execute(async () => {
                progress = "Connecting to repository...";
                var selected = new PartsListGitRepository();
                await Task.Run(() => selected.Connect(path));
                SetRepository(selected);
                await RefreshStatus();
            });
        }

        private async void Clone() {
            if (string.IsNullOrWhiteSpace(url.text)) { progress = "Paste a GitHub repository URL first."; return; }
            var paths = StandaloneFileBrowser.OpenFolderPanel("Choose a parent folder for the new clone", "", false);
            if (paths.Length == 0 || string.IsNullOrEmpty(paths[0])) return;
            var selectedUrl = url.text;
            await Execute(async () => {
                progress = "Cloning... Complete GitHub sign-in in your browser if prompted.";
                var selected = await Task.Run(() => PartsListGitRepository.Clone(selectedUrl, paths[0]));
                SetRepository(selected);
                await RefreshStatus();
            });
        }

        private void SetRepository(PartsListGitRepository selected) {
            repository = selected;
            connection.text = "Folder: " + selected.Root + "\nGitHub: " + selected.Remote + "\nBranch: " + selected.Branch;
            PlayerPrefs.SetString(FolderKey, selected.Root);
            PlayerPrefs.Save();
            branchField.text = selected.Branch;
            UpdateCommitButton();
        }

        private async Task RefreshStatus(string openingPath = null) {
            progress = "Checking GitHub for updates...";
            designCheck = null;
            try {
                var path = openingPath ?? builds?.buildPath;
                bool inside = !string.IsNullOrEmpty(path) && Path.GetFullPath(path).StartsWith(
                    repository.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                if (inside) {
                    // Read the requested file before opening; otherwise inspect the live scene,
                    // so unsaved part edits are included without saving anything to disk.
                    var fingerprint = openingPath != null
                        ? DesignComparison.Fingerprint(File.ReadAllBytes(path))
                        : DesignComparison.Fingerprint(SceneBuild.ToBuildData());
                    var starting = await StartingRevision(path, openingPath != null);
                    designCheck = await Task.Run(() => repository.CheckDesign(path, fingerprint, starting));
                    progress = designCheck.Message;
                    if (designCheck.State == DesignUpdateState.RemoteOnly)
                        recoveryInfo.text = "Your robot has no competing edits. Back Up & Update also preserves any local camera or save-only changes.";
                }
                else progress = await Task.Run(() => repository.CheckUpdates());
            }
            catch (Exception e) { progress = "Update status unknown. You may edit, but check your connection before pushing.\n" + e.Message; }
            var names = await Task.Run(() => repository.GetBranches());
            branches.text = "Available branches: " + string.Join(", ", names);
            connection.text = "Folder: " + repository.Root + "\nGitHub: " + repository.Remote + "\nBranch: " + repository.Branch;
        }

        private async void CheckUpdates() {
            await Execute(async () => {
                if (repository == null) throw new InvalidOperationException("Connect a repository first.");
                await RefreshStatus();
            });
        }

        private void RequireSavedScene() {
            if (builds != null && builds.HasUnsavedChanges())
                throw new InvalidOperationException("Save your current design first, or use Save As to keep a separate copy. Unsaved edits were not replaced.");
        }

        private async void ChangeBranch(bool create) {
            await Execute(async () => {
                if (repository == null) throw new InvalidOperationException("Connect a repository first.");
                if (openDecision != null) throw new InvalidOperationException("Close the pending file-open warning before switching branches.");
                RequireSavedScene();
                var name = branchField.text;
                await Task.Run(() => repository.SwitchBranch(name, create));
                SetRepository(repository);
                builds?.ReloadAfterRepositoryChange(repository.Root);
                if (!string.IsNullOrEmpty(builds?.buildPath)) await StartingRevision(builds.buildPath, true);
                await RefreshStatus();
            });
        }

        private void UpdateBranch() {
            if (busy) return;
            recoveryInfo.text = "To update, use Back Up My Version & Update below. Your saved file and unsaved editor work will be kept outside the repository first.";
            progress = "Choose Back Up My Version & Update, Keep Editing My Version, or save to a new branch.";
        }

        private async void BackupAndUpdate() {
            var path = pendingOpenPath ?? builds?.buildPath;
            if (repository == null || string.IsNullOrEmpty(path)) { progress = "Connect a repository and open a saved .pbb first."; return; }
            var folders = StandaloneFileBrowser.OpenFolderPanel("Choose a backup folder outside the repository", "", false);
            if (folders.Length == 0 || string.IsNullOrEmpty(folders[0])) return;
            await Execute(async () => {
                var targetBranch = branchField.text.Trim();
                if (targetBranch != repository.Branch)
                    throw new InvalidOperationException("Activate the entered branch before updating.");
                var livePath = builds?.buildPath;
                var live = builds == null ? null : BuildsManager.SerializeSnapshot(SceneBuild.ToBuildData());
                var backup = await Task.Run(() => repository.BackupAndUpdate(path, live, livePath, folders[0], value => progress = value));
                progress = "Updated successfully. Your previous versions are backed up at:\n" + backup;
                recoveryInfo.text = "Backup: " + backup;
                if (openDecision == null) {
                    builds?.ReloadAfterRepositoryChange(repository.Root);
                    await StartingRevision(path, true);
                }
                else {
                    builds?.ConfirmOpenSceneWasBackedUp();
                    openDecision.TrySetResult(true);
                    // Keep the backup path visible after opening the updated file.
                }
                Debug.Log("[Parts List Git] " + progress, this);
            });
        }

        private async void SaveVersionToBranch() {
            if (openDecision != null) {
                openDecision.TrySetResult(true);
                progress = "Opening your local copy first. Resolve any existing unsaved-work prompt, then click Save My Version to New Branch again.";
                return;
            }
            await Execute(async () => {
                if (repository == null || builds == null || string.IsNullOrEmpty(builds.buildPath))
                    throw new InvalidOperationException("Open a .pbb inside this repository first.");
                var path = builds.buildPath;
                repository.DesignRelativePath(path);
                var name = recoveryBranch.text.Trim();
                if (string.IsNullOrEmpty(name) || name == repository.Branch)
                    throw new InvalidOperationException("Enter a new branch name for your version.");
                var captured = SceneBuild.ToBuildData();
                var bytes = BuildsManager.SerializeSnapshot(captured);
                var list = exporter.GetPartsListText();
                var commitMessage = string.IsNullOrWhiteSpace(message.text) ? "Preserve my robot design" : message.text;
                await Task.Run(() => repository.SwitchBranch(name, true));
                SetRepository(repository); // Show the new destination even if pushing fails.
                var result = await Task.Run(() => repository.CommitAndPush(list, commitMessage, value => progress = value, path, bytes, name));
                builds.MarkSnapshotSaved(path, captured);
                await StartingRevision(path, true);
                progress = result;
                Debug.Log("[Parts List Git] " + result, this);
            });
        }

        private async Task<bool> ConfirmOpen(string path) {
            // An ordinary local design outside Git keeps the normal open workflow.
            var folder = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path)));
            while (folder != null && !Directory.Exists(Path.Combine(folder.FullName, ".git")) && !File.Exists(Path.Combine(folder.FullName, ".git"))) folder = folder.Parent;
            if (folder == null) return true;
            if (busy || openDecision != null) return false;
            if (window == null) {
                CreateWindow(false);
            }
            window.SetActive(true);
            var tracked = false;
            await Execute(async () => {
                var selected = new PartsListGitRepository();
                await Task.Run(() => selected.Connect(folder.FullName));
                // Also check new local files: a teammate may have added the same path remotely.
                selected.DesignRelativePath(path);
                tracked = true;
                SetRepository(selected);
                await RefreshStatus(path);
            });
            if (!tracked) { window.SetActive(false); return true; }
            if (designCheck != null && !designCheck.Warn) {
                window.SetActive(false);
                return true;
            }
            openDecision = new TaskCompletionSource<bool>();
            pendingOpenPath = path;
            editAnyway.SetActive(true);
            commitButton.interactable = false;
            try { return await openDecision.Task; }
            finally { openDecision = null; pendingOpenPath = null; if (editAnyway != null) editAnyway.SetActive(false); }
        }

        private async void CommitAndPush() {
            await Execute(async () => {
                if (repository == null) throw new InvalidOperationException("Connect a repository first.");
                var targetBranch = branchField.text.Trim();
                if (targetBranch != repository.Branch)
                    throw new InvalidOperationException("Click Create Branch or Switch Branch to activate '" + targetBranch + "' before pushing.");
                if (string.IsNullOrWhiteSpace(message.text)) throw new InvalidOperationException("Enter a commit message.");
                if (openDecision != null) throw new InvalidOperationException("Finish opening or cancel the design first.");
                if (exporter == null) throw new InvalidOperationException("The parts-list exporter is missing from this button.");
                if (builds == null || string.IsNullOrEmpty(builds.buildPath))
                    throw new InvalidOperationException("Save your .pbb design inside the selected repository first.");
                repository.DesignRelativePath(builds.buildPath);
                var designPath = builds.buildPath;
                var capturedDesign = SceneBuild.ToBuildData();
                var designBytes = BuildsManager.SerializeSnapshot(capturedDesign);
                // Capture Unity scene state on the main thread before doing Git work.
                var snapshot = exporter.GetPartsListText();
                var commitMessage = message.text;
                var starting = await StartingRevision(designPath);
                progress = "Preparing parts-list.txt...";
                var result = await Task.Run(() => repository.CommitAndPush(snapshot, commitMessage, value => progress = value, designPath, designBytes, targetBranch, starting));
                progress = result;
                if (builds != null) builds.MarkSnapshotSaved(designPath, capturedDesign);
                await StartingRevision(designPath, true);
                if (this != null) Debug.Log("[Parts List Git] " + result, this);
            });
        }

        private async Task Execute(Func<Task> action) {
            if (busy) return;
            busy = true;
            commitButton.interactable = false;
            try { await action(); }
            catch (Exception e) {
                progress = e.Message;
                if (this != null) Debug.LogError("[Parts List Git] " + e.Message, this);
            }
            finally {
                busy = false;
                UpdateCommitButton();
            }
        }

        private void UpdateCommitButton() {
            if (commitButton == null) return;
            commitButton.interactable = !busy && repository != null && openDecision == null && branchField.text.Trim() == repository.Branch;
            var caption = commitButton.GetComponentInChildren<Text>();
            if (caption != null) caption.text = repository == null ? "Commit & Push" : "Push to " + repository.Branch;
        }

        private Image Box(string name, Transform parent, Color color) {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.color = color;
            return image;
        }

        private void Position(RectTransform rect, float x, float y, float width, float height) {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        private static void Stretch(RectTransform rect) {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        private Text Label(Transform parent, string value, float x, float y, float width, float height, int size) {
            var go = new GameObject("Label", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.color = Color.white;
            text.text = value;
            text.raycastTarget = false;
            text.supportRichText = false;
            Position(text.rectTransform, x, y, width, height);
            return text;
        }

        private InputField Field(Transform parent, string hint, float x, float y, float width) {
            var background = Box("Input", parent, new Color(0.22f, 0.23f, 0.26f));
            Position(background.rectTransform, x, y, width, 34);
            var input = background.gameObject.AddComponent<InputField>();
            input.targetGraphic = background;
            var text = Label(background.transform, "", 8, 6, width - 16, 24, 16);
            input.textComponent = text;
            var placeholder = Label(background.transform, hint, 8, 6, width - 16, 24, 16);
            placeholder.color = Color.gray;
            input.placeholder = placeholder;
            return input;
        }

        private Button MakeButton(Transform parent, string caption, float x, float y, float width, UnityEngine.Events.UnityAction clicked) {
            var image = Box(caption, parent, new Color(0.08f, 0.34f, 0.56f));
            Position(image.rectTransform, x, y, width, 34);
            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(clicked);
            var text = Label(image.transform, caption, 4, 6, width - 8, 24, 15);
            text.alignment = TextAnchor.MiddleCenter;
            return button;
        }
    }
}
