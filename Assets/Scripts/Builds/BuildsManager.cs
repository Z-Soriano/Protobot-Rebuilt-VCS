using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.Serialization.Formatters.Binary;
using UnityEngine;
using UnityEngine.UI;
using SFB;

using Protobot.UI;
using Protobot.InputEvents;
using UnityEngine.SceneManagement;

namespace Protobot.Builds {
    public class BuildsManager : MonoBehaviour {
        public static event Action<string> RepositoryFileOpened;
        public static event Action<string> SavedForGit;
        public static Func<bool> IsGitSaveChoicePending;
        [SerializeField] private InputEvent saveInput;

        [SerializeField] private UnsavedChangesUI unsavedChangesMenu;
        
        /// <summary>
        /// Stores the path where the current build is located
        /// </summary>
        public string buildPath = "";
        
        /// <summary>
        /// Stores the currently saved data for the loaded build
        /// </summary>
        private BuildData savedBuildData;

        private string attemptPath = "";
        private BuildData attemptData;
        
        public BuildDataUnityEvent OnLoadBuild;
        public BuildDataUnityEvent OnSaveBuild;

        private bool avoidingQuit = false;
        private bool forceQuit = false;

        private bool IsNotSaved => buildPath == "";
        private float nextViewSave;

        private void Update() {
            if (Time.unscaledTime < nextViewSave) return;
            nextViewSave = Time.unscaledTime + 2f;
            SaveLocalView();
        }

        private void SaveLocalView() { LocalDesignView.Save(buildPath, LocalDesignView.Capture()); }
        private void OnApplicationQuit() { SaveLocalView(); }
        private void OnApplicationPause(bool paused) { if (paused) SaveLocalView(); }

        private void Awake() {
            saveInput.performed += Save;
        }

        public void Start() {
            buildPath = "";
            
            SceneBuild.OnGenerateBuild += (data) => {
                OnLoadBuild.Invoke(data);
            };
            

            Application.wantsToQuit += () => {
                if (IsGitSaveChoicePending?.Invoke() == true) return false;
                avoidingQuit = HasUnsavedChanges() && !forceQuit;
                if (avoidingQuit) {
                    unsavedChangesMenu.Enable(IsNotSaved);
                }

                return !avoidingQuit;
            };

            unsavedChangesMenu.OnPressDiscard += () => {
                if (avoidingQuit) 
                    Quit();
                else
                    LoadAttempt();
            };

            unsavedChangesMenu.OnPressSave += () => {
                if (avoidingQuit)
                    SaveAndQuit();
                else
                    SaveAndLoadAttempt();
            };
            
            string[] arguments = Environment.GetCommandLineArgs();
            string initPath = arguments[0];
            var initData = ParsePath(initPath);

            if (initData != null)
                AttemptLoad(initData, initPath);
        }

        public string GetFileName() => PathToFileName(buildPath);

        public static byte[] SerializeSnapshot(BuildData snapshot, bool includeView = false) {
            var output = new BuildData {
                name = snapshot.name, fileName = snapshot.fileName, lastWriteTime = snapshot.lastWriteTime,
                version = snapshot.version, parts = snapshot.parts,
                camera = includeView ? snapshot.camera : SceneBuild.DefaultBuild.camera
            };
            using (var stream = new MemoryStream()) {
                new BinaryFormatter().Serialize(stream, output);
                return stream.ToArray();
            }
        }

        public void MarkSnapshotSaved(string path, BuildData snapshot) {
            if (!string.Equals(Path.GetFullPath(buildPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return;
            savedBuildData = snapshot;
            LocalDesignView.Save(path, snapshot.camera);
            OnSaveBuild?.Invoke(snapshot);
        }
        
        public static string PathToFileName(string path) => (path.Length > 0) ? path.Split('\\')[^1] : "";
        
        public void Save() { SaveCore(true, false); }
        public void SaveAs() { SaveCore(true, true); }

        private bool SaveCore(bool offerGit, bool choosePath) {
            try {
                var path = buildPath;
                if (choosePath || string.IsNullOrEmpty(path)) {
                    path = StandaloneFileBrowser.SaveFilePanel("Save Build File", "", "", "pbb");
                    if (string.IsNullOrEmpty(path)) return false;
                }
                var snapshot = SceneBuild.ToBuildData();
                bool changed = savedBuildData == null ||
                    DesignComparison.Fingerprint(savedBuildData) != DesignComparison.Fingerprint(snapshot);
                // A view-only save must not dirty the tracked design file (including legacy files).
                if (changed || choosePath || path != buildPath || !File.Exists(path))
                    File.WriteAllBytes(path, SerializeSnapshot(snapshot));
                LocalDesignView.Save(path, snapshot.camera);
                buildPath = path;
                savedBuildData = snapshot;
                OnSaveBuild?.Invoke(snapshot);
                if (offerGit && changed) SavedForGit?.Invoke(path);
                return true;
            }
            catch (Exception e) {
                Debug.LogError("Could not save design: " + e.Message);
                return false;
            }
        }

        public void SaveAndQuit() {
            if (SaveCore(false, false)) Quit();
            else avoidingQuit = false;
        }

        public void Quit() {
            forceQuit = true;
            Application.Quit();
        }

        public void AttemptQuit() {
            Application.Quit();
        }

        public bool HasUnsavedChanges() {
            var sceneBuild = SceneBuild.ToBuildData();
            
            if (IsNotSaved) {
                return sceneBuild.parts.Length > 0;
            }

            if (savedBuildData == null) return true;
            // Protect every part detail, including small moves; camera/view is a local preference.
            var current = sceneBuild.parts.Select(p => JsonUtility.ToJson(p)).OrderBy(p => p, StringComparer.Ordinal);
            var saved = savedBuildData.parts.Select(p => JsonUtility.ToJson(p)).OrderBy(p => p, StringComparer.Ordinal);
            return !current.SequenceEqual(saved);
        }

        public void OpenBuild() {
            if (IsGitSaveChoicePending?.Invoke() == true) return;
            var paths = StandaloneFileBrowser.OpenFilePanel("Open Build File", "", "pbb", false);

            if (paths.Length == 0 || paths[0] == "") return;

            var path = paths[0];

            var build = ParsePath(path);
            AttemptLoad(build, path);
        }
        
        /// <summary>
        /// Converts a given file path to BuildData
        /// </summary>
        public static BuildData ParsePath(string filePath) {
            if (!File.Exists(filePath) || !filePath.Contains(".pbb")) return null;
            
            BinaryFormatter bf = new BinaryFormatter();
            FileStream file = File.Open(filePath, FileMode.Open);

            BuildData build = (BuildData)bf.Deserialize(file);
            file.Close();

            return build;
        }
        
        /// <summary>
        /// Sets the attempt variables to be ready once a load call is given
        /// </summary>
        /// <param name="newData"></param>
        /// <param name="newPath"></param>
        public void AttemptLoad(BuildData newData, string newPath) {
            if (IsGitSaveChoicePending?.Invoke() == true) return;
            attemptData = newData;
            attemptPath = newPath;
            if (HasUnsavedChanges()) unsavedChangesMenu.Enable(IsNotSaved);
            else LoadAttempt();
        }

        public void ReloadAfterRepositoryChange(string root) {
            if (string.IsNullOrEmpty(buildPath)) {
                attemptPath = "";
                attemptData = SceneBuild.DefaultBuild;
                LoadAttempt();
                return;
            }
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(buildPath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
            attemptPath = File.Exists(buildPath) ? buildPath : "";
            attemptData = attemptPath == "" ? SceneBuild.DefaultBuild : ParsePath(attemptPath);
            LoadAttempt();
        }

        public void RestoreParkedDesign(string draftPath, string originalPath) {
            var draft = ParsePath(draftPath);
            if (draft == null) throw new IOException("The parked robot draft could not be opened: " + draftPath);
            SaveLocalView();
            buildPath = originalPath;
            savedBuildData = File.Exists(originalPath) ? ParsePath(originalPath) : null;
            SceneBuild.GenerateBuild(draft);
            RepositoryFileOpened?.Invoke(buildPath);
        }

        public void SaveAndLoadAttempt() {
            if (SaveCore(false, false)) LoadAttempt();
        }

        public void LoadAttempt() {
            SaveLocalView();
            savedBuildData = attemptData;
            buildPath = attemptPath;
            var loadData = new BuildData {
                name = attemptData.name, fileName = attemptData.fileName, lastWriteTime = attemptData.lastWriteTime,
                version = attemptData.version, parts = attemptData.parts,
                camera = LocalDesignView.Load(buildPath, attemptData.camera ?? SceneBuild.DefaultBuild.camera)
            };
            SceneBuild.GenerateBuild(loadData);
            // Use the loaded scene's precision/defaults as the clean baseline. Converting
            // a stored double to Unity's float must not look like a new user edit.
            savedBuildData = SceneBuild.ToBuildData();

            // Call OutputPartsList to update the weight display after placing a part
                PartListOutput partListOutput = FindObjectOfType<PartListOutput>();
                if (partListOutput != null) {
                    partListOutput.CalculatePartsList(); // Recalculate and notify weight update
                }
            RepositoryFileOpened?.Invoke(buildPath);
        }

        /// <summary>
        /// Loads an empty build with an untitled.pbb
        /// </summary>
        public void CreateNewBuild() {
            AttemptLoad(SceneBuild.DefaultBuild, "");
        }
    }
}
