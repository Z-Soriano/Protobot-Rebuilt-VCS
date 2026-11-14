using System;
using System.Threading.Tasks;
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

        public void Open() {
            if (window == null) CreateWindow();
            window.SetActive(true);
        }

        private void Update() {
            if (status != null && progress != null) status.text = progress;
        }

        private void OnDestroy() {
            if (window != null) Destroy(window);
        }

        private void CreateWindow() {
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            window = new GameObject("Parts List GitHub", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = window.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            var scaler = window.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1000, 700);
            scaler.matchWidthOrHeight = 0.5f;

            var shade = Box("Modal Background", window.transform, new Color(0, 0, 0, 0.7f));
            Stretch(shade.rectTransform);
            var panel = Box("Panel", shade.transform, new Color(0.12f, 0.13f, 0.15f));
            var rect = panel.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(760, 620);

            Label(panel.transform, "Commit & Push Parts List", 24, 18, 640, 30, 22);
            MakeButton(panel.transform, "Close", 650, 18, 86, () => { if (!busy) window.SetActive(false); });
            Label(panel.transform, "Choose a GitHub repository", 24, 62, 700, 24, 17);
            url = Field(panel.transform, "https://github.com/owner/repository", 24, 94, 550);
            MakeButton(panel.transform, "Clone URL...", 586, 94, 150, () => { if (!busy) Clone(); });
            MakeButton(panel.transform, "Choose Local Repository...", 24, 138, 250, () => { if (!busy) ChooseLocal(); });
            Label(panel.transform, "Git must be installed. A browser may open for GitHub sign-in.", 24, 180, 710, 26, 14);
            connection = Label(panel.transform, "No repository connected.", 24, 214, 710, 76, 14);
            Label(panel.transform, "Commit message", 24, 294, 700, 24, 16);
            message = Field(panel.transform, "Describe your parts changes", 24, 324, 712);
            message.text = "Update parts list";
            Label(panel.transform,
                "Saves the current design to parts-list.txt at the repository root.\n" +
                "Replaces that file on each commit. Push sends all pending commits on the shown branch.",
                24, 370, 710, 48, 14);
            commitButton = MakeButton(panel.transform, "Commit & Push", 24, 428, 200, () => { if (!busy) CommitAndPush(); });
            commitButton.interactable = false;
            status = Label(panel.transform, "Choose a local clone, or paste a URL and choose where to clone it.", 24, 476, 710, 122, 14);

            // Paths are local preferences; no GitHub credentials are stored by this UI.
            var saved = PlayerPrefs.GetString(FolderKey, "");
            if (!string.IsNullOrEmpty(saved)) Connect(saved);
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
                progress = "Connected. Review the destination above, then enter a message and commit.";
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
                progress = "Cloned and connected. Enter a message, then commit and push.";
            });
        }

        private void SetRepository(PartsListGitRepository selected) {
            repository = selected;
            connection.text = "Folder: " + selected.Root + "\nGitHub: " + selected.Remote + "\nBranch: " + selected.Branch;
            PlayerPrefs.SetString(FolderKey, selected.Root);
            PlayerPrefs.Save();
        }

        private async void CommitAndPush() {
            await Execute(async () => {
                if (repository == null) throw new InvalidOperationException("Connect a repository first.");
                if (string.IsNullOrWhiteSpace(message.text)) throw new InvalidOperationException("Enter a commit message.");
                if (exporter == null) throw new InvalidOperationException("The parts-list exporter is missing from this button.");
                // Capture Unity scene state on the main thread before doing Git work.
                var snapshot = exporter.GetPartsListText();
                var commitMessage = message.text;
                progress = "Preparing parts-list.txt...";
                var result = await Task.Run(() => repository.CommitAndPush(snapshot, commitMessage, value => progress = value));
                progress = result;
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
                if (commitButton != null) commitButton.interactable = repository != null;
            }
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
