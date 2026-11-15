using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using System.Text;
using Protobot.Builds;

namespace Protobot.UI {
    public enum DesignUpdateState { Same, LocalOnly, RemoteOnly, BothChanged, NewBranch, Unknown }

    public sealed class DesignUpdateResult {
        public DesignUpdateState State;
        public string Message;
        public bool Warn => State == DesignUpdateState.RemoteOnly || State == DesignUpdateState.BothChanged || State == DesignUpdateState.Unknown;
    }

    public static class DesignComparison {
        // Parts are compared as a multiset: order, camera, save date, and file labels do not matter.
        public static string Fingerprint(BuildData data) {
            if (data == null || data.parts == null) throw new InvalidDataException("The design has no valid parts data.");
            var rows = data.parts.Select(part => {
                if (part == null) throw new InvalidDataException("The design contains an invalid part.");
                var row = new StringBuilder();
                Add(row, part.partId); Add(row, part.states);
                foreach (var number in new[] { part.xPos, part.yPos, part.zPos, part.xRot, part.yRot, part.zRot, part.rColor, part.gColor, part.bColor }) {
                    // Unity loads these values into floats; compare the same precision as the editor.
                    if (double.IsNaN(number) || double.IsInfinity(number)) throw new InvalidDataException("Invalid part coordinates.");
                    Add(row, ((float)number).ToString("R", CultureInfo.InvariantCulture));
                }
                return row.ToString();
            }).OrderBy(row => row, StringComparer.Ordinal);
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", rows))));
        }

        private static void Add(StringBuilder row, string value) {
            if (value == null) { row.Append("-1:"); return; }
            row.Append(value.Length).Append(':').Append(value);
        }

        public static string Fingerprint(byte[] bytes) {
            if (bytes == null) return null; // File absent in that revision (including deletion).
            if (bytes.Length > 64 * 1024 * 1024) throw new InvalidDataException("Design is too large to compare automatically.");
            using (var stream = new MemoryStream(bytes, false)) {
                var formatter = new BinaryFormatter { Binder = new DesignTypesOnly() };
                return Fingerprint(formatter.Deserialize(stream) as BuildData);
            }
        }

        public static DesignUpdateResult Compare(string baseline, string local, string remote) {
            if (local == remote) return Result(DesignUpdateState.Same, "Your robot matches the design on GitHub.");
            if (remote == baseline) return Result(DesignUpdateState.LocalOnly, "Only your robot design has changed. Your edits are preserved.");
            if (local == baseline) return Result(DesignUpdateState.RemoteOnly, "The robot design on GitHub changed since your starting version. Back Up & Pull, or continue without pulling.");
            return Result(DesignUpdateState.BothChanged, "You and a teammate changed this robot since your shared starting version. Back up and pull, or create a branch to commit and push your version; combining the designs needs manual work.");
        }

        public static DesignUpdateResult Result(DesignUpdateState state, string message) => new DesignUpdateResult { State = state, Message = message };

        // GitHub blobs are data, never arbitrary .NET object graphs.
        private sealed class DesignTypesOnly : SerializationBinder {
            public override Type BindToType(string assemblyName, string typeName) {
                foreach (var type in new[] { typeof(BuildData), typeof(ObjectData), typeof(ObjectData[]), typeof(CameraData) })
                    if (typeName == type.FullName) return type;
                throw new SerializationException("Unsupported type in .pbb design: " + typeName);
            }
        }
    }
}
