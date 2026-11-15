using System;
using System.IO;
using System.Collections.Generic;
using System.Collections;
using System.Threading.Tasks;
using Protobot.Builds;
using SFB;
using UnityEngine;
using UnityEngine.UI;

namespace Protobot.UI {
    public class PartsListGitUI : MonoBehaviour {
        [SerializeField] private PartListOutput exporter;
        [SerializeField] private GameObject preferencesCanvas;
        [SerializeField] private Toggle gitPreferencesCategory;
        private const string FolderKey = "PartsListGit.RepositoryFolder";
        private GameObject window;
        private InputField url;
        private InputField message;
        private Text connection;
        private Text designLocation;
        private string currentGitRoot;
        private float nextLocationCheck;
        private Text status;
        private Button commitButton;
        private PartsListGitRepository repository;
        private bool busy;
        private volatile string progress;
        private Font font;
        private InputField branchField;
        private Text branches;
        private BuildsManager builds;
        private DesignUpdateResult designCheck;
        private GameObject notice;
        private int openGeneration;
        private bool saveChoiceOpen;
        private RectTransform scrollContent;
        private ScrollRect scroll;
        private string[] branchNames = Array.Empty<string>();
        private Coroutine preferencesRoutine;

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
            BuildsManager.RepositoryFileOpened += CheckOpenedDesign;
            BuildsManager.SavedForGit += OfferSavedCommit;
            BuildsManager.IsGitSaveChoicePending = () => saveChoiceOpen;
            GitPreferences.Changed += PreferencesChanged;
            PreferencesChanged();
        }

        public void Open() {
            if (!GitPreferences.Enabled) {
                if (preferencesRoutine != null) StopCoroutine(preferencesRoutine);
                preferencesRoutine = StartCoroutine(OpenGitPreferences());
                return;
            }
            if (window == null) CreateWindow();
            window.SetActive(true);
            RefreshLocation();
            UpdateCommitButton();
        }

        private IEnumerator OpenGitPreferences() {
            // Inactive preference pages are not found by FindObjectOfType. Resolve the
            // scene instance too, so a script reload doesn't depend on newly added refs.
            GitPreferences page = null;
            foreach (var candidate in Resources.FindObjectsOfTypeAll<GitPreferences>()) {
                if (candidate.gameObject.scene == gameObject.scene) { page = candidate; break; }
            }
            if (page != null) {
                if (preferencesCanvas == null) {
                    var canvas = page.GetComponentInParent<Canvas>(true);
                    if (canvas != null) preferencesCanvas = canvas.gameObject;
                }
                if (gitPreferencesCategory == null && preferencesCanvas != null) {
                    foreach (var toggle in preferencesCanvas.GetComponentsInChildren<Toggle>(true)) {
                        for (int i = 0; i < toggle.onValueChanged.GetPersistentEventCount(); i++) {
                            if (toggle.onValueChanged.GetPersistentTarget(i) == page.gameObject) {
                                gitPreferencesCategory = toggle;
                                break;
                            }
                        }
                        if (gitPreferencesCategory != null) break;
                    }
                }
            }
            if (preferencesCanvas == null || gitPreferencesCategory == null || page == null) {
                Debug.LogError("[Parts List Git] Version Control preferences are missing from the loaded scene. Reload Assets/Scenes/Main.unity.", this);
                preferencesRoutine = null;
                yield break;
            }
            preferencesCanvas.SetActive(true);
            preferencesCanvas.transform.localScale = Vector3.one;
            // Let the window and its category group finish OnEnable/Start first.
            yield return null;
            if (preferencesCanvas == null || !preferencesCanvas.activeInHierarchy) yield break;
            gitPreferencesCategory.isOn = true;
            // Reopening an already-selected toggle doesn't emit onValueChanged.
            // Explicitly show this page and hide the other category pages as well.
            foreach (Transform sibling in page.transform.parent)
                sibling.gameObject.SetActive(sibling == page.transform);
            gitPreferencesCategory.Select();
            Canvas.ForceUpdateCanvases();
            preferencesRoutine = null;
        }

        private void Update() {
            if (status != null && progress != null) status.text = progress;
            if (window != null && window.activeSelf && Time.unscaledTime >= nextLocationCheck) {
                nextLocationCheck = Time.unscaledTime + 1f;
                RefreshLocation();
                UpdateCommitButton();
            }
        }

        private void OnDestroy() {
            BuildsManager.RepositoryFileOpened -= CheckOpenedDesign;
            BuildsManager.SavedForGit -= OfferSavedCommit;
            GitPreferences.Changed -= PreferencesChanged;
            openGeneration++;
            if (notice != null) Destroy(notice);
            BuildsManager.IsGitSaveChoicePending = null;
            if (window != null) Destroy(window);
        }

        private void CreateWindow(bool restoreConnection = true) {
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            window = NewCanvas("Version Control", 30000);
            var shade = Box("Background", window.transform, new Color(0, 0, 0, .65f));
            Stretch(shade.rectTransform);
            var panel = Box("Window", shade.transform, new Color(.09f, .11f, .14f));
            var rect = panel.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
            rect.sizeDelta = new Vector2(820, 690);
            Label(panel.transform, "Version Control", 28, 20, 600, 32, 24);
            designLocation = Label(panel.transform, "No saved design open.", 28, 57, 650, 40, 13);
            MakeButton(panel.transform, "Close", 690, 22, 100, () => { if (!busy) window.SetActive(false); });

            var viewport = Box("Scrollable details", panel.transform, new Color(.12f, .14f, .18f));
            Position(viewport.rectTransform, 20, 100, 780, 475);
            viewport.gameObject.AddComponent<RectMask2D>();
            scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport.rectTransform;
            scroll.horizontal = false;
            scroll.scrollSensitivity = 35;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(viewport.transform, false);
            scrollContent = content.GetComponent<RectTransform>();
            Position(scrollContent, 0, 0, 758, 1110);
            scroll.content = scrollContent;
            var track = Box("Scroll track", panel.transform, new Color(.16f, .19f, .24f));
            Position(track.rectTransform, 786, 100, 10, 475);
            var thumb = Box("Scroll thumb", track.transform, new Color(.4f, .48f, .58f));
            Stretch(thumb.rectTransform);
            var scrollbar = track.gameObject.AddComponent<Scrollbar>();
            scrollbar.handleRect = thumb.rectTransform;
            scrollbar.targetGraphic = thumb;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scroll.verticalScrollbar = scrollbar;
            var body = scrollContent;

            Label(body, "REPOSITORY", 24, 20, 700, 24, 15);
            connection = Label(body, "No repository connected. Choose a local folder or clone a GitHub URL.", 24, 52, 700, 100, 15);
            MakeButton(body, "Choose Local Repository", 24, 166, 260, () => { if (!busy) ChooseLocal(); });
            url = Field(body, "https://github.com/owner/repository", 24, 216, 516);
            MakeButton(body, "Clone URL", 552, 216, 170, () => { if (!busy) Clone(); });
            Label(body, "BRANCH", 24, 282, 700, 24, 15);
            branchField = Field(body, "Existing or new branch name", 24, 316, 360);
            branchField.onValueChanged.AddListener(_ => UpdateCommitButton());
            MakeButton(body, "Switch", 398, 316, 150, () => { if (!busy) ChangeBranch(false); });
            MakeButton(body, "Create New", 560, 316, 162, () => { if (!busy) ChangeBranch(true); });
            MakeButton(body, "Choose Existing Branch", 24, 366, 260, () => { if (!busy) ChooseBranch(); });
            branches = Label(body, "No branches loaded.", 304, 366, 418, 60, 14);
            Label(body, "COMMIT MESSAGE", 24, 458, 700, 24, 15);
            message = Field(body, "Describe your robot changes", 24, 492, 698);
            message.text = "Update robot design";
            Label(body, "Includes your .pbb and parts-list.txt. The design must be saved inside this repository.", 24, 544, 700, 52, 14);
            MakeButton(body, "Check for Updates", 24, 616, 240, () => { if (!busy) CheckUpdates(); });
            Label(body, "PRESERVE YOUR WORK", 24, 694, 700, 24, 15);
            recoveryInfo = Label(body, "If teammates changed the robot, keep editing or back up your work before updating.", 24, 728, 700, 66, 14);
            MakeButton(body, "Back Up My Version & Update", 24, 814, 350, () => { if (!busy) BackupAndUpdate(); });
            MakeButton(body, "Keep Editing", 392, 814, 240, () => { if (!busy) window.SetActive(false); });
            recoveryBranch = Field(body, "New branch for my version", 24, 880, 698);
            recoveryBranch.text = "my-version-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            MakeButton(body, "Save My Version to New Branch", 24, 932, 400, () => { if (!busy) SaveVersionToBranch(); });
            Label(body, "No automatic merging of robot files. Push sends all pending commits on the selected branch.", 24, 998, 700, 60, 14);
            status = Label(panel.transform, "Ready. Scroll for branch and recovery options.", 28, 590, 460, 74, 14);
            commitButton = MakeButton(panel.transform, "Commit & Push", 510, 604, 280, () => { if (!busy) CommitAndPush(); });
            commitButton.interactable = false;
            if (repository != null) SetRepository(repository);
            else {
                var saved = PlayerPrefs.GetString(FolderKey, "");
                if (restoreConnection && !string.IsNullOrEmpty(saved)) Connect(saved);
            }
            RefreshLocation();
            UpdateCommitButton();
        }

        private static string FindGitRoot(string path) {
            if (string.IsNullOrEmpty(path)) return null;
            var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path)));
            while (directory != null) {
                var marker = Path.Combine(directory.FullName, ".git");
                if (Directory.Exists(marker) || File.Exists(marker)) return directory.FullName;
                directory = directory.Parent;
            }
            return null;
        }

        private bool CurrentRepositorySelected => repository != null && currentGitRoot != null &&
            string.Equals(Path.GetFullPath(repository.Root).TrimEnd(Path.DirectorySeparatorChar), currentGitRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

        private void RefreshLocation() {
            if (designLocation == null) return;
            var path = builds?.buildPath;
            currentGitRoot = FindGitRoot(path);
            var folder = string.IsNullOrEmpty(path) ? "Save your .pbb to choose a folder." : Path.GetDirectoryName(Path.GetFullPath(path));
            var summary = string.IsNullOrEmpty(path) ? "Unsaved design" : currentGitRoot == null ? "Not a Git repository" :
                CurrentRepositorySelected ? "GitHub repository selected | Branch: " + repository.Branch : "Git repository found | Not connected in Protobot";
            designLocation.text = summary + "\n" + folder;
            designLocation.color = CurrentRepositorySelected ? new Color(.50f, .85f, .67f) : new Color(1f, .78f, .40f);
        }

        private bool ExplainPushSetup() {
            RefreshLocation();
            string title;
            string explanation;
            if (string.IsNullOrEmpty(builds?.buildPath)) {
                title = "Save your design first";
                explanation = "This robot has no saved folder yet. Save it as a .pbb inside your local GitHub repository, then try again.";
            }
            else if (currentGitRoot == null) {
                title = "This folder is not a Git repository";
                explanation = "Your design is saved locally, but its folder isn't connected to Git. Choose or clone a GitHub repository, then use Save As to save this .pbb inside that folder.";
            }
            else if (!CurrentRepositorySelected) {
                title = "Connect this design's repository";
                explanation = "The design is inside a Git repository, but it isn't the repository currently connected in Protobot. Connect this folder before pushing.";
            }
            else if (branchField.text.Trim() != repository.Branch) {
                title = "Activate your branch first";
                explanation = "A branch name was entered but is not active. Use Switch or Create New before pushing. Nothing has been uploaded.";
            }
            else return true;
            progress = explanation;
            ShowNotice(title, explanation, true);
            NoticeButton("OK", 28, 235, () => CloseNotice());
            if (currentGitRoot != null && !CurrentRepositorySelected) {
                var root = currentGitRoot;
                NoticeButton("Connect This Repository", 282, 235, () => { CloseNotice(); Connect(root); });
            }
            else if (currentGitRoot == null)
                NoticeButton("Choose Repository", 282, 235, () => { CloseNotice(); ChooseLocal(); });
            return false;
        }

        private GameObject NewCanvas(string name, int order) {
            var root = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            root.GetComponent<Canvas>().sortingOrder = order;
            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(900, 760);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            return root;
        }

        private void ChooseLocal() {
            var paths = StandaloneFileBrowser.OpenFolderPanel("Choose a local GitHub repository", "", false);
            if (paths.Length > 0 && !string.IsNullOrEmpty(paths[0])) Connect(paths[0]);
        }

        private async void Connect(string path) {
            openGeneration++;
            CloseNotice();
            await Execute(async () => {
                repository = null;
                branchNames = Array.Empty<string>();
                branchField.text = "";
                connection.text = "Connecting to: " + path;
                RefreshLocation();
                progress = "Connecting to repository...";
                var selected = new PartsListGitRepository();
                try { await Task.Run(() => selected.Connect(path)); }
                catch {
                    connection.text = "Could not connect to: " + path + "\nChoose a local GitHub clone, or clone a GitHub URL below.";
                    throw;
                }
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
            RefreshLocation();
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
            branchNames = names;
            branches.text = names.Length + " branch(es) available. Active: " + repository.Branch;
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
            recoveryInfo.text = "Back up your local version before updating, or save it on a new branch.";
            if (scroll != null) scroll.verticalNormalizedPosition = 0;
            progress = "Choose Back Up My Version & Update, Keep Editing My Version, or save to a new branch.";
        }

        private async void BackupAndUpdate() {
            var path = builds?.buildPath;
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
                builds?.ReloadAfterRepositoryChange(repository.Root);
                await StartingRevision(path, true);
                Debug.Log("[Parts List Git] " + progress, this);
            });
        }

        private async void SaveVersionToBranch() {
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

        private void PreferencesChanged() {
            openGeneration++;
            var button = GetComponent<Button>();
            if (button != null) button.interactable = true;
            if (!GitPreferences.Enabled) {
                if (window != null) window.SetActive(false);
                if (notice != null) Destroy(notice);
                repository = null;
                saveChoiceOpen = false;
            }
        }

        private void ChooseBranch() {
            if (repository == null || branchNames.Length == 0) { progress = "Connect a repository and check for updates first."; return; }
            ShowNotice("Choose a branch", "", true);
            var panel = notice.transform.GetChild(0).GetChild(0);
            var viewport = Box("Branch list", panel, new Color(.14f, .17f, .22f));
            Position(viewport.rectTransform, 28, 78, 500, 140);
            viewport.gameObject.AddComponent<RectMask2D>();
            var list = viewport.gameObject.AddComponent<ScrollRect>();
            list.viewport = viewport.rectTransform;
            list.horizontal = false;
            list.scrollSensitivity = 30;
            list.movementType = ScrollRect.MovementType.Clamped;
            var content = new GameObject("Branches", typeof(RectTransform));
            content.transform.SetParent(viewport.transform, false);
            var rect = content.GetComponent<RectTransform>();
            Position(rect, 0, 0, 500, Math.Max(140, branchNames.Length * 42));
            list.content = rect;
            for (int i = 0; i < branchNames.Length; i++) {
                var name = branchNames[i];
                MakeButton(rect, name, 8, i * 42 + 4, 480, () => {
                    CloseNotice();
                    branchField.text = name;
                    ChangeBranch(false);
                });
            }
            NoticeButton("Cancel", 28, 235, () => CloseNotice());
        }

        private async void CheckOpenedDesign(string path) {
            int generation = ++openGeneration;
            if (!GitPreferences.Enabled || busy) return;
            if (!saveChoiceOpen) CloseNotice();
            if (string.IsNullOrEmpty(path)) return;
            var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path)));
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, ".git")) && !File.Exists(Path.Combine(directory.FullName, ".git"))) directory = directory.Parent;
            if (directory == null) return;
            if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            ShowNotice("Checking for updates", Path.GetFileName(path) + " is open. Checking its GitHub repository...", false);
            try {
                var selected = new PartsListGitRepository();
                var fingerprint = DesignComparison.Fingerprint(File.ReadAllBytes(path));
                await Task.Run(() => selected.Connect(directory.FullName));
                var starting = await Task.Run(() => selected.HeadRevision());
                var result = await Task.Run(() => selected.CheckDesign(path, fingerprint, starting));
                if (this == null || generation != openGeneration || !GitPreferences.Enabled || saveChoiceOpen) return;
                repository = selected;
                startingRevisions[BaselineKey(path)] = starting;
                if (notice != null) Destroy(notice);
                if (window != null) SetRepository(selected);
                if (result.Warn) {
                    ShowNotice("Design update", Path.GetFileName(path) + " is open.\n\n" + result.Message, true);
                    NoticeButton("Keep This Version", 28, 235, () => CloseNotice());
                    NoticeButton("Review Update Options", 282, 235, () => { CloseNotice(); Open(); UpdateBranch(); });
                }
            }
            catch (Exception e) {
                if (this == null || generation != openGeneration || saveChoiceOpen || !GitPreferences.Enabled) return;
                Debug.LogWarning("[Parts List Git] Update check failed: " + e.Message, this);
                ShowNotice("Working offline", "Your selected design is open. Couldn't check GitHub. You can keep editing and save locally.", true);
                NoticeButton("Continue Editing", 28, 235, () => CloseNotice());
            }
        }

        private void ShowNotice(string title, string text, bool blocking) {
            CloseNotice();
            notice = NewCanvas("Version Control Message", 31000);
            var background = Box("Background", notice.transform, new Color(0, 0, 0, blocking ? .55f : 0));
            Stretch(background.rectTransform);
            background.raycastTarget = blocking;
            var panel = Box("Card", background.transform, new Color(.10f, .13f, .18f));
            panel.name = "NoticeCard";
            var rect = panel.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
            rect.sizeDelta = new Vector2(560, blocking ? 300 : 160);
            Label(panel.transform, title, 28, 24, 500, 36, 23);
            Label(panel.transform, text, 28, 78, 500, blocking ? 145 : 65, 15);
        }

        private void NoticeButton(string text, float x, float y, UnityEngine.Events.UnityAction action) {
            var panel = notice.transform.GetChild(0).GetChild(0);
            MakeButton(panel, text, x, y, 246, action);
        }

        private void CloseNotice() { if (notice != null) Destroy(notice); notice = null; }

        private void OfferSavedCommit(string path) {
            if (!GitPreferences.Enabled || !GitPreferences.PromptAfterSave || saveChoiceOpen || busy) return;
            if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            saveChoiceOpen = true;
            ShowNotice("Saved locally", Path.GetFileName(path) + " was saved successfully.\n\nWould you like to commit and push these robot changes to GitHub?", true);
            NoticeButton("Not Now", 28, 235, () => { saveChoiceOpen = false; CloseNotice(); });
            NoticeButton("Commit & Push", 282, 235, () => {
                saveChoiceOpen = false;
                CloseNotice();
                if (builds == null || builds.buildPath != path) return;
                Open();
                RefreshLocation();
                if (!CurrentRepositorySelected) {
                    if (scroll != null) scroll.verticalNormalizedPosition = 1;
                    ExplainPushSetup();
                    return;
                }
                if (scroll != null) scroll.verticalNormalizedPosition = .46f;
                message.Select();
                progress = "Saved locally. Review the branch, enter a commit message, then choose Commit & Push.";
            });
        }

        private async void CommitAndPush() {
            if (!ExplainPushSetup()) return;
            await Execute(async () => {
                if (repository == null) throw new InvalidOperationException("Connect a repository first.");
                var targetBranch = branchField.text.Trim();
                if (targetBranch != repository.Branch)
                    throw new InvalidOperationException("Click Create Branch or Switch Branch to activate '" + targetBranch + "' before pushing.");
                if (string.IsNullOrWhiteSpace(message.text)) throw new InvalidOperationException("Enter a commit message.");
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
            if (busy || !GitPreferences.Enabled) return;
            openGeneration++;
            if (!saveChoiceOpen) CloseNotice();
            busy = true;
            commitButton.interactable = false;
            try { await action(); }
            catch (Exception e) {
                progress = e.Message;
                if (this != null) Debug.LogError("[Parts List Git] " + e.Message, this);
                if (this != null && GitPreferences.Enabled) {
                    var text = e.Message.Length > 320 ? e.Message.Substring(0, 320) + "...\nSee Console for details." : e.Message;
                    ShowNotice("Version Control needs attention", text, true);
                    NoticeButton("OK", 28, 235, () => CloseNotice());
                }
            }
            finally {
                busy = false;
                UpdateCommitButton();
            }
        }

        private void UpdateCommitButton() {
            if (commitButton == null) return;
            commitButton.interactable = !busy && GitPreferences.Enabled;
            var caption = commitButton.GetComponentInChildren<Text>();
            if (caption != null) caption.text = !CurrentRepositorySelected ? "Set Up Repository" : branchField.text.Trim() != repository.Branch ? "Activate Branch" : "Push to " + repository.Branch;
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
