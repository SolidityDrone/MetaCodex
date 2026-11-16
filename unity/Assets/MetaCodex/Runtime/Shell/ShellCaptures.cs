using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace MetaCodex.Shell
{
    /// The image clipboard: PNG captures in one folder of the Linux (/root/.metacodex/captures), newest first, at most
    /// Max (Settings clipboardMax, 20): one more deletes the oldest file. index.json says where each came from.
    /// Copied text (a terminal's selection or link, ShellTerminals) is kept beside them as clip-*.txt files, newest first,
    /// at most Max too: Texts, apart from the pictures (Items), so what takes pictures never meets text.
    public sealed class CaptureClipboard
    {
        [Serializable] public sealed class Item { public string file, panel, panelId, time, kind; public int w, h, chars; public long bytes; }
        [Serializable] private sealed class IndexFile { public List<Item> items = new List<Item>(); public List<Item> texts = new List<Item>(); }

        private readonly Func<string> _dir;
        private readonly Func<int> _max;
        private List<Item> _items, _texts;
        public readonly string LinuxDir;
        public event Action Changed;
        /// The text list changed (Changed is the pictures').
        public event Action TextsChanged;

        /// `dir`: where the files are for the app (asked at first use: the Linux may not be up when this is made).
        public CaptureClipboard(Func<string> dir, string linuxDir, Func<int> max) { _dir = dir; LinuxDir = linuxDir.TrimEnd('/'); _max = max; }

        public string Dir => _dir();
        public int Max => Mathf.Clamp(_max?.Invoke() ?? 20, 1, 500);
        public IReadOnlyList<Item> Items { get { Load(); return _items; } }
        public IReadOnlyList<Item> Texts { get { Load(); return _texts; } }
        public static bool IsText(Item i) => i != null && i.kind == "text";
        public string LinuxPath(Item i) => LinuxDir + "/" + i.file;
        public string HostPath(Item i) => Path.Combine(Dir, i.file);
        public Item Find(string file) { Load(); return _items.FirstOrDefault(x => x.file == file); }

        public Item Add(byte[] png, int w, int h, string panel, string panelId)
        {
            Load();
            Directory.CreateDirectory(Dir);
            var now = DateTime.Now;
            string name = "capture-" + now.ToString("yyyyMMdd-HHmmss-fff") + ".png";
            for (int k = 2; File.Exists(Path.Combine(Dir, name)); k++) name = "capture-" + now.ToString("yyyyMMdd-HHmmss-fff") + "-" + k + ".png";
            File.WriteAllBytes(Path.Combine(Dir, name), png);
            var item = new Item { file = name, panel = panel ?? "", panelId = panelId ?? "", time = now.ToString("o"), w = w, h = h, bytes = png.Length };
            _items.Insert(0, item);
            Trim(save: false);
            Save();
            Changed?.Invoke();
            return item;
        }

        /// Copied text, newest first; the same text twice in a row is kept once.
        public Item AddText(string text, string panel, string panelId)
        {
            Load();
            if (string.IsNullOrEmpty(text)) return null;
            if (_texts.Count > 0 && _texts[0].chars == text.Length && ReadText(_texts[0]) == text) return _texts[0];
            Directory.CreateDirectory(Dir);
            var now = DateTime.Now;
            string name = "clip-" + now.ToString("yyyyMMdd-HHmmss-fff") + ".txt";
            for (int k = 2; File.Exists(Path.Combine(Dir, name)); k++) name = "clip-" + now.ToString("yyyyMMdd-HHmmss-fff") + "-" + k + ".txt";
            File.WriteAllText(Path.Combine(Dir, name), text);
            var item = new Item { file = name, kind = "text", panel = panel ?? "", panelId = panelId ?? "", time = now.ToString("o"), chars = text.Length, bytes = System.Text.Encoding.UTF8.GetByteCount(text) };
            _texts.Insert(0, item);
            TrimTexts();
            Save();
            TextsChanged?.Invoke();
            return item;
        }

        /// A text item's text (null when its file is gone).
        public string ReadText(Item i)
        {
            try { return i != null && File.Exists(HostPath(i)) ? File.ReadAllText(HostPath(i)) : null; }
            catch (Exception) { return null; }
        }

        private int TrimTexts()
        {
            int n = 0;
            while (_texts.Count > Max)
            {
                var old = _texts[_texts.Count - 1];
                _texts.RemoveAt(_texts.Count - 1);
                try { File.Delete(HostPath(old)); } catch (Exception) { }
                n++;
            }
            return n;
        }

        /// Keeps the newest Max (pictures and texts each) and deletes the rest from the disk; returns how many went.
        public int Trim(bool save = true)
        {
            Load();
            int n = 0;
            while (_items.Count > Max)
            {
                var old = _items[_items.Count - 1];
                _items.RemoveAt(_items.Count - 1);
                try { File.Delete(HostPath(old)); } catch (Exception) { }
                n++;
            }
            int nt = TrimTexts();
            if (save && n + nt > 0) { Save(); if (n > 0) Changed?.Invoke(); if (nt > 0) TextsChanged?.Invoke(); }
            return n + nt;
        }

        public bool Delete(string file)
        {
            var it = Find(file);
            if (it == null)
            {
                var tx = _texts.FirstOrDefault(x => x.file == file);
                if (tx == null) return false;
                _texts.Remove(tx);
                try { File.Delete(HostPath(tx)); } catch (Exception) { }
                Save();
                TextsChanged?.Invoke();
                return true;
            }
            _items.Remove(it);
            try { File.Delete(HostPath(it)); } catch (Exception) { }
            Save();
            Changed?.Invoke();
            return true;
        }

        public int Clear()
        {
            Load();
            int n = _items.Count + _texts.Count;
            foreach (var it in _items.Concat(_texts)) { try { File.Delete(HostPath(it)); } catch (Exception) { } }
            _items.Clear();
            _texts.Clear();
            Save();
            Changed?.Invoke();
            TextsChanged?.Invoke();
            return n;
        }

        /// From the disk again: the index, checked against the files (PNGs it does not know are added).
        public void Reload() { _items = null; _texts = null; Load(); }

        private static DateTime When(Item x)
        {
            return DateTime.TryParse(x.time, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : DateTime.MinValue;
        }

        private void Load()
        {
            if (_items != null && _texts != null) return;
            _items = new List<Item>();
            _texts = new List<Item>();
            try
            {
                string dir = Dir, idx = Path.Combine(dir, "index.json");
                if (File.Exists(idx))
                {
                    var index = JsonUtility.FromJson<IndexFile>(File.ReadAllText(idx));
                    _items = index?.items ?? new List<Item>();
                    _texts = index?.texts ?? new List<Item>();
                }
                _items = _items.Where(x => x != null && !string.IsNullOrEmpty(x.file) && File.Exists(Path.Combine(dir, x.file))).ToList();
                _texts = _texts.Where(x => x != null && !string.IsNullOrEmpty(x.file) && File.Exists(Path.Combine(dir, x.file))).ToList();
                foreach (var x in _texts) x.kind = "text";
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir, "*.png"))
                    {
                        string n = Path.GetFileName(f);
                        if (_items.Any(x => x.file == n)) continue;
                        var fi = new FileInfo(f);
                        _items.Add(new Item { file = n, panel = "", panelId = "", time = fi.LastWriteTime.ToString("o"), bytes = fi.Length });
                    }
                    foreach (var f in Directory.GetFiles(dir, "clip-*.txt"))
                    {
                        string n = Path.GetFileName(f);
                        if (_texts.Any(x => x.file == n)) continue;
                        var fi = new FileInfo(f);
                        _texts.Add(new Item { file = n, kind = "text", panel = "", panelId = "", time = fi.LastWriteTime.ToString("o"), bytes = fi.Length, chars = (int)fi.Length });
                    }
                }
                _items = _items.OrderByDescending(When).ToList();
                _texts = _texts.OrderByDescending(When).ToList();
            }
            catch (Exception) { }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string idx = Path.Combine(Dir, "index.json"), tmp = idx + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(new IndexFile { items = _items, texts = _texts ?? new List<Item>() }, true));
                if (File.Exists(idx)) File.Delete(idx);
                File.Move(tmp, idx);
            }
            catch (Exception) { }
        }
    }

    /// Captures (owner 2026-11-09): SELECT on a window keeps a picture of that area in the image clipboard. The window is
    /// drawn alone at 100 % opacity on a solid background, flat, without occlusion: whatever its opacity, nothing of the
    /// room (passthrough), the hands or other windows is in it. CLIPBOARD lists them (wrist, dock, voice); a thread's
    /// + attaches captures or Linux files to the next message, picked in a modal inside the thread panel.
    public sealed partial class MetaCodexShell
    {
        public const string CapturesLinuxDir = "/root/.metacodex/captures";
        /// A layer nothing else uses: a capture draws its window alone.
        public const int CaptureLayer = 31;
        private static readonly Color CaptureBackground = new Color(0.035f, 0.045f, 0.06f, 1f);
        public CaptureClipboard Clipboard { get; private set; }
        private string _clipDirOverride;

        private void InitClipboard()
        {
            Clipboard = new CaptureClipboard(() =>
                _clipDirOverride ?? (Application.isEditor ? Path.GetFullPath(Path.Combine("Temp", "mcx-captures")) : Files.HostPath(CapturesLinuxDir)),
                CapturesLinuxDir, () => Settings.clipboardMax);
            Clipboard.Changed += () => { RefreshClipboardPanel(); if (_amOpen && _amMode == "capture") ShowAttachModalList(); };
            Clipboard.TextsChanged += RefreshClipboardPanel; // text copied from a terminal (ShellTerminals)
        }

        /// Tests: the clipboard in a folder of their own.
        public void SetClipboardDirForTest(string dir) { _clipDirOverride = dir; Clipboard.Reload(); }

        // ------------------------------------------------------------------ capture

        /// Pixel size of a capture: 2600 px per metre of the panel (text stays readable), at most 1600 on a side.
        private static Vector2Int CaptureSize(Rect r)
        {
            const float ppm = 2600f;
            int pw = Mathf.RoundToInt(r.width * ppm), ph = Mathf.RoundToInt(r.height * ppm);
            float big = Mathf.Max(pw, ph);
            if (big > 1600f) { pw = Mathf.RoundToInt(pw * 1600f / big); ph = Mathf.RoundToInt(ph * 1600f / big); }
            return new Vector2Int(Mathf.Max(pw, 16), Mathf.Max(ph, 16));
        }

        /// The SELECT tool's area (panel units) as a picture of the window alone, always opaque; kept in the clipboard.
        /// Synchronous (tests, and devices without async readback): the GPU readback waits for the frame.
        public CaptureClipboard.Item CapturePanelArea(UiPanel p, Rect r)
        {
            if (p == null || r.width < 0.01f || r.height < 0.01f) return null;
            var size = CaptureSize(r);
            var rt = RenderPanelArea(p, r, size.x, size.y);
            if (rt == null) return null;
            var before = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(size.x, size.y, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, size.x, size.y), 0, 0);
            tex.Apply();
            RenderTexture.active = before;
            rt.Release();
            DestroyNow(rt);
            byte[] png = tex.EncodeToPNG();
            DestroyNow(tex);
            return Clipboard.Add(png, size.x, size.y, p.Title, p.Id);
        }

        /// The same without a hitch (the frame rate rule): the render is one quick camera render, the pixels come back
        /// from the GPU asynchronously and the PNG is encoded on a worker thread. `done` runs on the main thread.
        public void CapturePanelAreaAsync(UiPanel p, Rect r, Action<CaptureClipboard.Item> done)
        {
            if (p == null || r.width < 0.01f || r.height < 0.01f) { done?.Invoke(null); return; }
            // edit-mode self-tests have no frame loop to finish a readback: they take the synchronous path
            if (!SystemInfo.supportsAsyncGPUReadback || !Application.isPlaying) { done?.Invoke(CapturePanelArea(p, r)); return; }
            var size = CaptureSize(r);
            var rt = RenderPanelArea(p, r, size.x, size.y);
            if (rt == null) { done?.Invoke(null); return; }
            string title = p.Title, id = p.Id;
            AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, req =>
            {
                byte[] data = null;
                try { if (!req.hasError) data = req.GetData<byte>().ToArray(); }
                finally { rt.Release(); DestroyNow(rt); }
                if (data == null) { done?.Invoke(null); return; }
                System.Threading.Tasks.Task.Run(() =>
                {
                    byte[] png = null;
                    try
                    {
                        for (int i = 3; i < data.Length; i += 4) data[i] = 255; // opaque, whatever the blending left
                        png = ImageConversion.EncodeArrayToPNG(data, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB, (uint)size.x, (uint)size.y);
                    }
                    catch (Exception) { }
                    _main.Enqueue(() => done?.Invoke(png != null ? Clipboard.Add(png, size.x, size.y, title, id) : null));
                });
            });
        }

        /// Draws an area of a panel into a render texture: only that panel (its own layer for the render), flat, its
        /// shapes at full opacity, no real-world occlusion, on CaptureBackground. Everything is put back before returning;
        /// the caller releases the texture.
        public RenderTexture RenderPanelArea(UiPanel p, Rect r, int pw, int ph)
        {
            var layers = new List<(GameObject go, int layer)>();
            foreach (var tr in p.GetComponentsInChildren<Transform>(true)) { layers.Add((tr.gameObject, tr.gameObject.layer)); tr.gameObject.layer = CaptureLayer; }
            float occ = Shader.GetGlobalFloat("_McxOcclusion"), hide = Shader.GetGlobalFloat("_McxUiHide");
            float opacity = shapeMaterial != null ? shapeMaterial.GetFloat("_Opacity") : 1f;
            var camGo = new GameObject("capture camera") { hideFlags = HideFlags.HideAndDontSave };
            RenderTexture rt = null;
            try
            {
                Shader.SetGlobalFloat("_McxOcclusion", 0f);
                Shader.SetGlobalFloat("_McxUiHide", 0f);
                if (shapeMaterial != null) shapeMaterial.SetFloat("_Opacity", 1f);
                var cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.stereoTargetEye = StereoTargetEyeMask.None;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = CaptureBackground; // opaque: the room behind the window never shows
                cam.cullingMask = 1 << CaptureLayer;
                cam.orthographic = true;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 1f;
                rt = new RenderTexture(pw, ph, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                cam.targetTexture = rt;
                // RenderFlat draws the panel at rest (flat, not squashed by a transition): frame it then
                p.RenderFlat(() =>
                {
                    var s = p.transform.lossyScale;
                    cam.orthographicSize = r.height * s.y * 0.5f;
                    cam.aspect = r.width * s.x / Mathf.Max(1e-5f, r.height * s.y);
                    var centre = p.transform.TransformPoint(p.PanelToLocal(r.center, 0f));
                    cam.transform.SetPositionAndRotation(centre - p.transform.forward * 0.3f, p.transform.rotation);
                    cam.Render();
                });
                cam.targetTexture = null;
                return rt;
            }
            catch (Exception ex)
            {
                Log("capture: " + ex.Message);
                if (rt != null) { rt.Release(); DestroyNow(rt); }
                return null;
            }
            finally
            {
                foreach (var (go, layer) in layers) if (go != null) go.layer = layer;
                Shader.SetGlobalFloat("_McxOcclusion", occ);
                Shader.SetGlobalFloat("_McxUiHide", hide);
                if (shapeMaterial != null) shapeMaterial.SetFloat("_Opacity", opacity);
                DestroyNow(camGo);
            }
        }

        private static void DestroyNow(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }

        /// `mcx-ui capture list | clear | dir` (agents read what the user captured).
        public string CaptureCommand(string arg)
        {
            switch ((arg ?? "").Trim())
            {
                case "":
                case "list":
                    return new JArray(Clipboard.Items.Select(i => new JObject
                    {
                        ["path"] = Clipboard.LinuxPath(i), ["from"] = i.panel, ["width"] = i.w, ["height"] = i.h, ["time"] = i.time, ["bytes"] = i.bytes,
                    })).ToString(Newtonsoft.Json.Formatting.None);
                case "clear": return $"cleared {Clipboard.Clear()}";
                case "dir": return CapturesLinuxDir;
                default: return "error: capture list | clear | dir";
            }
        }

        // ------------------------------------------------------------------ CLIPBOARD panel

        private UiList _clipList;
        private UiLabel _clipStatus, _clipInfo, _clipText;
        private Rect _clipPreviewArea;
        private GameObject _clipPreview;
        private Texture2D _clipTex;
        private static Material _capImageMat;

        private static Material CaptureImageMaterial()
        {
            if (_capImageMat != null) return _capImageMat;
            var sh = Resources.Load<Shader>("Shaders/ViewImage") ?? Shader.Find("MetaCodex/ViewImage");
            if (sh != null) _capImageMat = new Material(sh) { name = "capture preview" };
            return _capImageMat;
        }

        private void BuildClipboard()
        {
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("clipboard", "CLIPBOARD · CAPTURES", 0.72f, 0.52f);
            var c = p.Content;
            float leftW = 0.28f;
            var left = new UiStack(new Rect(c.x, c.y, leftW, c.height), t.Gap);
            _clipStatus = p.Add(new UiLabel("", t.SmallSize) { Rect = left.Next(0.022f), Color = t.TextDim });
            _clipList = p.Add(new UiList(0.034f, (item, idx) => ShowClipPreview()) { Rect = left.Rest(), EmptyText = "No captures yet.\nSELECT (wrist or dock), then pinch and drag over any window.\nCOPY on a terminal keeps text here." });
            var rs = new UiStack(new Rect(c.x + leftW + t.Gap * 2, c.y, c.width - leftW - t.Gap * 2, c.height), t.Gap);
            const float keepH = 0.032f;
            _clipPreviewArea = rs.Next(c.height - 0.022f - keepH - t.ButtonHeight - t.Gap * 4);
            // a text item shows its text where a picture shows
            _clipText = p.Add(new UiLabel("", t.SmallSize) { Rect = _clipPreviewArea, Wrap = true, UseMono = true, Align = TextAlignmentOptions.TopLeft, Color = t.Text });
            _clipInfo = p.Add(new UiLabel("", t.SmallSize) { Rect = rs.Next(0.022f), Color = t.TextDim });
            // how many captures it keeps (was DISPLAY's CLIPBOARD SIZE): one more deletes the oldest
            p.Add(new UiSlider("KEEP", 5f, 100f, Settings.clipboardMax, v => { Settings.clipboardMax = Mathf.RoundToInt(v); OnSettingsChanged(); Clipboard?.Trim(); if (Clipboard != null) _clipStatus.Set($"{Clipboard.Items.Count} of {Clipboard.Max} · newest first", t.TextDim); }) { Rect = rs.Next(keepH), Format = "0", LabelWidth = 0.07f });
            AddButtons(p, rs.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("⎘ TO THE THREAD", () =>
                {
                    var it = SelectedClip();
                    if (it == null) { Toast("Pick a capture", "warn"); return; }
                    if (ThreadKey == null) { Toast("Open a thread first (THREADS): it goes with your next message there", "warn"); return; }
                    bool text = CaptureClipboard.IsText(it);
                    AddAttachment(text ? TextClipAttachment(it) : CaptureAttachment(it)); // the focused thread window's next message
                    ShowThreadWindow();
                    Toast(text ? "Text attached to the next message" : "Capture attached to the next message", "ok");
                }),
                ("COPY", () =>
                {
                    var it = SelectedClip();
                    if (it == null) { Toast("Pick a text", "warn"); return; }
                    if (!CaptureClipboard.IsText(it)) { Toast("A picture: ⎘ TO THE THREAD attaches it to a message", "info"); return; }
                    string text = Clipboard.ReadText(it);
                    if (string.IsNullOrEmpty(text)) { Toast("That text is gone", "warn"); return; }
                    if (SystemClipboardForTest) GUIUtility.systemCopyBuffer = text; // PASTE, the system keyboard and WEB take it from here
                    Toast($"Copied {text.Length} characters", "ok");
                }),
                ("DELETE", () => { var it = SelectedClip(); if (it != null) Clipboard.Delete(it.file); }),
                ("CLEAR ALL", () => Modal("CLEAR THE CLIPBOARD", $"Delete all {Clipboard.Items.Count + Clipboard.Texts.Count} captures and texts?", new[] { "DELETE ALL", "CANCEL" }, i => { if (i == 0) Clipboard.Clear(); })),
            }, primary: 0, danger: 3);
            p.Closed += _ => ClearClipPreview();
        }

        private CaptureClipboard.Item SelectedClip() =>
            _clipList != null && _clipList.SelectedIndex >= 0 && _clipList.SelectedIndex < _clipList.Items.Count ? _clipList.Items[_clipList.SelectedIndex].Tag as CaptureClipboard.Item : null;

        public void RefreshClipboardPanel()
        {
            if (_clipList == null || Clipboard == null) return;
            var t = UiTheme.Current;
            var items = Clipboard.Items;
            var texts = Clipboard.Texts;
            _clipStatus.Set(texts.Count == 0 ? $"{items.Count} of {Clipboard.Max} · newest first" : $"{items.Count} pictures · {texts.Count} texts · newest first", t.TextDim);
            // pictures and texts in one list, newest first
            _clipList.SetItems(items.Concat(texts).OrderByDescending(i => i.time, StringComparer.Ordinal).Select(i => CaptureClipboard.IsText(i)
                ? new UiListItem
                {
                    Icon = "☰",
                    IconColor = t.TextAccent,
                    Title = ClipTime(i),
                    Sub = $"{i.chars} characters · {(string.IsNullOrEmpty(i.panel) ? "text" : i.panel)}",
                    Badge = FileService.Size(i.bytes),
                    BadgeColor = t.TextDim,
                    Tag = i,
                }
                : new UiListItem
                {
                    Icon = "▣",
                    IconColor = t.Accent,
                    Title = ClipTime(i),
                    Sub = $"{i.w}×{i.h} · {(string.IsNullOrEmpty(i.panel) ? "capture" : i.panel)}",
                    Badge = FileService.Size(i.bytes),
                    BadgeColor = t.TextDim,
                    Tag = i,
                }), keepScroll: true);
            if (items.Count > 0 && _clipList.SelectedIndex < 0) _clipList.SelectIndex(0, notify: false);
            ShowClipPreview();
        }

        private static string ClipTime(CaptureClipboard.Item i) =>
            DateTime.TryParse(i.time, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToString(d.Date == DateTime.Today ? "HH:mm:ss" : "dd MMM HH:mm") : i.file;

        private void ShowClipPreview()
        {
            if (_clipInfo == null) return;
            var t = UiTheme.Current;
            var it = SelectedClip();
            if (it == null)
            {
                ClearClipPreview();
                _clipText?.Set("");
                _clipInfo.Set("", t.TextDim); // with no captures the list itself says what to do
                return;
            }
            _clipInfo.Set($"{Clipboard.LinuxPath(it)}", t.TextDim);
            if (CaptureClipboard.IsText(it))
            {
                ClearClipPreview();
                string text = Clipboard.ReadText(it) ?? "";
                if (text.Length > 700) text = text.Substring(0, 700) + "…";
                _clipText?.Set("<noparse>" + text.Replace("</noparse>", "‹/noparse>") + "</noparse>");
                return;
            }
            _clipText?.Set("");
            if (!_panels.TryGetValue("clipboard", out var p)) return;
            _clipTex = ShowImage(p, ref _clipPreview, _clipTex, Clipboard.HostPath(it), _clipPreviewArea);
        }

        private void ClearClipPreview()
        {
            if (_clipPreview != null) _clipPreview.SetActive(false);
            if (_clipTex != null) { DestroyNow(_clipTex); _clipTex = null; }
        }

        /// A PNG on a quad over a panel area (aspect kept); returns the new texture (the old one is freed).
        private Texture2D ShowImage(UiPanel p, ref GameObject quad, Texture2D old, string hostPath, Rect area)
        {
            if (old != null) DestroyNow(old);
            if (!File.Exists(hostPath)) { if (quad != null) quad.SetActive(false); return null; }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            if (!tex.LoadImage(File.ReadAllBytes(hostPath))) { DestroyNow(tex); if (quad != null) quad.SetActive(false); return null; }
            if (quad == null)
            {
                quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = "capture image";
                var col = quad.GetComponent<Collider>();
                if (col != null) DestroyNow(col);
                quad.transform.SetParent(p.transform, false);
                var mr0 = quad.GetComponent<MeshRenderer>();
                mr0.sharedMaterial = CaptureImageMaterial();
                mr0.shadowCastingMode = ShadowCastingMode.Off;
            }
            float aspect = tex.width / (float)Mathf.Max(1, tex.height);
            float w = area.width, h = w / aspect;
            if (h > area.height) { h = area.height; w = h * aspect; }
            quad.transform.localPosition = p.PanelToLocal(area.center, -0.0008f);
            quad.transform.localScale = new Vector3(w, h, 1f);
            var mpb = new MaterialPropertyBlock();
            mpb.SetTexture("_MainTex", tex);
            quad.GetComponent<MeshRenderer>().SetPropertyBlock(mpb);
            p.TagRenderer(quad.GetComponent<MeshRenderer>(), Vector3.zero); // comes and goes with the panel's transition
            quad.SetActive(true);
            return tex;
        }

        public GameObject ClipPreviewForTest => _clipPreview;
        public float ShapeOpacityForTest => shapeMaterial != null ? shapeMaterial.GetFloat("_Opacity") : -1f;
        public UiList ClipListForTest => _clipList;

        // ------------------------------------------------------------------ thread attachments

        /// Something going with the next message of a thread: a capture or a Linux file (MQH localImage / localFile).
        public sealed class Attachment { public string Kind, LinuxPath, Name, Mime; public long Size; }

        private readonly Dictionary<string, List<Attachment>> _attach = new Dictionary<string, List<Attachment>>();

        private List<Attachment> AttachList
        {
            get
            {
                string key = ThreadKey; // the open thread, or the draft (its first message takes them)
                if (key == null) return null;
                if (!_attach.TryGetValue(key, out var l)) _attach[key] = l = new List<Attachment>();
                return l;
            }
        }

        public IReadOnlyList<Attachment> AttachmentsForTest => (IReadOnlyList<Attachment>)AttachList ?? new List<Attachment>();

        private Attachment CaptureAttachment(CaptureClipboard.Item it) =>
            new Attachment { Kind = "image", LinuxPath = Clipboard.LinuxPath(it), Name = "capture " + ClipTime(it), Mime = "image/png", Size = it.bytes };

        /// A copied text goes as a text file (its clip-*.txt beside the captures).
        private Attachment TextClipAttachment(CaptureClipboard.Item it) =>
            new Attachment { Kind = "file", LinuxPath = Clipboard.LinuxPath(it), Name = "text " + ClipTime(it), Mime = "text/plain", Size = it.bytes };

        private static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".webp", ".gif" };

        private static Attachment FileAttachment(FileEntry e)
        {
            string ext = Path.GetExtension(e.Name).ToLowerInvariant();
            bool image = ImageExt.Contains(ext);
            return new Attachment { Kind = image ? "image" : "file", LinuxPath = e.LinuxPath, Name = e.Name, Mime = MimeOf(ext), Size = e.Size };
        }

        private static string MimeOf(string ext)
        {
            switch (ext)
            {
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".webp": return "image/webp";
                case ".gif": return "image/gif";
                case ".txt": case ".log": return "text/plain";
                case ".md": return "text/markdown";
                case ".json": return "application/json";
                case ".pdf": return "application/pdf";
                case ".csv": return "text/csv";
                case ".html": case ".htm": return "text/html";
                default: return "application/octet-stream";
            }
        }

        public void AddAttachment(Attachment a)
        {
            var l = AttachList;
            if (l == null || a == null) return;
            if (l.Any(x => x.LinuxPath == a.LinuxPath)) return;
            l.Add(a);
            RefreshAttachRow();
        }

        private void RemoveAttachment(int i)
        {
            var l = AttachList;
            if (l == null || i < 0 || i >= l.Count) return;
            l.RemoveAt(i);
            RefreshAttachRow();
        }

        /// MQH inputs for the attachments (images as localImage, the rest as localFile), on the thread's host.
        public JArray AttachmentInputs(IEnumerable<Attachment> list)
        {
            var arr = new JArray();
            foreach (var a in list ?? Enumerable.Empty<Attachment>())
            {
                var o = a.Kind == "image"
                    ? new JObject { ["type"] = "localImage", ["path"] = a.LinuxPath }
                    : new JObject { ["type"] = "localFile", ["path"] = a.LinuxPath, ["name"] = a.Name, ["sizeBytes"] = a.Size, ["mimeType"] = a.Mime };
                if (!string.IsNullOrEmpty(_hostId)) o["hostId"] = _hostId;
                arr.Add(o);
            }
            return arr;
        }

        private static string AttachmentsOnlyText(IReadOnlyCollection<Attachment> l)
        {
            int img = l.Count(x => x.Kind == "image"), files = l.Count - img;
            var parts = new List<string>();
            if (img > 0) parts.Add(img == 1 ? "the attached image" : $"the {img} attached images");
            if (files > 0) parts.Add(files == 1 ? "the attached file" : $"the {files} attached files");
            return "See " + string.Join(" and ", parts) + ".";
        }

        /// Tests: the body SEND would post now.
        public JObject ThreadSendBodyForTest(string text)
        {
            var l = AttachList ?? new List<Attachment>();
            string msg = string.IsNullOrWhiteSpace(text) && l.Count > 0 ? AttachmentsOnlyText(l) : text?.Trim();
            return MqhClient.SendBody(msg, null, l.Count > 0 ? AttachmentInputs(l) : null);
        }

        /// The row over the composer: the attachments as chips (✕ removes one), or the + menu while it is open.
        private void BuildAttachRow(UiPanel p, Rect row)
        {
            var t = UiTheme.Current;
            float ts = t.SmallSize * 0.98f;
            _attChips.Clear();
            const int maxChips = 4;
            float moreW = 0.07f, cw = (row.width - moreW - t.Gap * maxChips) / maxChips;
            for (int i = 0; i < maxChips; i++)
            {
                int k = i;
                _attChips.Add(p.Add(new UiButton("", () => RemoveAttachment(k)) { Rect = new Rect(row.x + i * (cw + t.Gap), row.y, cw, row.height), TextSize = ts }));
            }
            _attMore = p.Add(new UiButton("", () => Toast(string.Join(" · ", (AttachList ?? new List<Attachment>()).Select(a => a.Name)), "info"), UiButtonStyle.Ghost) { Rect = new Rect(row.xMax - moreW, row.y, moreW, row.height), TextSize = ts });
            _attHint = p.Add(new UiLabel("+ adds files from the Linux or captures to the next message", t.SmallSize) { Rect = row, Color = t.TextDim });
            float mw = (row.width - t.Gap * 2 - 0.05f) / 2f;
            _attMenuFiles = p.Add(new UiButton("⎘ ATTACH FILES", () => OpenAttachModal("files")) { Rect = new Rect(row.x, row.y, mw, row.height), TextSize = ts });
            _attMenuCapture = p.Add(new UiButton("▣ REFERENCE CAPTURE", () => OpenAttachModal("capture")) { Rect = new Rect(row.x + mw + t.Gap, row.y, mw, row.height), TextSize = ts });
            _attMenuClose = p.Add(new UiButton("✕", () => SetAttachMenu(false), UiButtonStyle.Ghost) { Rect = new Rect(row.xMax - 0.05f, row.y, 0.05f, row.height), TextSize = ts });
        }

        private void SetAttachMenu(bool on)
        {
            _attMenuOpen = on;
            _thPlus?.SetSelected(on);
            RefreshAttachRow();
        }

        private void ToggleAttachMenu() { if (_amOpen) CloseAttachModal(); SetAttachMenu(!_attMenuOpen); }

        public void RefreshAttachRow()
        {
            if (_attHint == null) return;
            var l = AttachList ?? new List<Attachment>();
            bool menu = _attMenuOpen;
            _attMenuFiles.SetVisible(menu);
            _attMenuCapture.SetVisible(menu);
            _attMenuClose.SetVisible(menu);
            _attHint.SetVisible(!menu && l.Count == 0);
            for (int i = 0; i < _attChips.Count; i++)
            {
                bool on = !menu && i < l.Count && (i < _attChips.Count - 1 || l.Count <= _attChips.Count);
                _attChips[i].SetVisible(on);
                if (on) _attChips[i].SetText((l[i].Kind == "image" ? "▣ " : "⎘ ") + Cut(l[i].Name, 16) + "  ✕");
            }
            int extra = l.Count - _attChips.Count;
            _attMore.SetVisible(!menu && extra > 0);
            if (extra > 0) _attMore.SetText("+" + extra);
        }

        public bool AttachMenuOpenForTest => _attMenuOpen;
        public void PressPlusForTest() => ToggleAttachMenu();

        // ---- the modal inside the thread panel: Linux files or the clipboard, several picked at once


        public bool AttachModalOpenForTest => _amOpen;
        public UiList AttachModalListForTest => _amList;

        private void BuildAttachModal(UiPanel p, Rect area)
        {
            var t = UiTheme.Current;
            _amElements.Clear();
            T Add<T>(T e) where T : UiElement { p.Add(e); _amElements.Add(e); e.SetVisible(false); return e; }
            float leftW = area.width * 0.56f;
            var top = new Rect(area.x, area.y, area.width, 0.024f);
            _amTitle = Add(new UiLabel("", t.SmallSize * 1.1f) { Rect = top, UseDisplay = true, Color = t.TextAccent });
            float listY = area.y + 0.024f + t.Gap, btnY = area.yMax - t.ButtonHeight;
            _amList = Add(new UiList(0.032f, (item, idx) => AttachModalPicked(item)) { Rect = new Rect(area.x, listY, leftW, btnY - t.Gap - listY) });
            var right = new Rect(area.x + leftW + t.Gap * 2, listY, area.width - leftW - t.Gap * 2, btnY - t.Gap - listY);
            _amPreviewArea = new Rect(right.x, right.y, right.width, right.height - 0.05f);
            _amInfo = Add(new UiLabel("", t.SmallSize) { Rect = new Rect(right.x, right.yMax - 0.048f, right.width, 0.048f), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
            float bw = (area.width - t.Gap * 2) / 3f;
            _amUp = Add(new UiButton("▲ UP", () => { _amDir = FileService.Parent(_amDir); ShowAttachModalList(); }) { Rect = new Rect(area.x, btnY, bw, t.ButtonHeight) });
            _amAttach = Add(new UiButton("ATTACH", ConfirmAttachModal, UiButtonStyle.Primary) { Rect = new Rect(area.x + bw + t.Gap, btnY, bw, t.ButtonHeight) });
            _amCancel = Add(new UiButton("CANCEL", CloseAttachModal) { Rect = new Rect(area.x + (bw + t.Gap) * 2, btnY, bw, t.ButtonHeight) });
        }

        public void OpenAttachModal(string mode)
        {
            if (ThreadKey == null) { Toast("Open a thread first", "warn"); return; }
            ResetComposerHeight(); // the modal is laid out for the message box at one row (ShellThreadComposer)
            _amMode = mode;
            _amPicked.Clear();
            if (mode == "files") _amDir = (string)_threadInfo?["environmentPath"] ?? (_threadOpen == null ? _draft?.ProjectPath : null) ?? _amDir ?? "/root";
            _amOpen = true;
            _attMenuOpen = false;
            _thPlus?.SetSelected(false);
            // the modal covers the timeline: its text, scroll buttons and the approval row step back while it is open
            _thTimeline?.SetVisible(false);
            _tlUp?.SetVisible(false);
            _tlDown?.SetVisible(false);
            _thAsk?.SetVisible(false);
            foreach (var b in _thAskButtons) b.SetVisible(false);
            foreach (var e in _amElements) e.SetVisible(true);
            _amUp.SetVisible(mode == "files");
            RefreshAttachRow();
            ShowAttachModalList();
        }

        public void CloseAttachModal()
        {
            if (!_amOpen) return;
            _amOpen = false;
            foreach (var e in _amElements) e.SetVisible(false);
            if (_amPreview != null) _amPreview.SetActive(false);
            if (_amTex != null) { DestroyNow(_amTex); _amTex = null; }
            _thTimeline?.SetVisible(true);
            _tlUp?.SetVisible(true);
            _tlDown?.SetVisible(true);
            SetAskVisible(_askShown);
            if (_askShown) ShowAsk();
            RenderTimeline();
            RefreshAttachRow();
            FitComposerToText(); // the message box fits its text again (ShellThreadComposer)
        }

        private void ShowAttachModalList()
        {
            if (_amList == null) return;
            var t = UiTheme.Current;
            var items = new List<UiListItem>();
            if (_amMode == "capture")
            {
                _amTitle.Set($"REFERENCE CAPTURE · {Clipboard.Items.Count} in the clipboard · tap to pick");
                foreach (var i in Clipboard.Items)
                {
                    string key = Clipboard.LinuxPath(i);
                    bool on = _amPicked.Contains(key);
                    items.Add(new UiListItem { Icon = on ? "☑" : "▣", IconColor = on ? t.Ok : t.Accent, Title = ClipTime(i), Sub = $"{i.w}×{i.h} · {i.panel}", Badge = on ? "✓" : "", BadgeColor = t.Ok, Tag = i });
                }
                if (items.Count == 0) _amInfo.Set("The clipboard is empty: SELECT (wrist or dock), then pinch and drag over a window.", t.TextDim);
            }
            else
            {
                _amTitle.Set("ATTACH FILES · " + Cut(_amDir, 48));
                var list = Files.List(_amDir, out string err);
                if (err != null) _amInfo.Set("⚠ " + err, t.Danger);
                foreach (var e in list)
                {
                    bool on = _amPicked.Contains(e.LinuxPath);
                    items.Add(new UiListItem
                    {
                        Icon = e.IsDir ? "▸" : on ? "☑" : "·",
                        IconColor = e.IsDir ? t.Accent : on ? t.Ok : t.TextDim,
                        Title = e.Name + (e.IsDir ? "/" : ""),
                        TitleColor = e.Name.StartsWith(".") ? t.TextDim : (Color?)null,
                        Sub = e.IsDir ? "" : FileService.Size(e.Size),
                        Badge = on ? "✓" : "",
                        BadgeColor = t.Ok,
                        Tag = e,
                    });
                }
            }
            _amList.SetItems(items, keepScroll: true);
            _amAttach.SetText(_amPicked.Count > 0 ? $"ATTACH ({_amPicked.Count})" : "ATTACH");
        }

        private void AttachModalPicked(UiListItem item)
        {
            var t = UiTheme.Current;
            if (item?.Tag is FileEntry e)
            {
                if (e.IsDir) { _amDir = e.LinuxPath; ShowAttachModalList(); return; }
                if (!_amPicked.Remove(e.LinuxPath)) _amPicked.Add(e.LinuxPath);
                _amInfo.Set($"{e.LinuxPath}\n{FileService.Size(e.Size)} · {e.Modified:dd MMM HH:mm}", t.TextDim);
                var tp = ThreadPanel; // this window's panel (the modal is drawn inside it)
                if (tp != null && ImageExt.Contains(Path.GetExtension(e.Name).ToLowerInvariant()))
                {
                    var prev = _amPreview;
                    _amTex = ShowImage(tp, ref prev, _amTex, Files.HostPath(e.LinuxPath), _amPreviewArea);
                    _amPreview = prev;
                }
                else if (_amPreview != null) _amPreview.SetActive(false);
            }
            else if (item?.Tag is CaptureClipboard.Item c)
            {
                string key = Clipboard.LinuxPath(c);
                if (!_amPicked.Remove(key)) _amPicked.Add(key);
                _amInfo.Set(key, t.TextDim);
                var tp = ThreadPanel;
                if (tp != null)
                {
                    var prev = _amPreview;
                    _amTex = ShowImage(tp, ref prev, _amTex, Clipboard.HostPath(c), _amPreviewArea);
                    _amPreview = prev;
                }
            }
            ShowAttachModalList();
        }

        /// Tests: tap a row of the modal (by its title).
        public void PickInAttachModalForTest(string title)
        {
            var it = _amList.Items.FirstOrDefault(x => x.Title == title);
            if (it != null) AttachModalPicked(it);
        }

        /// Tests: tap the n-th row of the modal (captures taken within one second share their time).
        public void PickInAttachModalForTest(int index) { if (index >= 0 && index < _amList.Items.Count) AttachModalPicked(_amList.Items[index]); }

        private void ConfirmAttachModal()
        {
            if (_amPicked.Count == 0) { Toast("Tap what to attach first", "warn"); return; }
            if (_amMode == "capture")
                foreach (var i in Clipboard.Items.Where(x => _amPicked.Contains(Clipboard.LinuxPath(x))).Reverse()) AddAttachment(CaptureAttachment(i));
            else
            {
                foreach (var path in _amPicked.ToList())
                {
                    var host = Files.HostPath(path);
                    var fi = new FileInfo(host);
                    AddAttachment(FileAttachment(new FileEntry { Name = Path.GetFileName(path), LinuxPath = path, Size = fi.Exists ? fi.Length : 0 }));
                }
            }
            int n = _amPicked.Count;
            CloseAttachModal();
            Toast($"{n} attached to the next message", "ok");
        }

        public void ConfirmAttachModalForTest() => ConfirmAttachModal();
        public void SetAttachDirForTest(string linuxDir) { _amDir = linuxDir; ShowAttachModalList(); }
    }
}
