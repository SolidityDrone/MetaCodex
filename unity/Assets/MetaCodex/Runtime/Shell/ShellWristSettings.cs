using System;
using System.Collections.Generic;
using System.Linq;
using MetaCodex.UI;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// The wrist menu's own settings (ShellSettings.wrist; CONTROL > WRIST MENU; voice set_setting wrist_*): its size, text
    /// size and glass, what its core shows, whether a tap folds it, and the layout of its two rings. The DOCK shows the same
    /// items in the same order (one table: MainMenuItems).
    [Serializable]
    public sealed class WristMenuSettings
    {
        public const float MinSize = 0.8f, MaxSize = 1.3f, MinText = 0.8f, MaxText = 1.4f, MinOpacity = 0.3f, MaxOpacity = 1f;
        /// MENU SIZE (x the default size), TEXT SIZE (x the names' and the time's size), GLASS (its own opacity: the glass
        /// no longer follows PANEL OPACITY).
        public float size = 1f, textSize = 1f, opacity = 1f;
        /// What the core shows.
        public bool battery = true, network = true, clock = true;
        /// A tap on a tile folds the rings.
        public bool closeAfterTap = false;
        /// The layout: item ids per ring in order, and the ones the user took off the menu. All three empty: the app's
        /// default layout (WristLayout).
        public List<string> inner = new List<string>(), outer = new List<string>(), hidden = new List<string>();
        /// The icon the user picked for a plugin's tile, "tileId=icon" (it wins over the plugin's own).
        public List<string> icons = new List<string>();

        /// Lists that a hand-edited or older file left out are empty, not null.
        public void Fix()
        {
            if (inner == null) inner = new List<string>();
            if (outer == null) outer = new List<string>();
            if (hidden == null) hidden = new List<string>();
            if (icons == null) icons = new List<string>();
        }

        public WristMenuSettings Clone()
        {
            Fix();
            var c = (WristMenuSettings)MemberwiseClone();
            c.inner = new List<string>(inner);
            c.outer = new List<string>(outer);
            c.hidden = new List<string>(hidden);
            c.icons = new List<string>(icons);
            return c;
        }

        public bool IsDefaultLayout => Count(inner) == 0 && Count(outer) == 0 && Count(hidden) == 0;

        public bool SameLook(WristMenuSettings o) => o != null && Mathf.Approximately(size, o.size) && Mathf.Approximately(textSize, o.textSize)
            && Mathf.Approximately(opacity, o.opacity) && battery == o.battery && network == o.network && clock == o.clock && closeAfterTap == o.closeAfterTap;

        /// The same rings and icons (allocates nothing: it runs on every settings change).
        public bool SameLayout(WristMenuSettings o) => o != null && Same(inner, o.inner) && Same(outer, o.outer) && Same(hidden, o.hidden) && Same(icons, o.icons);

        public bool SameAs(WristMenuSettings o) => SameLook(o) && SameLayout(o);

        /// The icon the user picked for this tile, or null.
        public string IconOf(string id)
        {
            if (icons == null || string.IsNullOrEmpty(id)) return null;
            foreach (var e in icons)
            {
                if (e == null) continue;
                int eq = e.LastIndexOf('=');
                if (eq == id.Length && string.CompareOrdinal(e, 0, id, 0, id.Length) == 0) return e.Substring(eq + 1);
            }
            return null;
        }

        /// Sets (or with null, forgets) the user's icon for a tile.
        public void SetIcon(string id, string icon)
        {
            Fix();
            icons.RemoveAll(e => e != null && e.LastIndexOf('=') == id.Length && e.StartsWith(id, StringComparison.Ordinal));
            if (!string.IsNullOrEmpty(icon)) icons.Add(id + "=" + icon);
        }

        private static int Count(List<string> l) => l?.Count ?? 0;

        public static bool Same(List<string> a, List<string> b)
        {
            int na = a?.Count ?? 0, nb = b?.Count ?? 0;
            if (na != nb) return false;
            for (int i = 0; i < na; i++) if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }
    }

    /// The wrist menu's layout from what there is and what the user saved (pure, self-tested): the inner ring, the outer
    /// ring, and what is off the menu (the page's ADD list).
    public static class WristLayout
    {
        /// `ids` / `outerByDefault`: every item there is, in the app's order, and its default ring. `pinned` (CONTROL) is
        /// never off the menu. Nothing saved: the default layout (every item in its default ring; an outer ring over its limit
        /// puts the rest off). Saved: its rings in its order; ids that are not there any more (an uninstalled plugin) are
        /// skipped; items it does not name (a new system item, a new plugin's tile) go to the end of the outer ring. Then the
        /// limits: the inner ring keeps MinInner..MaxInner (its overflow goes to the start of the outer ring; too few: it
        /// takes the outer ring's first), the outer ring keeps MaxOuter (its overflow goes off the menu).
        public static void Resolve(IReadOnlyList<string> ids, IReadOnlyList<bool> outerByDefault, string pinned,
            IReadOnlyList<string> savedInner, IReadOnlyList<string> savedOuter, IReadOnlyList<string> savedHidden,
            List<string> inner, List<string> outer, List<string> off)
        {
            inner.Clear();
            outer.Clear();
            off.Clear();
            var known = new HashSet<string>(ids);
            var placed = new HashSet<string>();
            bool fresh = (savedInner?.Count ?? 0) == 0 && (savedOuter?.Count ?? 0) == 0 && (savedHidden?.Count ?? 0) == 0;
            if (fresh)
            {
                for (int i = 0; i < ids.Count; i++)
                    if (placed.Add(ids[i])) (outerByDefault[i] ? outer : inner).Add(ids[i]);
            }
            else
            {
                Take(savedInner, inner, known, placed);
                Take(savedOuter, outer, known, placed);
                Take(savedHidden, off, known, placed);
                for (int i = 0; i < ids.Count; i++)
                    if (placed.Add(ids[i])) outer.Add(ids[i]); // new since the layout was saved: the end of the outer ring
            }
            if (pinned != null && off.Remove(pinned)) inner.Add(pinned); // never off the menu: the user could not come back here
            while (inner.Count > UiWristMenu.MaxInner)
            {
                int k = LastNot(inner, pinned);
                outer.Insert(0, inner[k]);
                inner.RemoveAt(k);
            }
            while (inner.Count < UiWristMenu.MinInner && (outer.Count > 0 || off.Count > 0))
            {
                var from = outer.Count > 0 ? outer : off;
                inner.Add(from[0]);
                from.RemoveAt(0);
            }
            while (outer.Count > UiWristMenu.MaxOuter)
            {
                int k = LastNot(outer, pinned);
                off.Insert(0, outer[k]);
                outer.RemoveAt(k);
            }
        }

        private static void Take(IReadOnlyList<string> from, List<string> to, HashSet<string> known, HashSet<string> placed)
        {
            if (from == null) return;
            for (int i = 0; i < from.Count; i++)
                if (from[i] != null && known.Contains(from[i]) && placed.Add(from[i])) to.Add(from[i]);
        }

        private static int LastNot(List<string> l, string pinned)
        {
            for (int i = l.Count - 1; i >= 0; i--) if (l[i] != pinned) return i;
            return l.Count - 1;
        }
    }

    /// CONTROL > WRIST MENU (page `wrist-settings`, its own window of CONTROL's size; owner 2026-11-16): the wrist menu's
    /// layout and look, edited on a copy of the menu (the twin, UiWristMenu in Preview mode) on the page. The layout is drag
    /// and drop (owner, after the first test: "more like drag and drop ... better than the buttons to move"): pinch a tile on
    /// the twin and drag it to another place, the other ring, or off the twin; drag a tile from the palette (SYSTEM and APPS
    /// tiles off the menu) onto the twin. Every change shows on the twin; APPLY makes it the real menu (and the DOCK's
    /// order), CANCEL drops it, RESET LAYOUT puts the twin's rings back to the default (APPLY still needed). Closing the page
    /// drops what was not applied. The twin exists only while the page is open: made when it opens (in small steps on the
    /// headset), folded and destroyed when it closes; nothing of it runs while the page is closed.
    public sealed partial class MetaCodexShell
    {
        public const string WristSettingsId = "wrist-settings";
        /// The menu item that can never leave the menu (it opens CONTROL, and this page with it).
        public const string ControlItemId = "control";

        /// The wrist menu's settings (never null).
        public WristMenuSettings WristCfg
        {
            get
            {
                if (Settings.wrist == null) Settings.wrist = new WristMenuSettings();
                Settings.wrist.Fix();
                return Settings.wrist;
            }
        }

        private WristMenuSettings _wristBuiltWith; // the rings and icons the menus were last built with (MainMenuItems)
        private int _menuInner, _menuOuter;         // their counts (the rail's status)

        /// The main menu for a set of wrist settings (the real one, or the page's draft): the inner ring's items, then the
        /// outer ring's; `off` gets what is not on the menu.
        public List<MainMenuItem> MainMenuItemsFor(WristMenuSettings ws, List<MainMenuItem> off)
        {
            var all = MenuCatalog(ws);
            var ids = new List<string>(all.Count);
            var outerDef = new List<bool>(all.Count);
            var byId = new Dictionary<string, MainMenuItem>(all.Count);
            foreach (var m in all)
            {
                if (m.Id == null || byId.ContainsKey(m.Id)) continue;
                byId[m.Id] = m;
                ids.Add(m.Id);
                outerDef.Add(m.Outer);
            }
            var inner = new List<string>();
            var outer = new List<string>();
            var offIds = new List<string>();
            WristLayout.Resolve(ids, outerDef, ControlItemId, ws.inner, ws.outer, ws.hidden, inner, outer, offIds);
            var list = new List<MainMenuItem>(inner.Count + outer.Count);
            foreach (var id in inner) { var m = byId[id]; m.Outer = false; list.Add(m); }
            foreach (var id in outer) { var m = byId[id]; m.Outer = true; list.Add(m); }
            if (off != null) foreach (var id in offIds) off.Add(byId[id]);
            if (ReferenceEquals(ws, Settings.wrist))
            {
                _wristBuiltWith = ws.Clone();
                _menuInner = inner.Count;
                _menuOuter = outer.Count;
            }
            return list;
        }

        /// A menu's size, text size, glass, core and tap behaviour from the settings. `baseScale`: its size at MENU SIZE 1.
        /// `text`: the text size too (a change makes its labels again; the page's twin takes it at its own pace instead).
        private static void ApplyLookTo(UiWristMenu m, WristMenuSettings ws, float baseScale = UiWristMenu.DefaultScale, bool text = true)
        {
            if (m == null || ws == null) return;
            m.Scale = baseScale * Mathf.Clamp(ws.size, WristMenuSettings.MinSize, WristMenuSettings.MaxSize);
            float op = Mathf.Clamp(ws.opacity, WristMenuSettings.MinOpacity, WristMenuSettings.MaxOpacity);
            if (!Mathf.Approximately(m.GlassOpacity, op)) m.GlassOpacity = op;
            if (text) m.TextScale = TextScaleOf(ws);
            m.ShowBattery = ws.battery;
            m.ShowNetwork = ws.network;
            m.ShowClock = ws.clock;
            m.CloseAfterTap = ws.closeAfterTap;
        }

        private static float TextScaleOf(WristMenuSettings ws) => Mathf.Clamp(ws.textSize, WristMenuSettings.MinText, WristMenuSettings.MaxText);

        /// Every settings change (ApplyMainMenuMode): the wrist menu takes its own settings (assignments only, unless the text
        /// size changed: then its labels are made again); a changed layout or icon rebuilds the DOCK and the wrist menu once;
        /// the page's draft follows a change made elsewhere (voice, MQH) while the user has not edited it.
        private void ApplyWristLook()
        {
            if (Settings == null) return;
            var ws = WristCfg;
            if (Wrist != null) ApplyLookTo(Wrist, ws); // after ApplyMaterialSettings: the glass is the wrist's own, not PANEL OPACITY
            if (_wristBuiltWith != null && !_wristBuiltWith.SameLayout(ws)) RebuildMainMenus();
            if (_wd != null && _wsBase != null && _wd.SameAs(_wsBase) && !_wsBase.SameAs(ws))
            {
                _wd = ws.Clone();
                _wsBase = ws.Clone();
                SyncWristLookControls();
                RefreshWristPage();
            }
        }

        /// CONTROL's rail: the rings' counts while the wrist menu is the main menu.
        private string WristRailStatus() => !Settings.wristMenu ? "dock" : _menuInner + _menuOuter > 0 ? _menuInner + " + " + _menuOuter : "wrist";

        // ------------------------------------------------------------------ the page

        // panel coordinates (metres from the top-left, y down): the left column (the palette of tiles off the menu, the chosen
        // tile, its icon), the right column (the twin, the look), the bottom row (RESET LAYOUT, CANCEL, APPLY)
        private const float WsLeftX = 0.014f, WsLeftW = 0.39f, WsRightX = 0.418f, WsRightW = 0.328f;
        /// The twin's centre and its size at MENU SIZE 1: at the largest MENU SIZE its drifting arcs (0.088 m from the centre at
        /// scale 1) still fit the 0.30 m square above the sliders.
        private static readonly Vector2 WsTwinAt = new Vector2(0.582f, 0.240f);
        private const float WsTwinBase = 1.31f, WsTwinLift = 0.02f;
        private const int WsTwinQueue = 2995; // after the page's glass (SciFiUI 2990), before the texts (3000)
        /// A pinch that moves this far (m, on the page) before it is let go is a drag; less is a tap.
        private const float WsDragStart = 0.012f;
        private const int WsSysPool = 12, WsAppPool = 8;

        private UiPanel _wsPage;
        private bool _wsBuilt;
        private UiWristMenu _wsTwin;
        private WristMenuSettings _wd, _wsBase; // the draft, and the settings it started from
        private string _wsSel;                   // the chosen item's id (its icon can be picked)
        private float _wsTextDue = -1f;          // TEXT SIZE reaches the twin not before this time (0.15 s after the slider's last step)
        private readonly List<string> _wsInner = new List<string>(), _wsOuter = new List<string>(), _wsOff = new List<string>();
        private readonly Dictionary<string, MainMenuItem> _wsById = new Dictionary<string, MainMenuItem>();
        /// The twin's items by id, kept between re-layouts (each remembers where it is drawn, so it eases to its new place).
        private readonly Dictionary<string, UiWristMenu.Item> _wsTwinItems = new Dictionary<string, UiWristMenu.Item>();
        private readonly List<WsTile> _wsSysTiles = new List<WsTile>(), _wsAppTiles = new List<WsTile>();
        private WsDragArea _wsTwinArea;
        private UiLabel _wsSysCap, _wsAppCap, _wsSysNote, _wsAppNote, _wsSelLine, _wsState, _wsIconCap, _wsIconName, _wsIconNote;
        private UiButton _wsIconPrev, _wsIconNext, _wsIconReset;
        private UiButton _wsBattery, _wsNetwork, _wsClock, _wsApply, _wsCancel, _wsReset;
        private UiSlider _wsSize, _wsText, _wsGlass;
        private UiToggle _wsCloseTap;

        // a drag (one at a time): the tile, where it came from, whether it moved far enough, where it would land now and why
        // it cannot (null: it can), the element that holds the pinch, and the twin's rings while it moves
        private string _wsDragId, _wsDropWhy;
        private bool _wsDragging, _wsDragFromMenu;
        private int _wsDropRing = -2, _wsDropIndex;
        private UiElement _wsDragOwner;
        private readonly List<string> _wsPrevInner = new List<string>(), _wsPrevOuter = new List<string>();

        /// Called from BuildControl (start and RebuildPanels): the page's window, empty; its content is made the first time it
        /// opens (the start does not pay for it).
        private void BuildWristSettingsPanel()
        {
            _wsBuilt = false;
            _wsTwin = null;
            _wd = _wsBase = null;
            _wsSel = null;
            _wsDragId = null;
            _wsSysTiles.Clear();
            _wsAppTiles.Clear();
            _wsTwinItems.Clear();
            var p = Panel<UiPanel>(WristSettingsId, "WRIST MENU", ControlPageW, ControlPageH); // CONTROL's one size
            p.Opened += _ => OnWristPageOpened();
            p.Closed += _ => OnWristPageClosed();
            p.Tick += _ => TickWristPage();
            _wsPage = p;
        }

        private void OnWristPageOpened()
        {
            EnsureWristPage();
            if (_wd == null)
            {
                _wd = WristCfg.Clone();
                _wsBase = WristCfg.Clone();
                _wsSel = null;
                SyncWristLookControls();
            }
            RefreshWristPage();
            EnsureTwin();
        }

        private void OnWristPageClosed()
        {
            WsDragReset();
            _wd = _wsBase = null; // what was not applied is dropped
            _wsSel = null;
            if (_wsTwin == null) return;
            if (!Application.isPlaying || Application.isBatchMode) { DropTwin(); return; } // self-tests: no frames to fold in
            _wsTwin.Dismiss(); // folds with its animation, then the page's window goes and the twin with it
            After(0.6f, () => { if (_wsPage == null || !_wsPage.IsOpen) DropTwin(); });
        }

        private void DropTwin()
        {
            if (_wsTwin == null) return;
            var go = _wsTwin.gameObject;
            _wsTwin = null;
            _wsTwinItems.Clear();
            if (Application.isPlaying) UnityEngine.Object.Destroy(go); else UnityEngine.Object.DestroyImmediate(go);
        }

        /// While the page is open: the twin stays on the page's surface (a bent page too), takes the draft's TEXT SIZE once the
        /// slider rests (its labels are refitted: not on every step of a drag), and a drag whose pinch was lost (the hand left
        /// the cameras' view: no release) is dropped.
        private void TickWristPage()
        {
            if (!_wsBuilt || _wd == null) { OnWristPageOpened(); return; } // shown without Open (RebuildPanels)
            PoseTwin();
            if (_wsTwin != null && Time.unscaledTime >= _wsTextDue)
            {
                float text = TextScaleOf(_wd);
                if (!Mathf.Approximately(_wsTwin.TextScale, text)) _wsTwin.TextScale = text;
            }
            if (_wsDragId != null && _wsDragOwner != null && !_wsDragOwner.Pressed)
            {
                bool moved = _wsDragging;
                WsDragReset();
                if (moved) RefreshWristPage();
            }
        }

        private void EnsureTwin()
        {
            if (_wsPage == null) return;
            if (_wsTwin != null) { _wsTwin.Resume(); return; } // still folding away: back (RefreshWristPage filled it)
            var tw = UiWristMenu.Create(_wsPage.transform, UiTheme.Current);
            tw.gameObject.name = "wrist menu twin";
            tw.Preview = true;
            tw.DepthWrite = false;
            tw.ArtQueue = WsTwinQueue;
            tw.Head = Head;
            tw.TextScale = TextScaleOf(_wd);
            _wsTwin = tw;
            FillTwin();
            PoseTwin();
            // the Editor and the batch self-tests read it right away: whole, now; on the headset in small steps (UiWristMenu)
            bool whole = Application.isEditor || !Application.isPlaying || Application.isBatchMode;
            if (whole) tw.Build(); else tw.BuildStaged();
        }

        /// The twin takes the draft: its rings, its look, the chosen tile; no drag shown.
        private void FillTwin()
        {
            var tw = _wsTwin;
            if (tw == null || _wd == null) return;
            FillTwinRings(_wsInner, _wsOuter);
            tw.PreviewHole = null;
            tw.GhostItem = null;
            tw.PreviewCoreText = null;
            ApplyLookTo(tw, _wd, WsTwinBase, text: false); // the text size: TickWristPage
            tw.PreviewSelected = SelectedPart();
        }

        private void FillTwinRings(List<string> inner, List<string> outer)
        {
            var tw = _wsTwin;
            tw.Inner.Clear();
            tw.Outer.Clear();
            foreach (var id in inner) tw.Inner.Add(TwinItem(id));
            foreach (var id in outer) tw.Outer.Add(TwinItem(id));
            tw.RebuildItems();
        }

        private UiWristMenu.Item TwinItem(string id)
        {
            if (!_wsTwinItems.TryGetValue(id, out var it)) _wsTwinItems[id] = it = new UiWristMenu.Item { LitTint = UiTheme.Current.Ok };
            if (_wsById.TryGetValue(id, out var m)) { it.Glyph = m.Glyph; it.Icon = m.Icon; it.Label = m.Label; }
            return it;
        }

        /// The chosen item's index on the twin (inner ring first), -1 for none.
        private int SelectedPart()
        {
            if (_wsSel == null) return -1;
            int i = _wsInner.IndexOf(_wsSel);
            if (i >= 0) return i;
            int o = _wsOuter.IndexOf(_wsSel);
            return o >= 0 ? _wsInner.Count + o : -1;
        }

        /// On the page's surface over WsTwinAt, a little towards the user; on a bent page, turned with the surface there.
        private void PoseTwin()
        {
            if (_wsTwin == null || _wsPage == null) return;
            float r = _wsPage.BendNow;
            var flat = _wsPage.PanelToLocal(WsTwinAt, 0f);
            Vector3 pos;
            Quaternion rot = Quaternion.identity;
            if (r <= 0.05f) pos = new Vector3(flat.x, flat.y, -WsTwinLift);
            else
            {
                float a = flat.x / r, rr = r - WsTwinLift;
                pos = new Vector3(rr * Mathf.Sin(a), flat.y, -r + rr * Mathf.Cos(a));
                rot = Quaternion.Euler(0f, a * Mathf.Rad2Deg, 0f);
            }
            var tt = _wsTwin.transform;
            if (tt.localPosition != pos) tt.localPosition = pos;
            if (tt.localRotation != rot) tt.localRotation = rot;
        }

        /// Where a pointer is on the twin's plane (twin-local metres before its scale): a hand ray or the mouse meets the
        /// plane the twin floats in (not the page 2 cm behind it, so it is exact from the side too); a fingertip is taken
        /// where it is; else (tests) the page point lifted to the twin's plane.
        private bool TwinPoint(UiPointerEvent e, out Vector2 q)
        {
            q = default;
            if (_wsTwin == null || _wsPage == null) return false;
            var t = _wsTwin.transform;
            var ptr = e.Pointer;
            Vector3 world;
            if (ptr != null && ptr.Kind == PointerKind.Poke) world = ptr.Tip;
            else if (ptr != null && ptr.Kind != PointerKind.Test && new Plane(t.forward, t.position).Raycast(ptr.Ray, out float d)) world = ptr.Ray.GetPoint(d);
            else world = _wsPage.SurfacePoint(e.Point, -WsTwinLift);
            var l = t.InverseTransformPoint(world);
            q = new Vector2(l.x, l.y);
            return true;
        }

        /// The page's controls, made once (the first time it opens): pools of palette tiles (shown and labelled as the draft
        /// needs: the page's mesh never grows while the user edits), the twin's drag surface, the look, the bottom row.
        private void EnsureWristPage()
        {
            if (_wsBuilt || _wsPage == null) return;
            _wsBuilt = true;
            var p = _wsPage;
            var t = UiTheme.Current;
            var c = p.Content;
            float y = c.y;
            p.Add(new UiLabel("Look at the back of your left wrist as at a watch, then touch its core with your right index finger. "
                + "Edit on the copy on the right; APPLY puts it on your wrist.", t.SmallSize)
            { Rect = new Rect(c.x, y, c.width, 0.032f), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
            y += 0.036f;

            // the palette: what is off the menu, in two groups; pinch one and drag it onto the copy (a tap adds it at the end)
            const float capH = 0.018f, tileH = 0.042f, tileGap = 0.005f, noteH = 0.020f;
            float tileW = (WsLeftW - 3f * 0.006f) / 4f;
            WsTile Tile(int k, float top)
            {
                var b = p.Add(new WsTile { Rect = new Rect(WsLeftX + (k % 4) * (tileW + 0.006f), top + (k / 4) * (tileH + tileGap), tileW, tileH), TextSize = 0.0112f });
                b.Down = WsTileDown;
                b.Drag = (tile, e) => WsDragMove(e);
                b.Up = WsTileUp;
                return b;
            }
            _wsSysCap = p.Add(new UiLabel("SYSTEM · OFF THE MENU", t.SmallSize) { Rect = new Rect(WsLeftX, y, WsLeftW, capH), UseDisplay = true, Color = t.TextAccent });
            y += capH + 0.002f;
            _wsSysNote = p.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(WsLeftX, y, WsLeftW, tileH), Color = t.TextDim });
            for (int k = 0; k < WsSysPool; k++) _wsSysTiles.Add(Tile(k, y));
            y += 3 * (tileH + tileGap) + 0.002f;
            _wsAppCap = p.Add(new UiLabel("APPS · OFF THE MENU", t.SmallSize) { Rect = new Rect(WsLeftX, y, WsLeftW, capH), UseDisplay = true, Color = t.TextAccent });
            y += capH + 0.002f;
            for (int k = 0; k < WsAppPool; k++) _wsAppTiles.Add(Tile(k, y));
            y += 2 * (tileH + tileGap);
            _wsAppNote = p.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(WsLeftX, y, WsLeftW, noteH), Color = t.TextDim });
            y += noteH + 0.004f;
            p.Add(new UiLabel("Pinch a tile on the copy and drag it: to another place, to the other ring, or off the copy to take it off. "
                + "Drag a tile from here onto the copy to add it there.", t.SmallSize)
            { Rect = new Rect(WsLeftX, y, WsLeftW, 0.044f), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
            y += 0.048f;
            // the chosen tile (a tap on the copy) and, for a plugin's tile, its icon: one of the generic icons, or the plugin's
            _wsSelLine = p.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(WsLeftX, y, WsLeftW, 0.022f), Color = t.Text });
            y += 0.026f;
            const float rowH = 0.032f;
            _wsIconCap = p.Add(new UiLabel("ICON", t.SmallSize) { Rect = new Rect(WsLeftX, y, 0.05f, rowH), UseDisplay = true, Color = t.TextDim });
            _wsIconPrev = p.Add(new UiButton("◂", () => WsIcon(-1)) { Rect = new Rect(WsLeftX + 0.05f, y, 0.04f, rowH) });
            _wsIconName = p.Add(new UiLabel("", t.SmallSize * 1.1f) { Rect = new Rect(WsLeftX + 0.094f, y, 0.11f, rowH), UseDisplay = true, Align = TextAlignmentOptions.Center });
            _wsIconNext = p.Add(new UiButton("▸", () => WsIcon(1)) { Rect = new Rect(WsLeftX + 0.208f, y, 0.04f, rowH) });
            _wsIconReset = p.Add(new UiButton("PLUGIN'S", () => WsIcon(0)) { Rect = new Rect(WsLeftX + 0.256f, y, WsLeftW - 0.256f, rowH), TextSize = 0.0118f });
            _wsIconNote = p.Add(new UiLabel("Icons: tap a plugin's tile on the copy to give it one.", t.SmallSize) { Rect = new Rect(WsLeftX, y, WsLeftW, rowH), Color = t.TextDim });

            // the right column: the twin above (its drag surface: nothing else of the page is there), the look under it
            _wsTwinArea = p.Add(new WsDragArea { Rect = new Rect(WsRightX, WsTwinAt.y - 0.152f, WsRightW, 0.304f) });
            _wsTwinArea.Down = WsTwinDown;
            _wsTwinArea.Drag = WsDragMove;
            _wsTwinArea.Up = WsTwinUp;
            _wsTwinArea.Over = WsTwinOver;
            _wsTwinArea.Left = () => { if (_wsTwin != null) _wsTwin.PreviewHover = -1; };
            float ry = WsTwinAt.y + 0.15f + 0.008f;
            const float lookH = 0.030f, lookGap = 0.005f;
            // the sliders move in steps of 0.05 (the thumb snaps to the value kept)
            _wsSize = p.Add(new UiSlider("MENU SIZE", WristMenuSettings.MinSize, WristMenuSettings.MaxSize, 1f, v => WsLook(() => { _wd.size = Steps(v); _wsSize.Set(_wd.size); })) { Rect = new Rect(WsRightX, ry, WsRightW, lookH), LabelWidth = 0.11f });
            ry += lookH + lookGap;
            _wsText = p.Add(new UiSlider("TEXT SIZE", WristMenuSettings.MinText, WristMenuSettings.MaxText, 1f, v =>
            {
                if (_wd == null) return;
                _wd.textSize = Steps(v);
                _wsText.Set(_wd.textSize);
                _wsTextDue = Time.unscaledTime + 0.15f; // the twin refits its labels once the slider rests (TickWristPage)
                RefreshWristState();
            }) { Rect = new Rect(WsRightX, ry, WsRightW, lookH), LabelWidth = 0.11f });
            ry += lookH + lookGap;
            _wsGlass = p.Add(new UiSlider("GLASS", WristMenuSettings.MinOpacity, WristMenuSettings.MaxOpacity, 1f, v => WsLook(() => { _wd.opacity = Steps(v); _wsGlass.Set(_wd.opacity); })) { Rect = new Rect(WsRightX, ry, WsRightW, lookH), LabelWidth = 0.11f });
            ry += lookH + lookGap;
            p.Add(new UiLabel("CORE", t.SmallSize) { Rect = new Rect(WsRightX, ry, 0.06f, lookH), UseDisplay = true, Color = t.TextDim });
            float coreW = (WsRightW - 0.06f - 2f * 0.005f) / 3f;
            _wsBattery = p.Add(new UiButton("BATTERY", () => WsLook(() => _wd.battery = !_wd.battery)) { Rect = new Rect(WsRightX + 0.06f, ry, coreW, lookH), TextSize = 0.0115f });
            _wsNetwork = p.Add(new UiButton("NETWORK", () => WsLook(() => _wd.network = !_wd.network)) { Rect = new Rect(WsRightX + 0.06f + coreW + 0.005f, ry, coreW, lookH), TextSize = 0.0115f });
            _wsClock = p.Add(new UiButton("CLOCK", () => WsLook(() => _wd.clock = !_wd.clock)) { Rect = new Rect(WsRightX + 0.06f + 2f * (coreW + 0.005f), ry, coreW, lookH), TextSize = 0.0115f });
            ry += lookH + lookGap;
            _wsCloseTap = p.Add(new UiToggle("Fold the rings after a tap", false, v => WsLook(() => _wd.closeAfterTap = v)) { Rect = new Rect(WsRightX, ry, WsRightW, lookH) });

            // the bottom row
            float bottomY = c.yMax - t.ButtonHeight;
            _wsState = p.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(WsLeftX, bottomY, WsLeftW, t.ButtonHeight), Color = t.TextDim });
            const float bGap = 0.014f;
            float bw = (WsRightW - 2f * bGap) / 3f;
            _wsReset = p.Add(new UiButton("RESET LAYOUT", WsResetLayout) { Rect = new Rect(WsRightX, bottomY, bw, t.ButtonHeight), TextSize = 0.0118f });
            _wsCancel = p.Add(new UiButton("CANCEL", WsCancel) { Rect = new Rect(WsRightX + bw + bGap, bottomY, bw, t.ButtonHeight), TextSize = 0.0125f });
            _wsApply = p.Add(new UiButton("APPLY", WsApply, UiButtonStyle.Primary) { Rect = new Rect(WsRightX + 2f * (bw + bGap), bottomY, bw, t.ButtonHeight), TextSize = 0.0125f });
        }

        private static float Steps(float v) => Mathf.Round(v / 0.05f) * 0.05f;

        /// The draft's rings from the catalog (the app's items and the app plugins' tiles, with the draft's icons), then every
        /// control and the twin. A drag going on is dropped (the lists it was made from changed).
        private void RefreshWristPage()
        {
            if (_wd == null || !_wsBuilt) return;
            WsDragReset();
            var all = MenuCatalog(_wd);
            _wsById.Clear();
            var ids = new List<string>(all.Count);
            var outerDef = new List<bool>(all.Count);
            foreach (var m in all)
            {
                if (m.Id == null || _wsById.ContainsKey(m.Id)) continue;
                _wsById[m.Id] = m;
                ids.Add(m.Id);
                outerDef.Add(m.Outer);
            }
            WristLayout.Resolve(ids, outerDef, ControlItemId, _wd.inner, _wd.outer, _wd.hidden, _wsInner, _wsOuter, _wsOff);
            if (_wsSel != null && !_wsInner.Contains(_wsSel) && !_wsOuter.Contains(_wsSel)) _wsSel = null;
            RefreshPalette();
            RefreshSelection();
            FillTwin();
            RefreshWristState();
        }

        /// The palette: SYSTEM and APPS tiles off the menu (each with its glyph and name, draggable), then the app plugins that
        /// are off or stopped by an error (dimmed: they have no tile until they run).
        private void RefreshPalette()
        {
            int s = 0, a = 0, appsOn = 0, stopped = 0;
            foreach (var id in _wsInner) if (_wsById[id].Plugin) appsOn++;
            foreach (var id in _wsOuter) if (_wsById[id].Plugin) appsOn++;
            foreach (var id in _wsOff)
            {
                var m = _wsById[id];
                if (!m.Plugin) { if (s < _wsSysTiles.Count) ShowTile(_wsSysTiles[s++], id, m.Glyph, m.Label, false); }
                else if (a < _wsAppTiles.Count) ShowTile(_wsAppTiles[a++], id, m.Glyph, m.Label, false);
            }
            int appsOff = a;
            if (Scripts != null)
                foreach (var e in Scripts.Entries)
                {
                    if (e.Kind != "plugin" || e.Enabled && e.Error == null || PluginKindOf(e) != KindApps) continue;
                    stopped++;
                    if (a < _wsAppTiles.Count) ShowTile(_wsAppTiles[a++], null, "◇", (e.Title.Length > 0 ? e.Title : e.Name).ToUpperInvariant(), true);
                }
            for (int k = s; k < _wsSysTiles.Count; k++) _wsSysTiles[k].SetVisible(false);
            for (int k = a; k < _wsAppTiles.Count; k++) _wsAppTiles[k].SetVisible(false);
            _wsSysNote.Set(s == 0 ? "Every system tile is on the menu: drag one off the copy to take it off." : "");
            _wsAppNote.Set(stopped > 0 ? "Dimmed: the plugin is off. Turn it on in MQH > PLUGINS."
                : appsOff + appsOn == 0 ? "No app plugins yet: MQH > PLUGINS has examples (timers, calendar)."
                : appsOff == 0 ? "Every app tile is on the menu." : "");
        }

        private static void ShowTile(WsTile b, string id, string glyph, string label, bool dimmed)
        {
            b.ItemId = id;
            b.SetVisible(true);
            b.SetText(glyph + "\n" + label);
            b.SetDimmed(dimmed);
            b.SetSelected(false);
        }

        /// The chosen item's line and its icon row.
        private void RefreshSelection()
        {
            var t = UiTheme.Current;
            int ii = _wsSel != null ? _wsInner.IndexOf(_wsSel) : -1, oi = _wsSel != null ? _wsOuter.IndexOf(_wsSel) : -1;
            bool any = ii >= 0 || oi >= 0;
            var m = any ? _wsById[_wsSel] : null;
            if (!any) _wsSelLine.Set("Tap a tile on the copy to choose it.", t.TextDim);
            else _wsSelLine.Set(UiChatView.Esc(m.Label) + " · " + (ii >= 0 ? $"inner ring, {ii + 1} of {_wsInner.Count}" : $"outer ring, {oi + 1} of {_wsOuter.Count}")
                + (m.Plugin ? " · plugin " + UiChatView.Esc(m.Source) : ""), t.Text);
            bool icon = any && m.Plugin;
            _wsIconCap.SetVisible(icon);
            _wsIconPrev.SetVisible(icon);
            _wsIconName.SetVisible(icon);
            _wsIconNext.SetVisible(icon);
            _wsIconReset.SetVisible(icon);
            _wsIconNote.SetVisible(!icon);
            if (icon)
            {
                bool own = _wd.IconOf(_wsSel) == null;
                _wsIconName.Set(m.Icon.ToUpperInvariant(), own ? t.TextDim : t.TextAccent);
                _wsIconReset.SetDimmed(own);
            }
            if (_wsTwin != null) _wsTwin.PreviewSelected = SelectedPart();
        }

        /// The bottom line and APPLY / CANCEL: whether the draft differs from the wrist.
        private void RefreshWristState()
        {
            if (_wd == null || !_wsBuilt) return;
            bool dirty = !_wd.SameAs(WristCfg);
            _wsState.Set(dirty ? "Not on your wrist yet: APPLY puts it there, CANCEL drops it." : "This is your wrist menu now.",
                dirty ? UiTheme.Current.Warn : UiTheme.Current.TextDim);
            _wsApply.SetDimmed(!dirty);
            _wsCancel.SetDimmed(!dirty);
            _wsReset.SetDimmed(_wd.IsDefaultLayout);
            _wsBattery.SetSelected(_wd.battery);
            _wsNetwork.SetSelected(_wd.network);
            _wsClock.SetSelected(_wd.clock);
        }

        /// The sliders and the toggle take the draft's values (when the page opens, after CANCEL).
        private void SyncWristLookControls()
        {
            if (_wd == null || !_wsBuilt) return;
            _wsSize.Set(_wd.size);
            _wsText.Set(_wd.textSize);
            _wsGlass.Set(_wd.opacity);
            _wsCloseTap.Set(_wd.closeAfterTap);
        }

        // ------------------------------------------------------------------ drag and drop (the router's pinch drag)

        // The router gives an element the pinch that starts on it (OnPress), every frame while it is held (OnDrag, the
        // pointer as it is now, also off the element and off the page) and its end (OnRelease): the twin's surface and each
        // palette tile are such elements. A pinch that moves WsDragStart is a drag: the tile follows the pointer on the twin,
        // the twin shows where it lands (its neighbours make room) and the core says what a drop does; let go, it lands
        // there. A pinch that does not move is a tap: on the twin it chooses the tile, on the palette it adds it at the end
        // of the outer ring. Nothing here allocates per frame: a drag re-lays the twin only when the place changes.

        private void WsTwinDown(UiPointerEvent e)
        {
            if (_wd == null || _wsTwin == null || !TwinPoint(e, out var q)) return;
            int part = _wsTwin.PickTile(q);
            if (part < 0 || part >= _wsInner.Count + _wsOuter.Count) return; // the core or around it: a tap there chooses nothing
            WsDragBegin(part < _wsInner.Count ? _wsInner[part] : _wsOuter[part - _wsInner.Count], true, _wsTwinArea);
        }

        private void WsTwinUp(UiPointerEvent e, bool inside)
        {
            if (_wsDragId == null)
            {
                if (_wsSel != null) { _wsSel = null; RefreshSelection(); } // a tap on the core or a gap: nothing chosen
                return;
            }
            WsDragEnd();
        }

        private void WsTwinOver(UiPointerEvent e)
        {
            if (_wsTwin == null || _wsDragId != null) return;
            int part = TwinPoint(e, out var q) ? _wsTwin.PickTile(q) : -1;
            if (_wsTwin.PreviewHover != part) _wsTwin.PreviewHover = part;
        }

        private void WsTileDown(WsTile tile, UiPointerEvent e)
        {
            if (_wd == null) return;
            if (tile.ItemId == null) return; // a plugin that is off: its tap says where to turn it on
            WsDragBegin(tile.ItemId, false, tile);
        }

        private void WsTileUp(WsTile tile, UiPointerEvent e, bool inside)
        {
            if (tile.ItemId == null)
            {
                if (inside) { UiSounds.Fire("click"); Toast("That plugin is off: turn it on in CONTROL > MQH > PLUGINS, then its tile is here.", "info"); }
                return;
            }
            if (_wsDragId == null) return;
            if (!_wsDragging) { WsDragReset(); if (inside) { UiSounds.Fire("click"); WsAdd(tile.ItemId); } return; } // a tap: at the end
            WsDragEnd();
        }

        private void WsDragBegin(string id, bool fromMenu, UiElement owner)
        {
            WsDragReset();
            _wsDragId = id;
            _wsDragFromMenu = fromMenu;
            _wsDragOwner = owner;
        }

        /// Every frame of a held pinch: past WsDragStart the tile is picked up; then the ghost follows the pointer and, when the
        /// place under it changes, the twin is laid out again with the tile there.
        private void WsDragMove(UiPointerEvent e)
        {
            if (_wsDragId == null || _wd == null || _wsTwin == null) return;
            if (!_wsDragging)
            {
                if ((e.Point - e.PressPoint).sqrMagnitude < WsDragStart * WsDragStart) return;
                _wsDragging = true;
                UiSounds.Fire("click");
                _wsTwin.GhostItem = TwinItem(_wsDragId);
                _wsTwin.PreviewHover = -1;
                _wsTwin.PreviewSelected = -1;
                foreach (var tl in _wsSysTiles) if (tl.ItemId == _wsDragId) tl.SetSelected(true);
                foreach (var tl in _wsAppTiles) if (tl.ItemId == _wsDragId) tl.SetSelected(true);
            }
            if (!TwinPoint(e, out var q)) return;
            _wsTwin.GhostAt = q;
            int ring = UiWristMenu.DropRing(q);
            bool fromInner = _wsInner.Contains(_wsDragId), fromOuter = _wsOuter.Contains(_wsDragId);
            int innerOthers = _wsInner.Count - (fromInner ? 1 : 0), outerOthers = _wsOuter.Count - (fromOuter ? 1 : 0);
            int index = ring >= 0 ? UiWristMenu.DropIndex(q, ring == 0, ring == 0 ? innerOthers : outerOthers) : 0;
            if (ring == _wsDropRing && index == _wsDropIndex) return; // the same place: nothing to lay out again
            _wsDropRing = ring;
            _wsDropIndex = index;
            _wsDropWhy = DropRefusal(_wsDragId, ring, fromInner, innerOthers, outerOthers);
            LayDragPreview(fromInner || fromOuter);
        }

        /// Why the dragged tile cannot land on `ring` (null: it can).
        private static string DropRefusal(string id, int ring, bool fromInner, int innerOthers, int outerOthers)
        {
            if (ring < 0 && id == ControlItemId) return "CONTROL stays on the menu: it is the way back to this page.";
            if (ring == 0 && innerOthers >= UiWristMenu.MaxInner) return $"The inner ring holds {UiWristMenu.MaxInner} at most.";
            if (ring == 1 && outerOthers >= UiWristMenu.MaxOuter) return $"The outer ring holds {UiWristMenu.MaxOuter} at most.";
            if (ring != 0 && fromInner && innerOthers < UiWristMenu.MinInner) return $"The inner ring keeps {UiWristMenu.MinInner} at least.";
            return null;
        }

        /// The twin while a tile is dragged: the rings without it, and it at the place under the pointer (an empty, lit slot);
        /// where it cannot land, the rings as they are with its own place lit (it goes back there).
        private void LayDragPreview(bool onMenu)
        {
            var tw = _wsTwin;
            string id = _wsDragId;
            _wsPrevInner.Clear();
            _wsPrevOuter.Clear();
            UiWristMenu.Item hole = null;
            if (_wsDropWhy != null)
            {
                _wsPrevInner.AddRange(_wsInner);
                _wsPrevOuter.AddRange(_wsOuter);
                if (onMenu) hole = TwinItem(id);
            }
            else
            {
                foreach (var x in _wsInner) if (x != id) _wsPrevInner.Add(x);
                foreach (var x in _wsOuter) if (x != id) _wsPrevOuter.Add(x);
                if (_wsDropRing >= 0)
                {
                    var ring = _wsDropRing == 0 ? _wsPrevInner : _wsPrevOuter;
                    ring.Insert(Mathf.Clamp(_wsDropIndex, 0, ring.Count), id);
                    hole = TwinItem(id);
                }
            }
            FillTwinRings(_wsPrevInner, _wsPrevOuter);
            tw.PreviewHole = hole;
            tw.GhostWarn = _wsDropWhy != null;
            tw.PreviewCoreText = _wsDropWhy != null ? (id == ControlItemId && _wsDropRing < 0 ? "STAYS" : "NO ROOM")
                : _wsDropRing == 0 ? "INNER" : _wsDropRing == 1 ? "OUTER" : onMenu ? "TAKE OFF" : "";
        }

        /// The pinch is let go: the tile lands where the twin showed it, goes off the menu, or goes back (with the reason).
        private void WsDragEnd()
        {
            string id = _wsDragId, why = _wsDropWhy;
            bool dragged = _wsDragging, fromMenu = _wsDragFromMenu;
            int ring = _wsDropRing, index = _wsDropIndex;
            WsDragReset();
            if (id == null || _wd == null) return;
            if (!dragged)
            {
                if (!fromMenu) return;
                _wsSel = id; // a tap on the twin chooses the tile (its icon, for a plugin's)
                RefreshSelection();
                _wsTwin?.Ping(SelectedPart());
                return;
            }
            if (ring == -2) { FillTwin(); return; }
            if (why != null)
            {
                UiSounds.Fire("error");
                Toast(why, "warn");
                FillTwin();
                return;
            }
            if (ring < 0 && !fromMenu) { FillTwin(); return; } // a palette tile let go away from the rings: nothing changes
            UiSounds.Fire("ping");
            WsEdit((inner, outer, off) =>
            {
                inner.Remove(id);
                outer.Remove(id);
                off.Remove(id);
                if (ring < 0)
                {
                    off.Insert(0, id);
                    if (_wsSel == id) _wsSel = null;
                    return null;
                }
                var r = ring == 0 ? inner : outer;
                r.Insert(Mathf.Clamp(index, 0, r.Count), id);
                _wsSel = id;
                return null;
            });
        }

        /// No drag any more: the twin shows the draft again (when a drag was shown), the palette's picked tile goes dark.
        private void WsDragReset()
        {
            bool shown = _wsDragging;
            _wsDragId = null;
            _wsDropWhy = null;
            _wsDragging = false;
            _wsDropRing = -2;
            _wsDropIndex = 0;
            _wsDragOwner = null;
            if (!shown) return;
            foreach (var tl in _wsSysTiles) tl.SetSelected(false);
            foreach (var tl in _wsAppTiles) tl.SetSelected(false);
            if (_wsTwin != null) { _wsTwin.GhostItem = null; _wsTwin.PreviewHole = null; _wsTwin.PreviewCoreText = null; _wsTwin.GhostWarn = false; }
        }

        // ------------------------------------------------------------------ the page's actions (all on the draft)

        /// A change of the rings: made on copies of the resolved lists, refused with a reason, else written into the draft
        /// (as the default when it is the default again) and shown.
        private void WsEdit(Func<List<string>, List<string>, List<string>, string> change)
        {
            if (_wd == null) return;
            var inner = new List<string>(_wsInner);
            var outer = new List<string>(_wsOuter);
            var off = new List<string>(_wsOff);
            string why = change(inner, outer, off);
            if (why != null) { Toast(why, "warn"); return; }
            _wd.inner = inner;
            _wd.outer = outer;
            _wd.hidden = off;
            if (IsDefaultRings(inner, outer, off)) { _wd.inner = new List<string>(); _wd.outer = new List<string>(); _wd.hidden = new List<string>(); }
            RefreshWristPage();
            _wsTwin?.Ping(SelectedPart());
        }

        private bool IsDefaultRings(List<string> inner, List<string> outer, List<string> off)
        {
            var ids = new List<string>();
            var outerDef = new List<bool>();
            foreach (var m in MenuCatalog(_wd)) { if (m.Id == null || ids.Contains(m.Id)) continue; ids.Add(m.Id); outerDef.Add(m.Outer); }
            List<string> di = new List<string>(), dout = new List<string>(), doff = new List<string>();
            WristLayout.Resolve(ids, outerDef, ControlItemId, null, null, null, di, dout, doff);
            return WristMenuSettings.Same(inner, di) && WristMenuSettings.Same(outer, dout) && WristMenuSettings.Same(off, doff);
        }

        /// A palette tile's tap: at the end of the outer ring (else the inner ring), the shortcut for a drag.
        private void WsAdd(string id)
        {
            WsEdit((inner, outer, off) =>
            {
                if (!off.Remove(id)) return null;
                if (outer.Count < UiWristMenu.MaxOuter) outer.Add(id);
                else if (inner.Count < UiWristMenu.MaxInner) inner.Add(id);
                else { off.Insert(0, id); return "Both rings are full: drag a tile off the copy first."; }
                _wsSel = id;
                return null;
            });
        }

        /// ICON ◂ ▸ steps through the generic icons; PLUGIN'S (0) gives the tile its plugin's icon back.
        private void WsIcon(int dir)
        {
            if (_wd == null || _wsSel == null || !_wsById.TryGetValue(_wsSel, out var m) || !m.Plugin) return;
            if (dir == 0) _wd.SetIcon(_wsSel, null);
            else
            {
                var names = UiIconAtlas.PluginIcons;
                int i = Array.IndexOf(names, m.Icon);
                _wd.SetIcon(_wsSel, names[((i < 0 ? 0 : i + dir) % names.Length + names.Length) % names.Length]);
            }
            RefreshWristPage();
            _wsTwin?.Ping(SelectedPart());
        }

        /// MENU SIZE, GLASS, the core's parts, the fold after a tap: the twin takes them at once (no labels refitted).
        private void WsLook(Action change)
        {
            if (_wd == null) return;
            change();
            ApplyLookTo(_wsTwin, _wd, WsTwinBase, text: false); // TEXT SIZE has its own pace (TickWristPage)
            RefreshWristState();
        }

        private void WsResetLayout()
        {
            if (_wd == null) return;
            _wd.inner = new List<string>();
            _wd.outer = new List<string>();
            _wd.hidden = new List<string>();
            RefreshWristPage();
        }

        private void WsCancel()
        {
            if (_wd == null) return;
            _wd = WristCfg.Clone();
            _wsBase = WristCfg.Clone();
            _wsSel = null;
            SyncWristLookControls();
            RefreshWristPage();
        }

        /// The draft becomes the wrist menu (and the DOCK's order): saved, applied, the menus built again once.
        private void WsApply()
        {
            if (_wd == null) return;
            if (_wd.SameAs(WristCfg)) { Toast("Nothing to apply: this is your wrist menu now.", "info"); return; }
            Settings.wrist = _wd.Clone();
            _wsBase = _wd.Clone();
            OnSettingsChanged(); // saved; ApplyWristLook gives the wrist menu its look and rebuilds the menus if the rings changed
            Log($"wrist menu: applied ({_wsInner.Count} + {_wsOuter.Count} tiles, size {_wd.size:0.00}, text {_wd.textSize:0.00}, glass {_wd.opacity:0.00})");
            Toast(Settings.wristMenu ? "Your wrist menu is updated." : "Saved. The DOCK follows the new order; the wrist menu is off (DISPLAY).", "ok");
            RefreshWristState();
        }

        /// The plugins' tiles changed: an open page takes them into its copy and its palette.
        private void OnMenuCatalogChanged()
        {
            if (_wd != null) RefreshWristPage();
        }

        // ------------------------------------------------------------------ self-tests

        public UiWristMenu WristTwinForTest => _wsTwin;
        public WristMenuSettings WristDraftForTest => _wd;
        public void WristPageSelectForTest(string id) { _wsSel = id; RefreshWristPage(); }
        /// The twin's drag surface, and the palette tile of an item (null: not in the palette).
        public UiElement WristTwinAreaForTest => _wsTwinArea;
        public UiElement WristPaletteTileForTest(string id) => _wsSysTiles.Concat(_wsAppTiles).FirstOrDefault(x => x.Visible && x.ItemId == id);
        /// The page point over a point of the twin (twin-local metres before its scale; a flat page).
        public Vector2 WristTwinToPageForTest(Vector2 local)
        {
            var world = _wsTwin.transform.TransformPoint(new Vector3(local.x, local.y, 0f));
            return _wsPage.LocalToPanel(_wsPage.transform.InverseTransformPoint(world));
        }
    }

    /// The WRIST MENU page's twin surface: an element that hands the router's pinch (press, every frame of the drag,
    /// release) and the ray's hover to the page.
    internal sealed class WsDragArea : UiElement
    {
        public Action<UiPointerEvent> Down, Drag, Over;
        public Action<UiPointerEvent, bool> Up;
        public Action Left;
        public override bool Interactive => true;
        protected override void Build() { }
        public override void OnPress(UiPointerEvent e) => Down?.Invoke(e);
        public override void OnDrag(UiPointerEvent e) => Drag?.Invoke(e);
        public override void OnHover(UiPointerEvent e) => Over?.Invoke(e);
        public override void OnRelease(UiPointerEvent e, bool inside) => Up?.Invoke(e, inside);
        protected override void OnInteractionChanged() { if (!Hover && !Pressed) Left?.Invoke(); }
    }

    /// A palette tile on the WRIST MENU page: a button whose pinch can be dragged onto the twin (ItemId null: a plugin that
    /// is off, shown dimmed). A pinch that does not move is a tap.
    internal sealed class WsTile : UiButton
    {
        public string ItemId;
        public Action<WsTile, UiPointerEvent> Down, Drag;
        public Action<WsTile, UiPointerEvent, bool> Up;
        public WsTile() : base("") { }
        public override void OnPress(UiPointerEvent e) { base.OnPress(e); Down?.Invoke(this, e); }
        public override void OnDrag(UiPointerEvent e) => Drag?.Invoke(this, e);
        public override void OnRelease(UiPointerEvent e, bool inside) => Up?.Invoke(this, e, inside);
    }
}
