using System;
using UnityEngine;
using UnityEngine.UI;

namespace Protobot.UI {
    public class GitPreferences : MonoBehaviour {
        public Toggle enabledToggle;
        public Toggle promptToggle;
        public static bool Enabled => PlayerPrefs.GetInt("GitIntegration.Enabled", 0) != 0;
        public static bool PromptAfterSave => PlayerPrefs.GetInt("GitIntegration.PromptAfterSave", 1) != 0;
        public static event Action Changed;

        private void Awake() {
            enabledToggle.SetIsOnWithoutNotify(Enabled);
            promptToggle.SetIsOnWithoutNotify(PromptAfterSave);
            promptToggle.interactable = Enabled;
            enabledToggle.onValueChanged.AddListener(value => {
                PlayerPrefs.SetInt("GitIntegration.Enabled", value ? 1 : 0);
                promptToggle.interactable = value;
                PlayerPrefs.Save();
                Changed?.Invoke();
            });
            promptToggle.onValueChanged.AddListener(value => {
                PlayerPrefs.SetInt("GitIntegration.PromptAfterSave", value ? 1 : 0);
                PlayerPrefs.Save();
                Changed?.Invoke();
            });
        }
    }
}
