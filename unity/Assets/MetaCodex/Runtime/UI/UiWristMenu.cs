using System;
using System.Collections.Generic;
using MetaCodex.Hands;
using TMPro;
using UnityEngine;

namespace MetaCodex.UI
{
    /// The main menu on the wrist (Settings > DISPLAY > MAIN MENU: WRIST): a holographic instrument projected from the
    /// back of the left wrist when the user looks at it as at a watch (palm turned away, so it never meets the Horizon OS
    /// palm-up gesture). Folded, it is a watch face: a hexagon of glass with the time, sixty ticks and a seconds hand.
    /// Touch it with the right index fingertip and it unfolds: a shock ring runs out, an inner ring of six tiles and an
    /// outer ring of twelve swing in from opposite sides (each draws its line icon with a scan line, then types its
    /// name), under a turning bezel, arcs and a radar sweep. Touching a tile runs it with a flash, a ring and sparks.
    /// Nothing pops: the face and the rings each follow one timeline, played back from where they are when the user
    /// changes their mind (owner 2026-11-09: "never appear / disappear instantly, always transition back or forth").
    /// Stable on the wrist (owner 2026-11-09: "when i try to click stuff on the left side, the radial either gets far
    /// away or it disappears"): it follows the wrist through a 1-euro filter (still when the wrist is still, no lag when
    /// it moves); while the right hand is at it (or over the left hand) it holds still where it is and never closes,
    /// since that hand hides the left one from the cameras and the left hand's pose jumps or is lost; a jump of the
    /// tracked wrist (more than 8 cm at once) is ignored until it persists; a lost left hand keeps it 1.5 s, a wrist
    /// turned away 0.45 s. A press is the fingertip touching a tile: within 5 mm of its surface (the finger's pad meets it
    /// before the tracked tip does), steady on that tile, at most 0.2 s after the touch began; a slide across the surface
    /// or through the face on the way to the left side presses nothing. No ring round the fingertip (owner: "a halo
    /// around my finger").
    /// It floats in front of the wrist, towards the eyes (TowardEyes), and writes the depth of its glass (WristDepth),
    /// so the hand that carries it stays behind it; a fingertip in front of it still shows (0.7.5).
    /// On the headset it is built in small steps, a frame each (BuildStaged), with its icon atlas drawn by a background job;
    /// until Ready (and Built) it is out of sight and does nothing.
    public sealed class UiWristMenu : MonoBehaviour
    {
        /// One tile. Locked: drawn dimmed while it returns true (the first start's setup, ShellSetupLocks); a touch still runs
        /// Act, which says why it waits.
        public sealed class Item { public string Glyph, Label, Icon; public Action Act; public Func<bool> Lit, Locked; public Color LitTint; }

        public readonly List<Item> Inner = new List<Item>(), Outer = new List<Item>();
        /// A locked tile's colour: dim steel (its name stays, fainter).
        private static readonly Color LockedTint = new Color(0.30f, 0.38f, 0.46f, 1f);
        /// The hands: left carries the menu, the right index fingertip presses.
        public Func<Handedness, (bool ok, HandFrame frame)> Hand;
        public Transform Head;
        public UiTheme Theme;
        /// Overall size (1 = 15 cm across the outer ring).
        public float Scale = 0.85f;
        /// Where it floats: this far off the back of the wrist, and this far from the wrist towards the eyes (metres).
        public float OffBack = 0.040f, TowardEyes = 0.065f;
        /// Writes the menu's depth so the hand cut-outs keep the carrying hand behind it (tests turn it off to compare).
        public bool DepthWrite = true;
        /// Seconds: the face appears / goes, the rings unfold / fold.
        public float AppearTime = 0.45f, VanishTime = 0.34f, BloomTime = 0.62f, FoldTime = 0.42f;
        /// Seconds it stays when the wrist is turned away, and when the left hand is lost (covered by the other one).
        public float TurnAwayGrace = 0.45f, LostGrace = 1.5f;

        public bool Visible => _present > 0.01f || _bloom > 0.01f;
        public bool Expanded => _expandTarget;
        public float Shown01 => _present;
        public float Expand01 => _bloom;
        /// What the fingertip is over: -2 the face, 0.. a tile (inner ring first, then outer), -1 nothing.
        public int Hover { get; private set; } = -1;
        /// The right hand is at the menu (or over the left hand): it holds still and does not close.
        public bool Engaged { get; private set; }
        public event Action<int> Pressed;
        /// True when every piece is built (Build, or BuildStaged some frames later). Until then the menu is out of sight,
        /// Tick does nothing and no touch is seen.
        public bool Ready => _ready;
        /// Raised once, on the main thread, when the menu becomes Ready: some frames after BuildStaged, or at the end of Build.
        public event Action Built;
        /// How many frames (steps) the build took: the staged build does one step a frame.
        public int BuildFrames { get; private set; }
        /// PANEL OPACITY (the value the panels get, MetaCodexShell.PanelOpacity): scales the alpha of the glass, the tiles
        /// and the watch face (WristArt `_Opacity`). Lines, icons and sparks keep their brightness; WristDepth does not change.
        public float GlassOpacity
        {
            get => _glassOpacity;
            set
            {
                _glassOpacity = Mathf.Clamp01(value);
                if (_mat != null) _mat.SetFloat("_Opacity", _glassOpacity);
            }
        }

        // geometry, metres before Scale: the face, the two rings of tiles, the bezel and the arcs
        private const float FaceR = 0.0205f, CoreHit = 0.0175f;
        private const float InR0 = 0.0236f, InR1 = 0.0470f, OutR0 = 0.0500f, OutR1 = 0.0690f, MidRingR = 0.0485f;
        private const float BezelR0 = 0.0722f, BezelR1 = 0.0740f, ArcR = 0.0790f, ArcR2 = 0.0822f;
        private const float InGapDeg = 3.6f, OutGapDeg = 2.8f, Corner = 0.0016f;
        private const float InStart = 90f, OutStart = 75f; // the first tile's centre (degrees, counter-clockwise from +x)
        private const float JumpDist = 0.08f;

        private Mesh _mesh;
        private Material _mat, _sparkMat;
        private MeshRenderer _mr, _depthMr;
        private float _glassOpacity = 1f;
        private ParticleSystem _burst, _motes;
        private TextMeshPro _faceText, _hint;
        private readonly List<TextMeshPro> _labels = new List<TextMeshPro>();
        private Vector4[] _iconRect = new Vector4[0];
        private float[] _hoverAmt = new float[0], _flash = new float[0];
        private bool _expandTarget, _wantShow, _posed, _armed, _dismissing, _emitterOk, _tipOk;
        private float _present, _bloom, _watchFor, _awayFor, _idle, _cooldownUntil, _time, _engagedUntil, _jumpFor, _coreHover;
        private int _hoverPart = -1, _touchPart = -1;
        private float _hoverSince, _touchSince, _faceTyped = 1f;
        private bool _touching;
        /// Where a touch counts, menu-local metres before Scale (+z is away from the eyes, the surface is z = 0): a touch
        /// is the tracked fingertip within TouchZ of the surface (the finger's pad meets the hologram before the tracked tip
        /// point does; 0.7.7 needed the tip through the surface, owner: "I need to pass my finger through more than I
        /// expected"), armed again once the tip is ArmZ out.
        public const float TouchZ = -0.006f, ArmZ = -0.016f;
        private float _wave = 1f;
        private Vector2 _waveCenter;
        private Vector3 _anchor, _emitterWorld, _tipLocal, _tipPrev;
        private Quaternion _rot = Quaternion.identity;
        private OneEuro3 _filter;
        private string _faceShown = "";
        private readonly List<Vector3> _v = new List<Vector3>();
        private readonly List<Color> _c = new List<Color>();
        private readonly List<Vector2> _uv = new List<Vector2>();
        private readonly List<Vector4> _info = new List<Vector4>(), _extra = new List<Vector4>();
        private readonly List<int> _tri = new List<int>();

        // the build, in steps (BuildStaged: one a frame; Build: all of them now). Nothing draws or reacts before _ready.
        private const int StepAtlas = 0, StepGlass = 1, StepMesh = 2, StepLabels = 3, StepBurst = 4, StepMotes = 5, StepFinish = 6;
        /// A staged step makes at most this many labels, and stops after this many milliseconds (one is always made).
        private const int LabelsPerStep = 3;
        private const double StepBudgetMs = 6.0;
        private bool _ready, _staged, _failed;
        private int _step, _itemsVersion, _labelsVersion;
        private Vector4 _closeRect; // the atlas rectangle of the face's close icon (z = 0: none, nothing drawn)
        // kept between frames, so that Rebuild (every frame while the menu is visible) allocates nothing
        private readonly Vector3[] _beamCorners = new Vector3[6];
        private static readonly (float start, float len)[] ArcsOuter = { (20f, 64f), (128f, 36f), (214f, 92f) };
        private static readonly (float start, float len)[] ArcsInner = { (60f, 22f), (250f, 30f), (318f, 12f) };
        private int _timeMinute = -1;
        private string _timeText = "";

        // ------------------------------------------------------------------ set up

        public static UiWristMenu Create(Transform parent, UiTheme theme)
        {
            var go = new GameObject("wrist menu");
            go.transform.SetParent(parent, false);
            var m = go.AddComponent<UiWristMenu>();
            m.Theme = theme;
            return m;
        }

        /// Call after filling Inner / Outer: builds everything now, in one call (the Editor self-tests, which have no frame
        /// loop); on the headset the shell uses BuildStaged.
        /// Why the start spent 3 s here (Quest 3S log, 2026-11-15, `wrist 3060`): this drew the icon atlas on the main thread
        /// (768 x 384 texels of signed distance, about 2.7 million closure calls, slow in IL2CPP) and made 13 labels, 2 particle
        /// systems and 3 shaders in one frame, although the DOCK is the default main menu. Now the atlas is drawn by a background
        /// job (one for all wrist menus: UiIconAtlas.Texture is not used at run time), BuildStaged does the rest in small steps,
        /// one a frame, and the shell makes the menu only when WRIST is the main menu (ShellWrist). This method runs the same
        /// steps in a loop and joins the job.
        public void Build()
        {
            if (_ready) return;
            for (int guard = 0; !_ready; guard++)
            {
                if (guard > 1000) throw new InvalidOperationException("wrist menu: the build did not finish");
                BuildStep(true);
            }
        }

        /// Builds in small steps instead, one a frame in this component's own Update (each one well under 10 ms on the
        /// headset): the atlas job, the shaders and materials, the mesh and the renderers, the labels (three at a time), the
        /// burst particles, the motes, then the texture once the job is done. Ready and Built say when it is over. Call after
        /// filling Inner / Outer (RebuildItems may follow at any time).
        public void BuildStaged()
        {
            if (_ready || _staged) return;
            _staged = true;
        }

        /// One step of the build. sync: no limit on the labels, and the atlas job is waited for (Build); else a frame at a time.
        private void BuildStep(bool sync)
        {
            BuildFrames++;
            switch (_step)
            {
                case StepAtlas: // the long one goes first: it runs beside all the other steps
                    StartAtlas();
                    _step = StepGlass;
                    break;
                case StepGlass:
                    MakeGlass();
                    _step = StepMesh;
                    break;
                case StepMesh:
                    MakeMesh();
                    _step = StepLabels;
                    break;
                case StepLabels:
                    if (_labels.Count == 0) _labelsVersion = _itemsVersion; // made from the lists as they are now
                    MakeLabels(sync ? int.MaxValue : LabelsPerStep, sync ? 0.0 : StepBudgetMs);
                    if (_faceText != null && _hint != null && _labels.Count >= Inner.Count + Outer.Count) _step = StepBurst;
                    break;
                case StepBurst:
                    if (_sparkMat != null && _burst == null) _burst = Sparks("sparks", false);
                    _step = StepMotes;
                    break;
                case StepMotes:
                    if (_sparkMat != null && _motes == null) _motes = Sparks("motes", true);
                    _step = StepFinish;
                    break;
                default:
                    Finish(sync);
                    break;
            }
        }

        private void MakeGlass()
        {
            var sh = Resources.Load<Shader>("Shaders/WristArt") ?? Shader.Find("MetaCodex/WristArt");
            var ssh = Resources.Load<Shader>("Shaders/WristSpark") ?? Shader.Find("MetaCodex/WristSpark");
            _mat = new Material(sh) { name = "wrist menu" };
            _mat.SetFloat("_Opacity", _glassOpacity);
            _sparkMat = ssh != null ? new Material(ssh) { name = "wrist sparks" } : null;
        }

        private void MakeMesh()
        {
            _mesh = new Mesh { name = "wrist menu" };
            _mesh.MarkDynamic();
            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _mr = gameObject.AddComponent<MeshRenderer>();
            _mr.sharedMaterial = _mat;
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _mr.receiveShadows = false;
            _mr.enabled = false; // out of sight until it is Ready and shown (SetVisible)
            // the depth of the glass, after its light and before the hands (same mesh, a second renderer: its own queue)
            var dsh = Resources.Load<Shader>("Shaders/WristDepth") ?? Shader.Find("MetaCodex/WristDepth");
            if (dsh != null)
            {
                var dgo = new GameObject("wrist menu depth");
                dgo.transform.SetParent(transform, false);
                dgo.AddComponent<MeshFilter>().sharedMesh = _mesh;
                _depthMr = dgo.AddComponent<MeshRenderer>();
                _depthMr.sharedMaterial = new Material(dsh) { name = "wrist menu depth" };
                _depthMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _depthMr.receiveShadows = false;
                _depthMr.enabled = false;
            }
        }

        /// Makes the labels not made yet (the face, the hint, then one per tile): at most `max`, and, with a budget, no more
        /// once that many milliseconds are used (one is always made). The staged build keeps each frame small with it.
        private void MakeLabels(int max, double budgetMs)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int made = 0; made < max; made++)
            {
                if (made > 0 && budgetMs > 0.0 && (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency > budgetMs) break;
                if (_faceText == null)
                {
                    _faceText = Label("face", "", 0.0056f, Theme?.Mono ?? Theme?.Display);
                    _faceText.characterSpacing = 1f;
                    HideUntilShown(_faceText);
                }
                else if (_hint == null)
                {
                    _hint = Label("hint", "MENU", 0.0032f, Theme?.Display);
                    HideUntilShown(_hint);
                }
                else if (_labels.Count < Inner.Count + Outer.Count)
                {
                    int i = _labels.Count;
                    var it = ItemAt(i);
                    var l = Label("tile " + it.Label, it.Label, i < Inner.Count ? 0.0034f : 0.0030f, Theme?.Display);
                    HideUntilShown(l);
                    _labels.Add(l);
                }
                else break;
            }
        }

        /// A label made while the menu is out of sight starts off (SetVisible turns them on and off with the menu).
        private void HideUntilShown(TextMeshPro l)
        {
            if (l != null && (_mr == null || !_mr.enabled)) l.gameObject.SetActive(false);
        }

        private void DestroyTileLabels()
        {
            foreach (var l in _labels) if (l != null) KillObject(l.gameObject);
            _labels.Clear();
        }

        private static void KillObject(GameObject go)
        {
            go.SetActive(false);
            if (Application.isPlaying) Destroy(go); else DestroyImmediate(go);
        }

        private void ResizeItemArrays()
        {
            int n = Inner.Count + Outer.Count;
            _hoverAmt = new float[n];
            _flash = new float[n];
            _iconRect = new Vector4[n];
        }

        /// Each tile's icon rectangle (and the face's close icon) from the shared atlas table. An icon the atlas does not
        /// have shows "mark"; with no atlas (its job failed) every rectangle is empty and no icon is drawn.
        private void FillIconRects()
        {
            bool have = _atlasTex != null;
            for (int i = 0; i < _iconRect.Length; i++)
            {
                _iconRect[i] = default;
                if (have && !TryRect(ItemAt(i).Icon, out _iconRect[i])) TryRect("mark", out _iconRect[i]);
            }
            _closeRect = default;
            if (have) TryRect("close", out _closeRect);
        }

        /// The last step: waits for the icon atlas (a frame at a time; Build joins the job), gives it to the material, sets the
        /// icon rectangles, and goes Ready. Labels made from lists that changed since (RebuildItems before Ready) are made again.
        private void Finish(bool sync)
        {
            if (_labelsVersion != _itemsVersion)
            {
                DestroyTileLabels();
                _step = StepLabels;
                return;
            }
            if (!AtlasFinished(sync)) return;
            if (_atlasTex != null) _mat.SetTexture("_IconAtlas", _atlasTex);
            _mat.SetFloat("_IconRange", UiIconAtlas.Range);
            ResizeItemArrays();
            FillIconRects();
            _ready = true;
            SetVisible(false);
            try { Built?.Invoke(); }
            catch (Exception ex) { Debug.LogWarning("[MetaCodex] wrist menu: Built handler: " + ex.Message); }
        }

        /// The item lists changed (a tile came or went in Inner / Outer): makes the tile labels again from them, and sizes the
        /// per-tile arrays and icon rectangles to match. The face, hint, mesh, materials, particles and atlas stay. Cheap
        /// enough to call whenever the table changes. Before Ready it only notes that the lists changed: the staged build
        /// uses the new lists when it makes the labels, and makes them again at the end if they changed after.
        public void RebuildItems()
        {
            if (!_ready) { _itemsVersion++; return; }
            DestroyTileLabels();
            MakeLabels(int.MaxValue, 0.0);
            ResizeItemArrays();
            FillIconRects();
            Hover = -1;
            _hoverPart = -1;
            _touchPart = -1;
        }

        // ------------------------------------------------------------------ the icon atlas (one for all wrist menus)

        // UiIconAtlas.Texture draws its atlas on the main thread at first use (a 3 s freeze at the start on the headset). The
        // wrist menu keeps its own copy instead: the same cells, the same texel loop, run by a background job; the main thread
        // only makes the Texture2D from the finished bytes. UiIconAtlas is only read: its names on the main thread, its
        // Distance (pure) on the worker.
        private const int AtlasIdle = 0, AtlasRunning = 1, AtlasDone = 2, AtlasFailed = 3;
        private static volatile int _atlasState;                 // written by the job last, after the bytes
        private static byte[] _atlasPx;                          // the finished bytes (R8, rows from the bottom)
        private static Texture2D _atlasTex;
        private static System.Threading.Thread _atlasJob;
        private static Vector4[] _atlasRects;                    // by index in the atlas order
        private static readonly Dictionary<string, int> AtlasIndex = new Dictionary<string, int>();

        /// Main thread: the icon names and their rectangles (the formula of UiIconAtlas.TryGet), then the job.
        private static void StartAtlas()
        {
            if (_atlasState != AtlasIdle) return;
            _atlasState = AtlasRunning;
            try
            {
                var names = new List<string>(UiIconAtlas.Names); // this also runs UiIconAtlas's static initialiser, here
                int max = UiIconAtlas.Cols * UiIconAtlas.Rows;
                if (names.Count > max) names.RemoveRange(max, names.Count - max);
                AtlasIndex.Clear();
                _atlasRects = new Vector4[names.Count];
                for (int i = 0; i < names.Count; i++)
                {
                    AtlasIndex[names[i]] = i;
                    _atlasRects[i] = new Vector4((i % UiIconAtlas.Cols) / (float)UiIconAtlas.Cols, 1f - (i / UiIconAtlas.Cols + 1) / (float)UiIconAtlas.Rows,
                        1f / UiIconAtlas.Cols, 1f / UiIconAtlas.Rows);
                }
                _atlasJob = new System.Threading.Thread(() => AtlasJob(names))
                {
                    Name = "mcx wrist icons", IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal,
                };
                _atlasJob.Start();
            }
            catch (Exception ex)
            {
                _atlasState = AtlasFailed;
                Debug.LogWarning("[MetaCodex] wrist menu: icon atlas failed: " + ex.Message);
            }
        }

        /// The worker: UiIconAtlas.Ensure's loop, byte for byte (cell layout, rows from the bottom, the same formula).
        private static void AtlasJob(List<string> names)
        {
            try
            {
                const int cell = UiIconAtlas.Cell, cols = UiIconAtlas.Cols, rows = UiIconAtlas.Rows;
                const float extent = UiIconAtlas.Extent, range = UiIconAtlas.Range;
                int w = cell * cols, h = cell * rows;
                var px = new byte[w * h];
                for (int i = 0; i < names.Count; i++)
                {
                    string iconName = names[i];
                    int cx = (i % cols) * cell, cy = (rows - 1 - i / cols) * cell; // row 0 at the top (uv y down the rows)
                    for (int y = 0; y < cell; y++)
                        for (int x = 0; x < cell; x++)
                        {
                            var p = new Vector2(((x + 0.5f) / cell * 2f - 1f) * extent, ((y + 0.5f) / cell * 2f - 1f) * extent);
                            float d = UiIconAtlas.Distance(iconName, p);
                            px[(cy + y) * w + cx + x] = (byte)Mathf.RoundToInt(Mathf.Clamp01(0.5f - d / (2f * range)) * 255f);
                        }
                }
                _atlasPx = px;
                _atlasState = AtlasDone; // the bytes first: the main thread reads them only after it sees this
            }
            catch (Exception ex)
            {
                _atlasState = AtlasFailed;
                Debug.LogWarning("[MetaCodex] wrist menu: icon atlas failed: " + ex.Message);
            }
        }

        /// Main thread: true once the atlas is settled (the texture is made, or there is none: the job failed). False while the
        /// job still runs; sync waits for it.
        private static bool AtlasFinished(bool sync)
        {
            StartAtlas();
            if (_atlasState == AtlasRunning)
            {
                if (!sync) return false;
                if (_atlasJob != null) _atlasJob.Join();
            }
            if (_atlasState == AtlasDone && _atlasTex == null && _atlasPx != null) MakeAtlasTexture();
            return true;
        }

        /// Main thread, once: the same texture as UiIconAtlas's (R8, linear, no mips, clamped, bilinear, anisotropy 2), GPU only.
        private static void MakeAtlasTexture()
        {
            try
            {
                var tex = new Texture2D(UiIconAtlas.Cell * UiIconAtlas.Cols, UiIconAtlas.Cell * UiIconAtlas.Rows, TextureFormat.R8, false, true)
                {
                    name = "mcx wrist icon atlas", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, anisoLevel = 2,
                    hideFlags = HideFlags.DontSave,
                };
                tex.LoadRawTextureData(_atlasPx);
                tex.Apply(false, true);
                _atlasTex = tex;
            }
            catch (Exception ex)
            {
                _atlasState = AtlasFailed;
                Debug.LogWarning("[MetaCodex] wrist menu: icon atlas failed: " + ex.Message);
            }
        }

        /// An icon's rectangle in the atlas (uv offset in xy, size in zw); false when there is none by that name.
        private static bool TryRect(string iconName, out Vector4 rect)
        {
            rect = default;
            int index = 0;
            if (_atlasRects == null || string.IsNullOrEmpty(iconName) || !AtlasIndex.TryGetValue(iconName, out index)) return false;
            rect = _atlasRects[index];
            return true;
        }

        private TextMeshPro Label(string name, string text, float lineHeight, TMP_FontAsset font)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var t = go.AddComponent<TextMeshPro>();
            if (font != null) { t.font = font; t.fontSharedMaterial = font.material; }
            t.fontSize = (Theme ?? UiTheme.Current).FontSizeFor(lineHeight) * 1.12f;
            t.characterSpacing = 6f;
            t.alignment = TextAlignmentOptions.Center;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.richText = false;
            t.rectTransform.sizeDelta = new Vector2(0.03f, 0.012f);
            t.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            t.text = text;
            t.color = new Color(1f, 1f, 1f, 0f);
            var r = go.GetComponent<MeshRenderer>();
            if (r != null) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return t;
        }

        private ParticleSystem Sparks(string name, bool motes)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = motes;
            main.maxParticles = motes ? 20 : 72;
            main.simulationSpace = motes ? ParticleSystemSimulationSpace.Local : ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.startLifetime = motes ? new ParticleSystem.MinMaxCurve(1.0f, 1.8f) : new ParticleSystem.MinMaxCurve(0.22f, 0.5f);
            main.startSpeed = motes ? new ParticleSystem.MinMaxCurve(0.003f, 0.009f) : new ParticleSystem.MinMaxCurve(0.05f, 0.16f);
            main.startSize = motes ? new ParticleSystem.MinMaxCurve(0.0006f, 0.0013f) : new ParticleSystem.MinMaxCurve(0.0012f, 0.0034f);
            var accent = (Theme ?? UiTheme.Current).Accent;
            main.startColor = new ParticleSystem.MinMaxGradient(accent, Color.Lerp(accent, Color.white, 0.6f));
            var em = ps.emission;
            em.enabled = motes;
            if (motes) em.rateOverTime = 5f;
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = motes ? ParticleSystemShapeType.Circle : ParticleSystemShapeType.Sphere;
            shape.radius = motes ? FaceR * 1.1f : 0.003f;
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(motes ? 0.5f : 1f, 0.12f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.15f));
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = _sparkMat;
            r.renderMode = motes ? ParticleSystemRenderMode.Billboard : ParticleSystemRenderMode.Stretch;
            if (!motes) { r.velocityScale = 0.06f; r.lengthScale = 1.2f; }
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return ps;
        }

        private Item ItemAt(int part) => part < Inner.Count ? Inner[part] : Outer[part - Inner.Count];

        private void SetVisible(bool on)
        {
            if (_mr != null && _mr.enabled != on) _mr.enabled = on;
            if (!on && _depthMr != null && _depthMr.enabled) _depthMr.enabled = false; // never an invisible disc of depth
            foreach (var l in _labels) if (l.gameObject.activeSelf != on) l.gameObject.SetActive(on);
            if (_faceText != null && _faceText.gameObject.activeSelf != on) _faceText.gameObject.SetActive(on);
            if (_hint != null && _hint.gameObject.activeSelf != on) _hint.gameObject.SetActive(on);
            if (_motes != null) { if (on && !_motes.isPlaying) _motes.Play(); else if (!on && _motes.isPlaying) _motes.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); }
        }

        // ------------------------------------------------------------------ every frame

        private void Update()
        {
            if (_ready) { Tick(Time.unscaledDeltaTime); return; }
            if (!_staged || _failed) return; // not built yet: the staged build does one step a frame, nothing else runs
            try { BuildStep(false); }
            catch (Exception ex)
            {
                _failed = true;
                enabled = false;
                Debug.LogWarning("[MetaCodex] wrist menu: the staged build failed in step " + _step + ": " + ex.Message);
            }
        }

        /// One step: watch pose, placement, the fingertip, the timelines, the mesh (tests call it with their own dt).
        /// Nothing happens before Ready.
        public void Tick(float dt)
        {
            if (!_ready || _mesh == null) return;
            if (_labels.Count != Inner.Count + Outer.Count) RebuildItems(); // the lists changed and nobody said so
            _time += dt;
            (bool okL, HandFrame L) = Hand != null ? Hand(Handedness.Left) : (false, default);
            (bool okR, HandFrame R) = Hand != null ? Hand(Handedness.Right) : (false, default);
            bool leftSeen = okL && !L.SystemGesture && Head != null;
            bool watching = false;
            Vector3 raw = default;
            _emitterOk = false;
            if (leftSeen)
            {
                var back = -L.PalmNormal(Handedness.Left);
                var fwd = (L.MiddleKnuckle - L.Wrist).normalized;
                var toHead = Head.position - L.Wrist;
                float dist = toHead.magnitude;
                toHead /= Mathf.Max(dist, 1e-4f);
                // the back of the wrist turned to the eyes, and looked at (within ~44 degrees of the gaze)
                watching = Vector3.Dot(back, toHead) > 0.45f && Vector3.Dot(Head.forward, -toHead) > 0.72f && dist > 0.12f && dist < 0.75f;
                // in front of the hand, not in it: the fingers and knuckles must stay behind the rings
                raw = L.Wrist - fwd * 0.012f + back * OffBack + toHead * TowardEyes;
                _emitterWorld = L.Wrist - fwd * 0.004f + back * 0.014f;
                _emitterOk = true;
            }

            // the right hand at the menu, or over the left hand: hold still, stay open
            _tipOk = okR && _posed;
            _tipPrev = _tipLocal;
            if (_tipOk) _tipLocal = transform.InverseTransformPoint(R.IndexTip);
            bool near = false;
            if (_tipOk && Visible)
            {
                float reach = _bloom > 0.3f ? ArcR2 + 0.02f : FaceR + 0.05f;
                near = _tipLocal.z > -0.11f && _tipLocal.z < 0.04f && new Vector2(_tipLocal.x, _tipLocal.y).magnitude < reach;
            }
            if (!near && okR && leftSeen && Visible && (R.IndexTip - L.Wrist).sqrMagnitude < 0.13f * 0.13f) near = true;
            if (near) _engagedUntil = _time + 0.6f;
            Engaged = Visible && _time < _engagedUntil;

            // show or not: a grace before it goes, none of it while engaged
            if (watching) { _watchFor += dt; _awayFor = 0f; }
            else { _watchFor = 0f; if (!Engaged) _awayFor += dt; }
            float grace = leftSeen ? TurnAwayGrace : LostGrace;
            if (_dismissing) _wantShow = false;
            else _wantShow = _wantShow ? Engaged || _awayFor < grace : _watchFor > 0.08f;
            if (!_wantShow) _expandTarget = false;

            // the timelines: the rings fold first, then the face goes
            float bloomTarget = _expandTarget ? 1f : 0f;
            _bloom = Mathf.MoveTowards(_bloom, bloomTarget, dt / (bloomTarget > _bloom ? BloomTime : FoldTime));
            bool keepFace = _wantShow || _bloom > 0.35f;
            _present = Mathf.MoveTowards(_present, keepFace ? 1f : 0f, dt / (keepFace ? AppearTime : VanishTime));

            // placement: firm on the wrist, still while engaged, jumps of the tracked pose ignored until they persist
            bool fresh = !_posed || _present <= 0.001f && _bloom <= 0.001f;
            if (leftSeen)
            {
                if (fresh) { _filter.Reset(raw); _anchor = raw; _jumpFor = 0f; _posed = true; }
                else if (!Engaged)
                {
                    bool jump = (raw - _anchor).sqrMagnitude > JumpDist * JumpDist;
                    _jumpFor = jump ? _jumpFor + dt : 0f;
                    if ((!jump || _jumpFor > 0.3f) && L.HighConfidence) _anchor = _filter.Step(raw, dt, 1.6f, 6f, 1f);
                }
                else _jumpFor = 0f;
                if (!Engaged || fresh)
                {
                    var toEye = (Head.position - _anchor).normalized;
                    var up = Vector3.ProjectOnPlane(Head.up, toEye);
                    var want = Quaternion.LookRotation(-toEye, up.sqrMagnitude > 1e-4f ? up.normalized : Vector3.up);
                    _rot = fresh ? want : Quaternion.Slerp(_rot, want, 1f - Mathf.Exp(-18f * dt));
                }
            }
            if (_posed) transform.SetPositionAndRotation(_anchor, _rot);
            transform.localScale = Vector3.one * Scale;
            if (_tipOk && fresh) _tipLocal = _tipPrev = transform.InverseTransformPoint(R.IndexTip);

            // the fingertip: over what, and touching. A touch starts when the tip comes within TouchZ of the surface,
            // over a part; it presses that part once it is steady on it (mostly straight in, over it for a moment), within
            // 0.2 s of the touch starting: a slanted press that arrives sideways and stops still counts; a slide across the
            // surface (a touch that starts over a gap, or moves on to another part) or a dip through the face on the way to
            // the left side presses nothing
            Hover = -1;
            if (_tipOk && _present > 0.6f)
            {
                var p = _tipLocal;
                int part = HitTest(new Vector2(p.x, p.y));
                bool inZone = p.z > -0.035f && p.z < 0.03f;
                if (inZone && p.z < 0.012f) Hover = part;
                int zonePart = inZone ? part : -1;
                if (zonePart != _hoverPart) { _hoverPart = zonePart; _hoverSince = _time; }
                if (p.z < ArmZ) _armed = true;
                bool touching = p.z >= TouchZ && p.z < 0.03f;
                if (touching && !_touching) { _touchPart = part; _touchSince = _time; }
                _touching = touching;
                var vel = dt > 1e-5f ? (p - _tipPrev) / dt : Vector3.zero;
                bool steady = new Vector2(vel.x, vel.y).magnitude < Mathf.Max(0.25f, Mathf.Abs(vel.z) * 2f);
                float dwell = part == -2 && _expandTarget ? 0.06f : 0.02f;
                if (_armed && touching && part != -1 && part == _touchPart && _time - _touchSince <= 0.2f && steady
                    && part == _hoverPart && _time - _hoverSince >= dwell && _time >= _cooldownUntil)
                {
                    _armed = false;
                    _cooldownUntil = _time + 0.3f;
                    Press(part, R.IndexTip, new Vector2(p.x, p.y));
                }
            }
            else { _hoverPart = -1; _touching = false; }
            if (_expandTarget && Hover == -1 && !Engaged) { _idle += dt; if (_idle > 7f) _expandTarget = false; } else _idle = 0f;
            for (int i = 0; i < _hoverAmt.Length; i++)
            {
                _hoverAmt[i] = Mathf.MoveTowards(_hoverAmt[i], Hover == i ? 1f : 0f, dt * 10f);
                _flash[i] = Mathf.Max(0f, _flash[i] - dt / 0.35f);
            }
            _coreHover = Mathf.MoveTowards(_coreHover, Hover == -2 ? 1f : 0f, dt * 10f);
            if (_wave < 1f) _wave = Mathf.Min(1f, _wave + dt / 0.42f);
            _faceTyped = Mathf.Min(1f, _faceTyped + dt / 0.14f);

            if (_dismissing && _present <= 0.001f && _bloom <= 0.001f) { _dismissing = false; HideNow(); enabled = false; return; }
            bool on = _present > 0.001f || _bloom > 0.001f;
            SetVisible(on);
            // depth only once it is mostly there: a fading menu must not hide the hand behind it
            bool depth = on && DepthWrite && _present > 0.5f;
            if (_depthMr != null && _depthMr.enabled != depth) _depthMr.enabled = depth;
            if (on) Rebuild();
        }

        /// What a point of the menu's surface is over (menu-local, metres before Scale).
        public int HitTest(Vector2 q)
        {
            float r = q.magnitude, a = Mathf.Atan2(q.y, q.x) * Mathf.Rad2Deg;
            if (r < CoreHit) return -2;
            if (_bloom < 0.6f) return -1;
            if (r >= InR0 - 0.001f && r <= InR1 + 0.0015f) { int i = TileAt(a, Inner.Count, InStart, InGapDeg); if (i >= 0) return i; }
            if (r >= OutR0 - 0.0015f && r <= OutR1 + 0.002f) { int i = TileAt(a, Outer.Count, OutStart, OutGapDeg); if (i >= 0) return Inner.Count + i; }
            return -1;
        }

        private static int TileAt(float a, int n, float start, float gap)
        {
            if (n == 0) return -1;
            float step = 360f / n;
            int i = Mathf.RoundToInt(-Mathf.DeltaAngle(start, a) / step);
            i = ((i % n) + n) % n;
            float centre = start - i * step;
            return Mathf.Abs(Mathf.DeltaAngle(centre, a)) <= step * 0.5f - gap * 0.5f ? i : -1;
        }

        private void Press(int part, Vector3 tipWorld, Vector2 local)
        {
            if (part == -2)
            {
                _expandTarget = !_expandTarget;
                UiSounds.Fire(_expandTarget ? "open" : "close");
                Burst(tipWorld, 10);
                _wave = 0f;
                _waveCenter = Vector2.zero;
                return;
            }
            var item = ItemAt(part); // the action may change the tiles (RebuildItems): keep this one
            _flash[part] = 1f;
            _idle = 0f;
            UiSounds.Fire("click");
            Burst(tipWorld, 18);
            _wave = 0f;
            _waveCenter = local;
            Pressed?.Invoke(part);
            try { item.Act?.Invoke(); }
            catch (Exception ex) { Debug.LogWarning("[MetaCodex] wrist menu " + item.Label + ": " + ex.Message); }
        }

        private void Burst(Vector3 at, int count)
        {
            if (_burst == null) return;
            _burst.transform.position = at;
            _burst.Emit(count);
        }

        /// Tests and hints: the world position of a part's centre (face: -2, tiles: 0..).
        public Vector3 WorldOf(int part)
        {
            if (part == -2) return transform.position;
            bool inner = part < Inner.Count;
            int i = inner ? part : part - Inner.Count, n = inner ? Inner.Count : Outer.Count;
            float a = ((inner ? InStart : OutStart) - i * 360f / n) * Mathf.Deg2Rad;
            float r = inner ? (InR0 + InR1) * 0.5f : (OutR0 + OutR1) * 0.5f;
            return transform.TransformPoint(new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f));
        }

        /// Off (DOCK mode, or the shell disabled it): its depth must not stay in the picture.
        private void OnDisable() { if (_depthMr != null) _depthMr.enabled = false; }

        /// Out of sight at once (tests; the shell uses Dismiss).
        public void HideNow()
        {
            _present = 0f; _bloom = 0f; _expandTarget = false; _wantShow = false; _posed = false; Engaged = false; _engagedUntil = 0f;
            SetVisible(false);
        }

        /// Goes with its closing animation, then turns itself off (the DOCK took over).
        public void Dismiss()
        {
            _dismissing = true;
            _expandTarget = false;
            if (!Visible) { _dismissing = false; HideNow(); enabled = false; }
        }

        /// On again (WRIST mode).
        public void Resume() { _dismissing = false; enabled = true; }

        /// Tests: fold or unfold without a finger.
        public void SetExpandedForTest(bool on) { _expandTarget = on; _bloom = on ? 1f : 0f; }

        /// Tests: a frame of the timelines, drawn now (pictures of the animation).
        public void PoseTimelineForTest(float present, float bloom)
        {
            if (!_ready) return;
            _present = Mathf.Clamp01(present);
            _bloom = Mathf.Clamp01(bloom);
            SetVisible(true);
            Rebuild();
        }

        // ------------------------------------------------------------------ the picture

        private static float Seg(float x, float a, float b) => Mathf.Clamp01((x - a) / (b - a));
        private static float EaseOut(float t) { t = Mathf.Clamp01(t); float u = 1f - t; return 1f - u * u * u; }
        private static float EaseInOut(float t) { t = Mathf.Clamp01(t); return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) * 0.5f; }
        private static float EaseOutBack(float t) { const float c1 = 1.70158f, c3 = c1 + 1f; t = Mathf.Clamp01(t); return 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f); }
        private static float Bump(float x, float a, float b) => Mathf.Sin(Mathf.PI * Seg(x, a, b));

        private float InnerStart(int j) => 0.14f + 0.055f * j;
        private float OuterStart(int j) => 0.28f + 0.032f * j;

        private void Rebuild()
        {
            _v.Clear(); _c.Clear(); _uv.Clear(); _info.Clear(); _extra.Clear(); _tri.Clear();
            var t = Theme ?? UiTheme.Current;
            Color accent = t.Accent, warm = t.Warn;
            float P = _present, B = _bloom;
            float uiHide = Mathf.Clamp01(Shader.GetGlobalFloat("_McxUiHide"));
            var now = DateTime.Now;

            // the projector: rays from the back of the wrist to the face's ring while it appears or unfolds (none at rest)
            float faceScale = Mathf.Lerp(0.55f, 1f, EaseOutBack(Seg(P, 0.15f, 0.85f))) * (1f - 0.07f * Bump(B, 0f, 0.3f));
            float beam = _emitterOk && Head != null ? 0.85f * Bump(P, 0f, 0.9f) + 0.5f * Bump(B, 0f, 0.45f) : 0f;
            if (beam > 0.01f)
            {
                var e = transform.InverseTransformPoint(_emitterWorld);
                var eye = transform.InverseTransformPoint(Head.position);
                float grow = EaseOut(Seg(P, 0f, 0.42f));
                var corners = _beamCorners;
                for (int k = 0; k < 6; k++)
                {
                    float a = (90f + 60f * k) * Mathf.Deg2Rad;
                    var target = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * (FaceR * 0.955f * faceScale);
                    corners[k] = Vector3.Lerp(e, target, grow);
                }
                for (int k = 0; k < 6; k++) Cone(e, corners[k], corners[(k + 1) % 6], accent, beam * 0.5f);
                for (int k = 0; k < 6; k++) Ray(e, corners[k], eye, 0.0006f, accent, beam);
            }

            // the radar sweep and the guides the shock ring leaves
            if (B > 0.01f)
            {
                float sweep = _time * 55f;
                Sweep(InR0, OutR1, sweep - 36f, sweep, 14, accent, 0.07f * Seg(B, 0.75f, 1f));
                float g = EaseInOut(Seg(B, 0.12f, 0.6f)) * 360f;
                if (g > 0.5f)
                {
                    Arc(Vector2.zero, InR0 - 0.0017f, 0.00026f, 90f - g, 90f, Mathf.Max(4, (int)(g / 6f)), accent, 0.38f, 0.4f);
                    Arc(Vector2.zero, OutR1 + 0.0017f, 0.00026f, 90f - g, 90f, Mathf.Max(4, (int)(g / 5f)), accent, 0.38f, 0.4f);
                }
                Arc(Vector2.zero, MidRingR, 0.00030f, 0f, 360f, 72, accent, 0.22f * Seg(B, 0.3f, 0.7f), 0.3f);
            }

            // the rings of tiles: the inner six swing in clockwise, the outer twelve the other way, each a fan opening
            int n = Inner.Count + Outer.Count;
            for (int i = 0; i < n; i++)
            {
                bool inner = i < Inner.Count;
                int j = inner ? i : i - Inner.Count, cnt = inner ? Inner.Count : Outer.Count;
                float s0 = inner ? InnerStart(j) : OuterStart(j);
                float e = Seg(B, s0, s0 + 0.34f);
                var label = _labels[i];
                if (e <= 0.001f) { HideLabel(label); continue; }
                float eo = EaseOut(e), eb = EaseOutBack(e);
                float step = 360f / cnt, gap = inner ? InGapDeg : OutGapDeg;
                float half = (step * 0.5f - gap * 0.5f) * Mathf.Clamp01(eo * 1.05f);
                float centre = (inner ? InStart : OutStart) - j * step + (inner ? 38f : -38f) * (1f - eo);
                float h = _hoverAmt[i], f = _flash[i];
                var item = ItemAt(i);
                bool locked = item.Locked != null && item.Locked();
                bool lit = !locked && item.Lit != null && item.Lit();
                Color col = locked ? LockedTint : lit ? item.LitTint : accent;
                float slide = -0.007f * (1f - eb);
                float r0 = (inner ? InR0 : OutR0) + slide + h * 0.0008f, r1 = (inner ? InR1 : OutR1) + slide + h * 0.0016f;
                float z = 0.006f * (1f - eo) + f * 0.0012f;
                float alpha = Seg(B, s0, s0 + 0.16f) * P;
                float prox = Hover == i && _tipOk ? Mathf.Clamp01(1f + _tipLocal.z / 0.03f) : 0f;
                float state = Mathf.Clamp01(h * 0.75f + f);
                Tile(r0, r1, centre - half, centre + half, inner ? 10 : 6, z, col, alpha * (inner ? 1f : 0.92f), state, prox, lit ? 1f : 0f, f);
                // the icon draws itself with a scan line, then the name types in under it
                float ar = centre * Mathf.Deg2Rad, rm = (r0 + r1) * 0.5f;
                var mid = new Vector2(Mathf.Cos(ar), Mathf.Sin(ar)) * rm;
                float icon = (inner ? 0.0108f : 0.0094f) * (1f + 0.12f * h);
                Color ink = locked ? LockedTint : lit ? item.LitTint : accent;
                IconQuad(mid + new Vector2(0f, inner ? 0.0026f : 0.0022f), icon, z - 0.0005f, _iconRect[i], ink, alpha, Seg(B, s0 + 0.12f, s0 + 0.34f), state, 1f);
                if (lit) TickLine(r1 - 0.0019f, r1 - 0.0007f, centre, 0.0011f, item.LitTint, alpha, 0.9f, z - 0.0004f);
                float typed = Seg(B, s0 + 0.18f, s0 + 0.40f);
                label.rectTransform.localPosition = new Vector3(mid.x, mid.y - (inner ? 0.0058f : 0.0050f), z - 0.0006f);
                int chars = Mathf.CeilToInt(item.Label.Length * typed);
                if (label.maxVisibleCharacters != chars) label.maxVisibleCharacters = chars;
                var lc = lit ? Color.Lerp(item.LitTint, Color.white, 0.5f) : Color.Lerp(t.TextDim, Color.white, 0.25f + h * 0.75f);
                lc.a = alpha * Seg(B, s0 + 0.2f, s0 + 0.42f) * (0.85f + 0.15f * h) * (1f - uiHide) * (locked ? 0.55f : 1f);
                if (label.color != lc) label.color = lc;
            }

            // the bezel: seventy-two ticks drawn round from twelve, turning slowly; arcs that slide in and drift
            if (B > 0.01f)
            {
                float tickIn = Seg(B, 0.45f, 0.85f), turn = -24f * (1f - EaseOut(Seg(B, 0.45f, 0.95f))) - _time * 1.2f;
                for (int k = 0; k < 72; k++)
                {
                    if (k / 72f > tickIn) break;
                    bool major = k % 6 == 0;
                    float a = 90f - k * 5f + turn;
                    TickLine(BezelR0 - (major ? 0.0007f : 0f), BezelR1 + (major ? 0.0010f : 0f), a, major ? 0.00065f : 0.00042f, accent, (major ? 0.85f : 0.45f) * P, 0.5f);
                }
                // twelve o'clock: a warm mark
                if (tickIn > 0.01f) TickLine(BezelR1 + 0.0016f, BezelR1 + 0.0042f, 90f + turn, 0.0012f, warm, 0.9f * Seg(B, 0.5f, 0.8f) * P, 0.8f);
                float arcs = Seg(B, 0.6f, 1f), slideIn = 0.008f * (1f - EaseOut(arcs)), drift = _time * 4f;
                if (arcs > 0.001f)
                {
                    foreach (var (a0, len) in ArcsOuter)
                    {
                        float s = a0 + drift;
                        Arc(Vector2.zero, ArcR + slideIn, 0.0006f, s, s + len * EaseOut(arcs), 18, accent, 0.5f * arcs * P, 0.6f);
                        TickLine(ArcR - 0.0022f + slideIn, ArcR + 0.0012f + slideIn, s, 0.0008f, accent, 0.8f * arcs * P, 0.6f);
                        TickLine(ArcR - 0.0022f + slideIn, ArcR + 0.0012f + slideIn, s + len * EaseOut(arcs), 0.0008f, accent, 0.8f * arcs * P, 0.6f);
                    }
                    foreach (var (a0, len) in ArcsInner)
                    {
                        float s = a0 - drift * 1.6f;
                        Arc(Vector2.zero, ArcR2 + slideIn * 1.4f, 0.0004f, s, s + len, 10, accent, 0.35f * arcs * P, 0.4f, 0f, -0.0003f, 7f, 0.55f);
                    }
                }
            }

            // the shock ring: out of the face to the outer ring when it unfolds, back in when it folds
            float shock = Bump(B, 0.06f, 0.48f);
            if (shock > 0.01f)
            {
                float k = EaseOut(Seg(B, 0.06f, 0.42f));
                Arc(Vector2.zero, Mathf.Lerp(FaceR * 0.8f, OutR1 + 0.004f, k), Mathf.Lerp(0.0016f, 0.0004f, k), 0f, 360f, 72, Color.Lerp(accent, Color.white, 0.35f), shock * 0.9f, 1f);
            }

            // the face: the outline draws round, the glass fills, the outer ring is traced, the ticks come round
            float ignite = Bump(P, 0.16f, 0.55f);
            if (ignite > 0.01f) Arc(Vector2.zero, FaceR * EaseOut(Seg(P, 0.16f, 0.55f)), 0.0007f, 0f, 360f, 48, Color.Lerp(accent, Color.white, 0.5f), ignite, 1f, 0f, -0.0006f);
            float sec = (now.Second + now.Millisecond / 1000f) / 60f;
            var face = new Vector4(EaseInOut(Seg(P, 0.25f, 0.75f)), Seg(P, 0.45f, 1f), EaseInOut(Seg(P, 0.35f, 0.9f)), EaseOut(Seg(P, 0.55f, 1f)));
            FaceQuad(FaceR * faceScale, -0.0004f, accent, Mathf.Clamp01(P * 3f), Mathf.Clamp01(_coreHover + 0.6f * Bump(B, 0f, 0.3f)), sec, face);
            if (B > 0.5f && _expandTarget && _closeRect.z > 0f)
                IconQuad(new Vector2(0f, -FaceR * 0.42f * faceScale), 0.0042f, -0.0008f, _closeRect, accent, Seg(B, 0.6f, 1f) * P * (0.55f + 0.45f * _coreHover), 1f, _coreHover, 0.6f);

            if (_wave < 1f)
            {
                float w = 1f - Mathf.Pow(1f - _wave, 3f);
                Arc(_waveCenter, Mathf.Lerp(0.004f, 0.034f, w), 0.0016f * (1f - _wave) + 0.0004f, 0f, 360f, 48, Color.Lerp(accent, Color.white, 0.4f), P * (1f - _wave) * 0.9f, 1f, 0f, -0.002f);
            }

            _mesh.Clear();
            _mesh.SetVertices(_v);
            _mesh.SetColors(_c);
            _mesh.SetUVs(0, _uv);
            _mesh.SetUVs(1, _info);
            _mesh.SetUVs(2, _extra);
            _mesh.SetTriangles(_tri, 0);
            _mesh.RecalculateBounds();

            // the face's text: the time, or the name of what the fingertip is over while unfolded; the hint while folded
            string want = B > 0.5f && Hover >= 0 ? ItemAt(Hover).Label : B > 0.5f && Hover == -2 ? "CLOSE" : TimeText(now);
            if (want != _faceShown) { _faceShown = want; _faceText.text = want; _faceTyped = 0f; }
            int faceChars = Mathf.CeilToInt(want.Length * Mathf.Min(_faceTyped, Seg(P, 0.6f, 1f) * 1.0001f));
            if (_faceText.maxVisibleCharacters != faceChars) _faceText.maxVisibleCharacters = faceChars;
            bool name = want.Length > 0 && !char.IsDigit(want[0]);
            float fs = (Theme ?? UiTheme.Current).FontSizeFor(name ? (want.Length > 6 ? 0.0030f : 0.0036f) : 0.0056f) * 1.12f;
            if (!Mathf.Approximately(_faceText.fontSize, fs)) _faceText.fontSize = fs;
            float cs = name ? 5f : 1f;
            if (!Mathf.Approximately(_faceText.characterSpacing, cs)) _faceText.characterSpacing = cs;
            _faceText.rectTransform.localPosition = new Vector3(0f, B > 0.5f && _expandTarget ? FaceR * 0.08f : 0f, -0.0010f);
            var fc = name ? Color.Lerp(t.TextAccent, Color.white, 0.4f) : Color.Lerp(t.TextAccent, Color.white, 0.15f + 0.5f * _coreHover);
            fc.a = Seg(P, 0.6f, 1f) * (1f - uiHide);
            _faceText.color = fc;
            _hint.rectTransform.localPosition = new Vector3(0f, -(FaceR + 0.0052f), -0.0009f);
            var hc = t.TextDim;
            hc.a = Seg(P, 0.75f, 1f) * (1f - B) * 0.8f * (1f - uiHide);
            _hint.color = hc;
        }

        private static void HideLabel(TextMeshPro l) { var c = l.color; if (c.a != 0f) { c.a = 0f; l.color = c; } }

        /// "HH:mm", made again only when the minute changes (the face asks every frame).
        private string TimeText(DateTime now)
        {
            int minute = now.Hour * 60 + now.Minute;
            if (minute != _timeMinute) { _timeMinute = minute; _timeText = now.ToString("HH:mm"); }
            return _timeText;
        }

        // ------------------------------------------------------------------ mesh pieces (WristArt kinds)

        private void Vert(Vector3 p, Color col, Vector2 uv, Vector4 info, Vector4 extra)
        {
            _v.Add(p); _c.Add(col); _uv.Add(uv); _info.Add(info); _extra.Add(extra);
        }

        private void Strip(int b, int segs)
        {
            for (int i = 0; i < segs; i++)
            {
                int q = b + i * 2;
                _tri.Add(q); _tri.Add(q + 1); _tri.Add(q + 3);
                _tri.Add(q); _tri.Add(q + 3); _tri.Add(q + 2);
            }
        }

        private void Quad(int b) { _tri.Add(b); _tri.Add(b + 1); _tri.Add(b + 2); _tri.Add(b); _tri.Add(b + 2); _tri.Add(b + 3); }

        /// A tile of glass (kind 0) from r0 to r1 between two angles, its corners rounded.
        private void Tile(float r0, float r1, float a0, float a1, int segs, float z, Color col, float strength, float state, float prox, float lit, float flash)
        {
            if (strength <= 0.002f || r1 <= r0 || a1 <= a0) return;
            var lin = col.linear;
            lin.a = strength;
            float width = r1 - r0, span = (a1 - a0) * Mathf.Deg2Rad, l0 = span * r0, l1 = span * r1;
            var ex = new Vector4(Mathf.Min(Corner, width * 0.3f, l0 * 0.3f), prox, lit, flash);
            int b = _v.Count;
            for (int i = 0; i <= segs; i++)
            {
                float k = i / (float)segs, a = (a0 + (a1 - a0) * k) * Mathf.Deg2Rad;
                float cx = Mathf.Cos(a), sy = Mathf.Sin(a);
                Vert(new Vector3(cx * r0, sy * r0, z), lin, new Vector2(0f, k * l0), new Vector4(width, l0, 0f, state), ex);
                Vert(new Vector3(cx * r1, sy * r1, z), lin, new Vector2(width, k * l1), new Vector4(width, l1, 0f, state), ex);
            }
            Strip(b, segs);
        }

        /// An arc of light (kind 1) of width w around radius r, with room for its glow; dashes along it if asked.
        private void Arc(Vector2 centre, float r, float w, float a0, float a1, int segs, Color col, float strength, float glow, float state = 0f, float z = -0.0003f, float dashes = 0f, float duty = 0.5f)
        {
            if (strength <= 0.002f || w <= 0f || segs < 1) return;
            float qw = w + 2f * Mathf.Max(0.0007f, w * 2.4f) * Mathf.Clamp01(glow + 0.2f);
            var lin = col.linear;
            lin.a = strength;
            var info = new Vector4(w, qw, 1f, state);
            var ex = new Vector4(glow, dashes, duty, 0f);
            int b = _v.Count;
            for (int i = 0; i <= segs; i++)
            {
                float k = i / (float)segs, a = (a0 + (a1 - a0) * k) * Mathf.Deg2Rad;
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                var p0 = centre + dir * Mathf.Max(0f, r - qw * 0.5f);
                var p1 = centre + dir * (r + qw * 0.5f);
                Vert(new Vector3(p0.x, p0.y, z), lin, new Vector2(0f, k), info, ex);
                Vert(new Vector3(p1.x, p1.y, z), lin, new Vector2(1f, k), info, ex);
            }
            Strip(b, segs);
        }

        /// A radial tick of light (kind 1) from r0 to r1 at angle a.
        private void TickLine(float r0, float r1, float aDeg, float w, Color col, float strength, float glow, float z = -0.0003f)
        {
            if (strength <= 0.002f) return;
            float a = aDeg * Mathf.Deg2Rad;
            var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            float qw = w + 2f * Mathf.Max(0.0006f, w * 2f) * Mathf.Clamp01(glow + 0.2f);
            var side = new Vector2(-dir.y, dir.x) * (qw * 0.5f);
            var lin = col.linear;
            lin.a = strength;
            var info = new Vector4(w, qw, 1f, 0f);
            var ex = new Vector4(glow, 0f, 0.5f, 0f);
            int b = _v.Count;
            Vector2 p0 = dir * r0 - side, p1 = dir * r0 + side, p2 = dir * r1 + side, p3 = dir * r1 - side;
            Vert(new Vector3(p0.x, p0.y, z), lin, new Vector2(0f, 0f), info, ex);
            Vert(new Vector3(p1.x, p1.y, z), lin, new Vector2(1f, 0f), info, ex);
            Vert(new Vector3(p2.x, p2.y, z), lin, new Vector2(1f, 1f), info, ex);
            Vert(new Vector3(p3.x, p3.y, z), lin, new Vector2(0f, 1f), info, ex);
            Quad(b);
        }

        /// A soft glow (kind 2) over a ring sector, brightest at its leading edge (a1): the radar sweep.
        private void Sweep(float r0, float r1, float a0, float a1, int segs, Color col, float strength)
        {
            if (strength <= 0.002f) return;
            var lin = col.linear;
            lin.a = strength;
            var info = new Vector4(r1 - r0, 0f, 2f, 0f);
            int b = _v.Count;
            for (int i = 0; i <= segs; i++)
            {
                float k = i / (float)segs, a = (a0 + (a1 - a0) * k) * Mathf.Deg2Rad;
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                Vert(new Vector3(dir.x * r0, dir.y * r0, 0.0004f), lin, new Vector2(0f, k), info, Vector4.zero);
                Vert(new Vector3(dir.x * r1, dir.y * r1, 0.0004f), lin, new Vector2(1f, k), info, Vector4.zero);
            }
            Strip(b, segs);
        }

        /// A line icon (kind 3) from the atlas, centred at c.
        private void IconQuad(Vector2 c, float size, float z, Vector4 rect, Color col, float strength, float reveal, float state, float glow)
        {
            if (strength <= 0.002f || rect.z <= 0f) return; // no rectangle: the atlas has no such icon (or its job failed)
            float h = size * 0.5f * UiIconAtlas.Extent; // the cell holds the icon's -1..1 and its glow
            var lin = col.linear;
            lin.a = strength;
            var info = new Vector4(glow, reveal, 3f, state);
            int b = _v.Count;
            Vert(new Vector3(c.x - h, c.y - h, z), lin, new Vector2(0f, 0f), info, rect);
            Vert(new Vector3(c.x + h, c.y - h, z), lin, new Vector2(1f, 0f), info, rect);
            Vert(new Vector3(c.x + h, c.y + h, z), lin, new Vector2(1f, 1f), info, rect);
            Vert(new Vector3(c.x - h, c.y + h, z), lin, new Vector2(0f, 1f), info, rect);
            Quad(b);
        }

        /// The watch face (kind 5): a quad of half size `half`.
        private void FaceQuad(float half, float z, Color col, float strength, float state, float seconds, Vector4 extra)
        {
            if (strength <= 0.002f) return;
            var lin = col.linear;
            lin.a = strength;
            var info = new Vector4(half, seconds, 5f, state);
            int b = _v.Count;
            Vert(new Vector3(-half, -half, z), lin, new Vector2(-1f, -1f), info, extra);
            Vert(new Vector3(half, -half, z), lin, new Vector2(1f, -1f), info, extra);
            Vert(new Vector3(half, half, z), lin, new Vector2(1f, 1f), info, extra);
            Vert(new Vector3(-half, half, z), lin, new Vector2(-1f, 1f), info, extra);
            Quad(b);
        }

        /// A ray of the projector (kind 4) from a to b, turned to face the eye.
        private void Ray(Vector3 a, Vector3 b, Vector3 eye, float w, Color col, float strength)
        {
            var side = Vector3.Cross(b - a, (a + b) * 0.5f - eye);
            if (side.sqrMagnitude < 1e-14f) return;
            side = side.normalized * (w * 0.5f);
            var lin = col.linear;
            lin.a = strength;
            var info = new Vector4(0f, 0f, 4f, 0f);
            int i0 = _v.Count;
            Vert(a - side, lin, new Vector2(0f, 0f), info, Vector4.zero);
            Vert(a + side, lin, new Vector2(1f, 0f), info, Vector4.zero);
            Vert(b + side, lin, new Vector2(1f, 1f), info, Vector4.zero);
            Vert(b - side, lin, new Vector2(0f, 1f), info, Vector4.zero);
            Quad(i0);
        }

        /// The faint cone of light between two rays (kind 4, x = 1).
        private void Cone(Vector3 e, Vector3 p0, Vector3 p1, Color col, float strength)
        {
            var lin = col.linear;
            lin.a = strength;
            var info = new Vector4(1f, 0f, 4f, 0f);
            int i0 = _v.Count;
            Vert(e, lin, new Vector2(0.5f, 0f), info, Vector4.zero);
            Vert(p0, lin, new Vector2(0f, 1f), info, Vector4.zero);
            Vert(p1, lin, new Vector2(1f, 1f), info, Vector4.zero);
            _tri.Add(i0); _tri.Add(i0 + 1); _tri.Add(i0 + 2);
        }

        /// A 1-euro filter (Casiez et al. 2012): smooth when still, quick when moving.
        private struct OneEuro3
        {
            private bool _init;
            private Vector3 _x, _dx;

            public void Reset(Vector3 v) { _init = true; _x = v; _dx = Vector3.zero; }

            public Vector3 Step(Vector3 v, float dt, float minCutoff, float beta, float dCutoff)
            {
                if (!_init || dt <= 1e-5f) { Reset(v); return v; }
                _dx = Vector3.Lerp(_dx, (v - _x) / dt, Alpha(dt, dCutoff));
                _x = Vector3.Lerp(_x, v, Alpha(dt, minCutoff + beta * _dx.magnitude));
                return _x;
            }

            private static float Alpha(float dt, float cutoff) { float tau = 1f / (2f * Mathf.PI * cutoff); return 1f / (1f + tau / dt); }
        }
    }
}
