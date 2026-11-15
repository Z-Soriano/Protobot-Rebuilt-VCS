using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Protobot.Builds {
    // Local preferences, outside the design repository and shared .pbb snapshot.
    public static class LocalDesignView {
        private static string Key(string path) {
            using (var hash = SHA256.Create())
                return "DesignView.v1." + Convert.ToBase64String(hash.ComputeHash(
                    Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
        }

        public static void Save(string path, CameraData camera) {
            if (string.IsNullOrEmpty(path) || camera == null) return;
            var key = Key(path);
            var json = JsonUtility.ToJson(camera);
            if (PlayerPrefs.GetString(key, "") == json) return;
            PlayerPrefs.SetString(key, json);
            PlayerPrefs.Save();
        }

        public static CameraData Load(string path, CameraData fallback) {
            if (string.IsNullOrEmpty(path)) return fallback;
            try {
                var json = PlayerPrefs.GetString(Key(path), "");
                return string.IsNullOrEmpty(json) ? fallback : JsonUtility.FromJson<CameraData>(json) ?? fallback;
            }
            catch (ArgumentException) { return fallback; }
        }

        public static CameraData Capture() {
            var camera = PivotCamera.Main;
            if (camera == null) return null;
            var projection = camera.GetComponent<ProjectionSwitcher>();
            return new CameraData {
                xPos = camera.focusPosition.x, yPos = camera.focusPosition.y, zPos = camera.focusPosition.z,
                xRot = camera.lookAngle.x, yRot = camera.lookAngle.y, zRot = camera.lookAngle.z,
                zoom = camera.focusDistance, isOrtho = projection != null && projection.isOrtho
            };
        }
    }
}
