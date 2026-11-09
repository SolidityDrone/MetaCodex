using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace MetaCodex.Security
{
    /// Seals small values with a key the caller never sees.
    public interface ISealer
    {
        /// "android-keystore" or "dev-file".
        string Kind { get; }
        /// Where the key lives: strongbox / tee / software / dev-file.
        string Level { get; }
        string Seal(byte[] plain, string aad);
        byte[] Open(string sealedValue, string aad);
        string LastError { get; }
    }

    /// The headset: AES-256-GCM with a non-exportable key in the Android Keystore (MetaCodexVault.java).
    public sealed class AndroidKeystoreSealer : ISealer
    {
        private const string Cls = "com.pnncl.metacodex.vault.MetaCodexVault";
        public string Kind => "android-keystore";
        public string LastError { get; private set; }
        private string _level;

        public string Level
        {
            get
            {
                if (_level != null) return _level;
#if UNITY_ANDROID && !UNITY_EDITOR
                try { using (var c = new AndroidJavaClass(Cls)) _level = c.CallStatic<string>("securityLevel"); }
                catch (Exception ex) { _level = "unknown: " + ex.Message; }
#else
                _level = "n/a";
#endif
                return _level;
            }
        }

        public string Seal(byte[] plain, string aad)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var c = new AndroidJavaClass(Cls))
            {
                var r = c.CallStatic<string>("seal", Convert.ToBase64String(plain), aad);
                if (r == null) LastError = c.CallStatic<string>("lastError");
                return r;
            }
#else
            LastError = "Android Keystore only on the headset";
            return null;
#endif
        }

        public byte[] Open(string sealedValue, string aad)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var c = new AndroidJavaClass(Cls))
            {
                var r = c.CallStatic<string>("open", sealedValue, aad);
                if (r == null) { LastError = c.CallStatic<string>("lastError"); return null; }
                return Convert.FromBase64String(r);
            }
#else
            LastError = "Android Keystore only on the headset";
            return null;
#endif
        }

        public static string NoBackupDir()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var c = new AndroidJavaClass(Cls))
                return c.CallStatic<string>("noBackupDir", activity);
#else
            return null;
#endif
        }
    }

    /// The headset when the Android Keystore cannot be reached: nothing is sealed or opened (no plain-text fallback, no
    /// key in a file). The vault shows why and stays locked until the keystore works again.
    public sealed class LockedSealer : ISealer
    {
        public LockedSealer(string why) { LastError = "vault locked: " + why; }
        public string Kind => "locked";
        public string Level => "locked (Android Keystore unavailable)";
        public string LastError { get; }
        public string Seal(byte[] plain, string aad) => null;
        public byte[] Open(string sealedValue, string aad) => null;
    }

    /// Editor and tests only: AES-256-CBC + HMAC-SHA256 (encrypt-then-MAC) with a random key in a local file.
    /// Not a protection (the key sits next to the data); it exists so the vault logic runs off the headset.
    public sealed class DevFileSealer : ISealer
    {
        private readonly byte[] _enc = new byte[32], _mac = new byte[32];
        public string Kind => "dev-file";
        public string Level => "dev-file (no hardware key)";
        public string LastError { get; private set; }

        public DevFileSealer(string keyPath)
        {
            byte[] k;
            if (File.Exists(keyPath)) k = Convert.FromBase64String(File.ReadAllText(keyPath).Trim());
            else
            {
                k = new byte[64];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(k);
                Directory.CreateDirectory(Path.GetDirectoryName(keyPath));
                File.WriteAllText(keyPath, Convert.ToBase64String(k));
            }
            Buffer.BlockCopy(k, 0, _enc, 0, 32);
            Buffer.BlockCopy(k, 32, _mac, 0, 32);
        }

        public string Seal(byte[] plain, string aad)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = _enc; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                aes.GenerateIV();
                byte[] ct;
                using (var enc = aes.CreateEncryptor()) ct = enc.TransformFinalBlock(plain, 0, plain.Length);
                var body = aes.IV.Concat(ct).ToArray();
                var mac = Mac(body, aad);
                return Convert.ToBase64String(body.Concat(mac).ToArray());
            }
        }

        public byte[] Open(string sealedValue, string aad)
        {
            try
            {
                var all = Convert.FromBase64String(sealedValue);
                if (all.Length < 16 + 16 + 32) throw new Exception("too short");
                var body = all.Take(all.Length - 32).ToArray();
                var mac = all.Skip(all.Length - 32).ToArray();
                if (!FixedEquals(mac, Mac(body, aad))) throw new Exception("authentication failed");
                using (var aes = Aes.Create())
                {
                    aes.Key = _enc; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                    aes.IV = body.Take(16).ToArray();
                    using (var dec = aes.CreateDecryptor()) return dec.TransformFinalBlock(body, 16, body.Length - 16);
                }
            }
            catch (Exception ex) { LastError = ex.Message; return null; }
        }

        private byte[] Mac(byte[] body, string aad)
        {
            using (var h = new HMACSHA256(_mac))
            {
                var a = Encoding.UTF8.GetBytes(aad ?? "");
                var len = BitConverter.GetBytes(a.Length);
                return h.ComputeHash(len.Concat(a).Concat(body).ToArray());
            }
        }

        private static bool FixedEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int d = 0;
            for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }
    }

    [Serializable]
    public sealed class VaultEntry
    {
        public string name = "";
        /// api-key | token | key | password | other
        public string kind = "api-key";
        /// ask | once | allow | deny (who may read it through the control port: see SecretVault)
        public string policy = "ask";
        public string note = "";
        /// sealed value (base64), never the plain text
        public string value = "";
        public long created, updated, lastUsed;
        public int uses;
    }

    public sealed class VaultEvent
    {
        public DateTime Time;
        public string Name, Requester, Decision;
        public override string ToString() => $"{Time:HH:mm:ss} {Decision,-7} {Name} ← {Requester}";
    }

    /// The app's secrets: names in a JSON file, values sealed by the Android Keystore key. The file sits in the
    /// app's no-backup folder, outside the Linux rootfs (proot never binds it), so agents and backups only ever
    /// see ciphertext. Values go out one at a time, decided by each secret's policy:
    ///   ask   - the user approves in VR each time (ALLOW ONCE / ALWAYS / DENY)
    ///   once  - handed out once per app session (bootstrap keys, e.g. MQH's machine-environment key)
    ///   allow - always, logged
    ///   deny  - never to the userland (used only inside the app)
    public sealed class SecretVault
    {
        public static readonly Regex NamePattern = new Regex(@"^[A-Za-z0-9_.\-]{1,64}$");
        public static readonly string[] Policies = { "ask", "once", "allow", "deny" };
        public const int MaxValueBytes = 16 * 1024;

        public string FilePath { get; }
        public ISealer Sealer { get; }
        /// Why the vault file could not be read (it was kept aside as vault.json.unreadable-<time>), or null.
        public string LoadError { get; private set; }
        /// The vault can seal and open (false: locked, see Sealer.LastError).
        public bool Usable => Sealer.Kind != "locked";
        public IReadOnlyList<VaultEntry> Entries => _file.entries;
        public readonly List<VaultEvent> Audit = new List<VaultEvent>();
        public event Action Changed;

        [Serializable] private sealed class VaultFile { public int version = 1; public List<VaultEntry> entries = new List<VaultEntry>(); }
        private VaultFile _file = new VaultFile();
        private readonly HashSet<string> _deliveredThisSession = new HashSet<string>();

        public SecretVault(string filePath, ISealer sealer)
        {
            FilePath = filePath;
            Sealer = sealer;
            Load();
        }

        /// The headset: Android Keystore + no-backup folder, and nothing else: when the keystore cannot be reached the
        /// vault is locked (it never falls back to a key in a file there: `fallbackDir` is the app's shared folder,
        /// which the Linux userland and adb can read). Off the headset (Editor, tests): the dev sealer in `fallbackDir`.
        public static SecretVault OpenDefault(string fallbackDir)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            string why;
            try
            {
                var dir = AndroidKeystoreSealer.NoBackupDir();
                if (!string.IsNullOrEmpty(dir))
                {
                    var v = new SecretVault(Path.Combine(dir, "vault.json"), new AndroidKeystoreSealer());
                    v.AdoptDevFile(Path.Combine(fallbackDir, "vault-dev"));
                    return v;
                }
                why = "no no-backup folder";
            }
            catch (Exception ex) { why = ex.Message; }
            Debug.LogWarning("[MetaCodex] keystore vault unavailable: " + why);
            return new SecretVault(Path.Combine(fallbackDir, "vault-locked.json"), new LockedSealer(why));
#else
            return new SecretVault(Path.Combine(fallbackDir, "vault-dev", "vault.json"), new DevFileSealer(Path.Combine(fallbackDir, "vault-dev", "dev.key")));
#endif
        }

        /// Secrets an older build sealed with the dev sealer on the headset (its key sat in a file in the shared folder):
        /// resealed under the keystore key, then the dev key and file are deleted. Returns how many moved.
        public int AdoptDevFile(string devDir)
        {
            string keyPath = Path.Combine(devDir, "dev.key"), filePath = Path.Combine(devDir, "vault.json");
            if (!File.Exists(keyPath) || !Usable) return 0;
            int moved = 0;
            try
            {
                if (File.Exists(filePath))
                {
                    var old = new SecretVault(filePath, new DevFileSealer(keyPath));
                    foreach (var e in old.Entries.ToList())
                    {
                        if (Has(e.name)) continue;
                        var plain = old.Reveal(e.name);
                        if (plain == null) continue;
                        if (Set(e.name, plain, e.kind, e.policy, e.note) == null) moved++;
                    }
                }
                File.Delete(keyPath);
                if (File.Exists(filePath)) File.Delete(filePath);
                Debug.Log($"[MetaCodex] vault: {moved} secrets moved from the old dev file into the keystore vault; the dev key is deleted");
            }
            catch (Exception ex) { Debug.LogWarning("[MetaCodex] vault: the old dev file could not be adopted: " + ex.Message); }
            return moved;
        }

        private static string Aad(string name) => "metacodex-vault:v1:" + name;
        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private void Load()
        {
            try
            {
                if (File.Exists(FilePath)) _file = JsonUtility.FromJson<VaultFile>(File.ReadAllText(FilePath)) ?? throw new Exception("empty file");
            }
            catch (Exception ex)
            {
                // never overwrite what could not be read: keep it aside (its sealed values may still open later)
                LoadError = ex.Message;
                try { if (File.Exists(FilePath)) File.Move(FilePath, FilePath + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss")); } catch (Exception) { }
                Debug.LogWarning("[MetaCodex] vault unreadable (kept aside): " + ex.Message);
                _file = new VaultFile();
            }
            if (_file.entries == null) _file.entries = new List<VaultEntry>();
        }

        private void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonUtility.ToJson(_file, true));
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
            Changed?.Invoke();
        }

        public VaultEntry Find(string name) => _file.entries.FirstOrDefault(e => e.name == name);
        public bool Has(string name) => Find(name) != null;

        /// Stores (or replaces) a value. Returns null or the error.
        public string Set(string name, string value, string kind = "api-key", string policy = "ask", string note = "")
        {
            if (!Usable) return Sealer.LastError;
            if (name == null || !NamePattern.IsMatch(name)) return "name: letters, digits, . _ - (max 64)";
            if (string.IsNullOrEmpty(value)) return "empty value";
            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > MaxValueBytes) return "value larger than 16 KB";
            if (Array.IndexOf(Policies, policy) < 0) policy = "ask";
            var sealedValue = Sealer.Seal(bytes, Aad(name));
            Array.Clear(bytes, 0, bytes.Length);
            if (sealedValue == null) return "sealing failed: " + Sealer.LastError;
            var e = Find(name);
            if (e == null) { e = new VaultEntry { name = name, created = Now() }; _file.entries.Add(e); }
            e.kind = kind ?? "api-key";
            e.policy = policy;
            e.note = note ?? "";
            e.value = sealedValue;
            e.updated = Now();
            Save();
            return null;
        }

        /// The plain value for the app itself (no policy check), or null.
        public string Reveal(string name)
        {
            var e = Find(name);
            if (e == null) return null;
            var b = Sealer.Open(e.value, Aad(name));
            if (b == null) return null;
            var s = Encoding.UTF8.GetString(b);
            Array.Clear(b, 0, b.Length);
            return s;
        }

        /// A random key (hex) created inside the app on first use; later calls return the stored one.
        public string Ensure(string name, int bytes, string kind = "key", string policy = "once", string note = "")
        {
            if (Has(name)) return Reveal(name);
            var raw = new byte[Mathf.Clamp(bytes, 16, 64)];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(raw);
            var hex = BitConverter.ToString(raw).Replace("-", "").ToLowerInvariant();
            Array.Clear(raw, 0, raw.Length);
            var err = Set(name, hex, kind, policy, note);
            return err == null ? hex : null;
        }

        public bool Delete(string name)
        {
            var e = Find(name);
            if (e == null) return false;
            _file.entries.Remove(e);
            _deliveredThisSession.Remove(name);
            Save();
            return true;
        }

        public void SetPolicy(string name, string policy)
        {
            var e = Find(name);
            if (e == null || Array.IndexOf(Policies, policy) < 0) return;
            e.policy = policy;
            e.updated = Now();
            Save();
        }

        /// Lets a "once" secret be handed out again (the app restarted MQH, or the user asked).
        public void Rearm(string name = null)
        {
            if (name == null) _deliveredThisSession.Clear(); else _deliveredThisSession.Remove(name);
        }

        public enum Decision { Allow, Deny, Ask }

        /// What the policy says for a request from the userland (does not record anything).
        public Decision Check(string name, out string reason)
        {
            var e = Find(name);
            reason = null;
            if (e == null) { reason = "no such secret"; return Decision.Deny; }
            switch (e.policy)
            {
                case "allow": return Decision.Allow;
                case "deny": reason = "policy deny (app use only)"; return Decision.Deny;
                case "once":
                    if (_deliveredThisSession.Contains(name)) { reason = "already handed out this session (policy once)"; return Decision.Deny; }
                    return Decision.Allow;
                default: return Decision.Ask;
            }
        }

        /// Records the outcome of a request; for allowed ones returns the value.
        public string Deliver(string name, string requester, bool allowed, string decisionLabel = null)
        {
            Audit.Add(new VaultEvent { Time = DateTime.Now, Name = name, Requester = string.IsNullOrEmpty(requester) ? "?" : requester, Decision = decisionLabel ?? (allowed ? "ALLOW" : "DENY") });
            while (Audit.Count > 60) Audit.RemoveAt(0);
            if (!allowed) { Changed?.Invoke(); return null; }
            var e = Find(name);
            if (e == null) return null;
            var v = Reveal(name);
            if (v == null) return null;
            e.uses++;
            e.lastUsed = Now();
            if (e.policy == "once") _deliveredThisSession.Add(name);
            Save();
            return v;
        }
    }
}
