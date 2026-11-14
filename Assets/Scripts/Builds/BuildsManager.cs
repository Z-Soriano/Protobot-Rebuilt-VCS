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
        public static Func<string, Task<bool>> ConfirmRepositoryOpen;
        private bool checkingOpen;
        private bool repositoryOpenBackedUp;
        public void ConfirmOpenSceneWasBackedUp() { repositoryOpenBackedUp = true; }
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

        private void Awake() {
            saveInput.performed += Save;
        }

        public void Start() {
            buildPath = "";
            
            SceneBuild.OnGenerateBuild += (data) => {
                OnLoadBuild.Invoke(data);
            };
            

            Application.wantsToQuit += () => {
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

        public static byte[] SerializeSnapshot(BuildData snapshot) {
            using (var stream = new MemoryStream()) {
                new BinaryFormatter().Serialize(stream, snapshot);
                return stream.ToArray();
            }
        }

        public void MarkSnapshotSaved(string path, BuildData snapshot) {
            if (!string.Equals(Path.GetFullPath(buildPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return;
            savedBuildData = snapshot;
            OnSaveBuild?.Invoke(snapshot);
        }
        
        public static string PathToFileName(string path) => (path.Length > 0) ? path.Split('\\')[^1] : "";
        
        public void Save() {
            if (buildPath == "") {
                SaveAs();
                return;
            }

            var sceneBuildData = SceneBuild.ToBuildData();
            
            BinaryFormatter bf = new BinaryFormatter();

            FileStream file = File.Create(buildPath);
            bf.Serialize(file, sceneBuildData);
            file.Close();

            savedBuildData = sceneBuildData;
            OnSaveBuild?.Invoke(sceneBuildData);
        }

        public void SaveAs() {
            var path = StandaloneFileBrowser.SaveFilePanel("Save Build File", "", "", "pbb");

            if (path == "") return;

            buildPath = path;
            Save();
        }

        public void SaveAndQuit() {
            Save();
            Quit();
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
            // Include color/state changes and repeated instances when protecting an update.
            var current = sceneBuild.parts.Select(p => JsonUtility.ToJson(p)).OrderBy(p => p, StringComparer.Ordinal);
            var saved = savedBuildData.parts.Select(p => JsonUtility.ToJson(p)).OrderBy(p => p, StringComparer.Ordinal);
            return !current.SequenceEqual(saved) || JsonUtility.ToJson(sceneBuild.camera) != JsonUtility.ToJson(savedBuildData.camera);
        }

        public void OpenBuild() {
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
        public async void AttemptLoad(BuildData newData, string newPath) {
            if (checkingOpen) return;
            if (!string.IsNullOrEmpty(newPath) && ConfirmRepositoryOpen != null) {
                checkingOpen = true;
                try {
                    if (!await ConfirmRepositoryOpen(newPath)) return;
                    // An explicit update may have changed the file while the dialog was open.
                    newData = ParsePath(newPath);
                    if (newData == null && !File.Exists(newPath)) {
                        newData = SceneBuild.DefaultBuild;
                        newPath = "";
                    }
                }
                catch (Exception e) { Debug.LogError("Could not open design: " + e.Message); return; }
                finally { checkingOpen = false; }
            }
            attemptData = newData;
            attemptPath = newPath;

            var backedUp = repositoryOpenBackedUp;
            repositoryOpenBackedUp = false;

            if (!backedUp && HasUnsavedChanges()) {
                unsavedChangesMenu.Enable(IsNotSaved);
            }
            else {
                LoadAttempt();
            }
        }

        public void ReloadAfterRepositoryChange(string root) {
            if (string.IsNullOrEmpty(buildPath)) return;
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(buildPath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
            attemptPath = File.Exists(buildPath) ? buildPath : "";
            attemptData = attemptPath == "" ? SceneBuild.DefaultBuild : ParsePath(attemptPath);
            LoadAttempt();
        }

        public void SaveAndLoadAttempt() {
            Save();
            LoadAttempt();
        }

        public void LoadAttempt() {
            savedBuildData = attemptData;
            buildPath = attemptPath;
            SceneBuild.GenerateBuild(attemptData);

            // Call OutputPartsList to update the weight display after placing a part
                PartListOutput partListOutput = FindObjectOfType<PartListOutput>();
                if (partListOutput != null) {
                    partListOutput.CalculatePartsList(); // Recalculate and notify weight update
                }
        }

        /// <summary>
        /// Loads an empty build with an untitled.pbb
        /// </summary>
        public void CreateNewBuild() {
            AttemptLoad(SceneBuild.DefaultBuild, "");
        }
    }
}
