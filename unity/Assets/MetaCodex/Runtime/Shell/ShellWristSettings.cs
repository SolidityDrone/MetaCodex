using System;
using System.Collections.Generic;
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
    /// layout and look, edited on a copy of the menu (the twin, UiWristMenu in Preview mode) on the page. Every change shows
    /// on the twin; APPLY makes it the real menu (and the DOCK's order), CANCEL drops it, RESET LAYOUT puts the twin's rings
    /// back to the default (APPLY still needed). Closing the page drops what was not applied. The twin exists only while
    /// the page is open: made when it opens (in small steps on the headset), folded and destroyed when it closes; nothing
    /// of it runs while the page is closed.
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

        // panel coordinates (metres from the top-left, y down): the left column (the rings, the actions, ADD), the right
        // column (the twin, the look), the bottom row (RESET LAYOUT, CANCEL, APPLY)
        private const float WsLeftX = 0.014f, WsLeftW = 0.39f, WsRightX = 0.418f, WsRightW = 0.328f;
        /// The twin's centre and its size at MENU SIZE 1: at the largest MENU SIZE its drifting arcs (0.088 m from the centre at
        /// scale 1) still fit the 0.30 m square above the sliders.
        private static readonly Vector2 WsTwinAt = new Vector2(0.582f, 0.240f);
        private const float WsTwinBase = 1.31f, WsTwinLift = 0.02f;
        private const int WsTwinQueue = 2995; // after the page's glass (SciFiUI 2990), before the texts (3000)

        private UiPanel _wsPage;
        private bool _wsBuilt;
        private UiWristMenu _wsTwin;
        private WristMenuSettings _wd, _wsBase; // the draft, and the settings it started from
        private string _wsSel;                   // the chosen item's id
        private float _wsTextDue = -1f;          // TEXT SIZE reaches the twin not before this time (0.15 s after the slider's last step)
        private readonly List<string> _wsInner = new List<string>(), _wsOuter = new List<string>(), _wsOff = new List<string>();
        private readonly Dictionary<string, MainMenuItem> _wsById = new Dictionary<string, MainMenuItem>();
        private readonly List<UiButton> _wsInnerBtns = new List<UiButton>(), _wsOuterBtns = new List<UiButton>();
        private UiLabel _wsInnerCap, _wsOuterCap, _wsSelLine, _wsAddCap, _wsState, _wsIconCap, _wsIconName, _wsIconNote;
        private UiButton _wsLeft, _wsRight, _wsToInner, _wsToOuter, _wsHide, _wsIconPrev, _wsIconNext, _wsIconReset;
        private UiButton _wsBattery, _wsNetwork, _wsClock, _wsApply, _wsCancel, _wsReset;
        private UiSlider _wsSize, _wsText, _wsGlass;
        private UiToggle _wsCloseTap;
        private UiList _wsAdd;

        /// Called from BuildControl (start and RebuildPanels): the page's window, empty; its content is made the first time it
        /// opens (the start does not pay for it).
        private void BuildWristSettingsPanel()
        {
            _wsBuilt = false;
            _wsTwin = null;
            _wd = _wsBase = null;
            _wsSel = null;
            _wsInnerBtns.Clear();
            _wsOuterBtns.Clear();
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
            _wd = _wsBase = null; // what was not applied is dropped
            _wsSel = null;
            if (_wsTwin == null) return;
            if (!Application.isPlaying) { DropTwin(); return; }
            _wsTwin.Dismiss(); // folds with its animation, then the page's window goes and the twin with it
            After(0.6f, () => { if (_wsPage == null || !_wsPage.IsOpen) DropTwin(); });
        }

        private void DropTwin()
        {
            if (_wsTwin == null) return;
            var go = _wsTwin.gameObject;
            _wsTwin = null;
            if (Application.isPlaying) UnityEngine.Object.Destroy(go); else UnityEngine.Object.DestroyImmediate(go);
        }

        /// While the page is open: the twin stays on the page's surface (a bent page too), and takes the draft's TEXT SIZE once
        /// the slider rests (its labels are made again: not on every step of a drag).
        private void TickWristPage()
        {
            if (!_wsBuilt || _wd == null) { OnWristPageOpened(); return; } // shown without Open (RebuildPanels)
            PoseTwin();
            if (_wsTwin != null && Time.unscaledTime >= _wsTextDue)
            {
                float text = TextScaleOf(_wd);
                if (!Mathf.Approximately(_wsTwin.TextScale, text)) _wsTwin.TextScale = text;
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

        /// The twin takes the draft: its rings, its look, the chosen tile.
        private void FillTwin()
        {
            var tw = _wsTwin;
            if (tw == null || _wd == null) return;
            var t = UiTheme.Current;
            tw.Inner.Clear();
            tw.Outer.Clear();
            foreach (var id in _wsInner) tw.Inner.Add(TwinItem(id, t));
            foreach (var id in _wsOuter) tw.Outer.Add(TwinItem(id, t));
            tw.RebuildItems();
            ApplyLookTo(tw, _wd, WsTwinBase, text: false); // the text size: TickWristPage
            tw.PreviewSelected = SelectedPart();
        }

        private UiWristMenu.Item TwinItem(string id, UiTheme t)
        {
            var m = _wsById[id];
            return new UiWristMenu.Item { Glyph = m.Glyph, Icon = m.Icon, Label = m.Label, LitTint = t.Ok };
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

        /// The page's controls, made once (the first time it opens). Pools of buttons for the rings (8 + 12), shown and
        /// labelled as the draft needs: the page's mesh never grows while the user edits.
        private void EnsureWristPage()
        {
            if (_wsBuilt || _wsPage == null) return;
            _wsBuilt = true;
            var p = _wsPage;
            var t = UiTheme.Current;
            var c = p.Content;
            float y = c.y;
            p.Add(new UiLabel("Look at the back of your left wrist as at a watch, then touch its core with your right index finger. "
                + "Changes show on the copy on the right; APPLY puts them on your wrist.", t.SmallSize)
            { Rect = new Rect(c.x, y, c.width, 0.032f), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
            y += 0.036f;

            // the rings: a button per place, in order (clockwise from 12)
            const float slotH = 0.032f, slotGap = 0.005f, capH = 0.018f;
            float slotW = (WsLeftW - 3f * slotGap) / 4f;
            _wsInnerCap = p.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(WsLeftX, y, WsLeftW, capH), UseDisplay = true, Color = t.TextAccent });
            y += capH + 0.002f;
            for (int k = 0; k < UiWristMenu.MaxInner; k++)
            {
                int slot = k;
                _wsInnerBtns.Add(p.Add(new UiButton("", () => WsPick(false, slot)) { Rect = new Rect(WsLeftX + (k % 4) * (slotW + slotGap), y + (k / 4) * (slotH + slotGap), slotW, slotH), TextSize = 0.0125f }));
            }
            y += 2 * (slotH + slotGap) + 0.002f;
            _wsOuterCap = p.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(WsLeftX, y, WsLeftW, capH), UseDisplay = true, Color = t.TextAccent });
            y += capH + 0.002f;
            for (int k = 0; k < UiWristMenu.MaxOuter; k++)
            {
                int slot = k;
                _wsOuterBtns.Add(p.Add(new UiButton("", () => WsPick(true, slot)) { Rect = new Rect(WsLeftX + (k % 4) * (slotW + slotGap), y + (k / 4) * (slotH + slotGap), slotW, slotH), TextSize = 0.0125f }));
            }
            y += 3 * (slotH + slotGap) + 0.002f;

            // what is chosen, and what can be done with it
            _wsSelLine = p.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(WsLeftX, y, WsLeftW, 0.022f), Color = t.Text });
            y += 0.026f;
            float actW = (WsLeftW - 4f * 0.005f) / 5f;
            UiButton Act(int k, string text, Action a) => p.Add(new UiButton(text, a) { Rect = new Rect(WsLeftX + k * (actW + 0.005f), y, actW, slotH), TextSize = 0.0118f });
            _wsLeft = Act(0, "◂ MOVE", () => WsMove(-1));
            _wsRight = Act(1, "MOVE ▸", () => WsMove(1));
            _wsToInner = Act(2, "TO INNER", WsToInner);
            _wsToOuter = Act(3, "TO OUTER", WsToOuter);
            _wsHide = Act(4, "HIDE", WsHide);
            y += slotH + 0.006f;
            // a plugin tile's icon: one of the generic icons (the twin shows it), or the plugin's own
            _wsIconCap = p.Add(new UiLabel("ICON", t.SmallSize) { Rect = new Rect(WsLeftX, y, 0.05f, slotH), UseDisplay = true, Color = t.TextDim });
            _wsIconPrev = p.Add(new UiButton("◂", () => WsIcon(-1)) { Rect = new Rect(WsLeftX + 0.05f, y, 0.04f, slotH) });
            _wsIconName = p.Add(new UiLabel("", t.SmallSize * 1.1f) { Rect = new Rect(WsLeftX + 0.094f, y, 0.11f, slotH), UseDisplay = true, Align = TextAlignmentOptions.Center });
            _wsIconNext = p.Add(new UiButton("▸", () => WsIcon(1)) { Rect = new Rect(WsLeftX + 0.208f, y, 0.04f, slotH) });
            _wsIconReset = p.Add(new UiButton("PLUGIN'S", () => WsIcon(0)) { Rect = new Rect(WsLeftX + 0.256f, y, WsLeftW - 0.256f, slotH), TextSize = 0.0118f });
            _wsIconNote = p.Add(new UiLabel("Icons: choose a plugin's tile to give it one.", t.SmallSize) { Rect = new Rect(WsLeftX, y, WsLeftW, slotH), Color = t.TextDim });
            y += slotH + 0.006f;
            // what is off the menu: a tap puts it back (at the end of the outer ring)
            _wsAddCap = p.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(WsLeftX, y, WsLeftW, capH), UseDisplay = true, Color = t.TextAccent });
            y += capH + 0.002f;
            float bottomY = c.yMax - t.ButtonHeight;
            _wsAdd = p.Add(new UiList(0.030f, (item, idx) => { if (item?.Tag is string id) WsAdd(id); })
            { Rect = new Rect(WsLeftX, y, WsLeftW, bottomY - 0.008f - y), EmptyText = "Everything is on the menu." });

            // the right column: the twin above (nothing of the page is drawn there), the look under it
            float ry = WsTwinAt.y + 0.15f + 0.008f;
            const float rowH = 0.030f, rowGap = 0.005f;
            // the sliders move in steps of 0.05 (the thumb snaps to the value kept)
            _wsSize = p.Add(new UiSlider("MENU SIZE", WristMenuSettings.MinSize, WristMenuSettings.MaxSize, 1f, v => WsLook(() => { _wd.size = Steps(v); _wsSize.Set(_wd.size); })) { Rect = new Rect(WsRightX, ry, WsRightW, rowH), LabelWidth = 0.11f });
            ry += rowH + rowGap;
            _wsText = p.Add(new UiSlider("TEXT SIZE", WristMenuSettings.MinText, WristMenuSettings.MaxText, 1f, v =>
            {
                if (_wd == null) return;
                _wd.textSize = Steps(v);
                _wsText.Set(_wd.textSize);
                _wsTextDue = Time.unscaledTime + 0.15f; // the twin makes its labels again once the slider rests (TickWristPage)
                RefreshWristState();
            }) { Rect = new Rect(WsRightX, ry, WsRightW, rowH), LabelWidth = 0.11f });
            ry += rowH + rowGap;
            _wsGlass = p.Add(new UiSlider("GLASS", WristMenuSettings.MinOpacity, WristMenuSettings.MaxOpacity, 1f, v => WsLook(() => { _wd.opacity = Steps(v); _wsGlass.Set(_wd.opacity); })) { Rect = new Rect(WsRightX, ry, WsRightW, rowH), LabelWidth = 0.11f });
            ry += rowH + rowGap;
            p.Add(new UiLabel("CORE", t.SmallSize) { Rect = new Rect(WsRightX, ry, 0.06f, rowH), UseDisplay = true, Color = t.TextDim });
            float coreW = (WsRightW - 0.06f - 2f * 0.005f) / 3f;
            _wsBattery = p.Add(new UiButton("BATTERY", () => WsLook(() => _wd.battery = !_wd.battery)) { Rect = new Rect(WsRightX + 0.06f, ry, coreW, rowH), TextSize = 0.0115f });
            _wsNetwork = p.Add(new UiButton("NETWORK", () => WsLook(() => _wd.network = !_wd.network)) { Rect = new Rect(WsRightX + 0.06f + coreW + 0.005f, ry, coreW, rowH), TextSize = 0.0115f });
            _wsClock = p.Add(new UiButton("CLOCK", () => WsLook(() => _wd.clock = !_wd.clock)) { Rect = new Rect(WsRightX + 0.06f + 2f * (coreW + 0.005f), ry, coreW, rowH), TextSize = 0.0115f });
            ry += rowH + rowGap;
            _wsCloseTap = p.Add(new UiToggle("Fold the rings after a tap", false, v => WsLook(() => _wd.closeAfterTap = v)) { Rect = new Rect(WsRightX, ry, WsRightW, rowH) });

            // the bottom row
            _wsState = p.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(WsLeftX, bottomY, WsLeftW, t.ButtonHeight), Color = t.TextDim });
            const float bGap = 0.014f;
            float bw = (WsRightW - 2f * bGap) / 3f;
            _wsReset = p.Add(new UiButton("RESET LAYOUT", WsResetLayout) { Rect = new Rect(WsRightX, bottomY, bw, t.ButtonHeight), TextSize = 0.0118f });
            _wsCancel = p.Add(new UiButton("CANCEL", WsCancel) { Rect = new Rect(WsRightX + bw + bGap, bottomY, bw, t.ButtonHeight), TextSize = 0.0125f });
            _wsApply = p.Add(new UiButton("APPLY", WsApply, UiButtonStyle.Primary) { Rect = new Rect(WsRightX + 2f * (bw + bGap), bottomY, bw, t.ButtonHeight), TextSize = 0.0125f });
        }

        private static float Steps(float v) => Mathf.Round(v / 0.05f) * 0.05f;

        /// The draft's rings from the catalog (the app's items and the app plugins' tiles, with the draft's icons), then every
        /// control and the twin.
        private void RefreshWristPage()
        {
            if (_wd == null || !_wsBuilt) return;
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

            FillSlots(_wsInnerBtns, _wsInner);
            FillSlots(_wsOuterBtns, _wsOuter);
            _wsInnerCap.Set($"INNER RING · {_wsInner.Count} ({UiWristMenu.MinInner} to {UiWristMenu.MaxInner})");
            int more = UiWristMenu.MaxOuter - _wsOuter.Count;
            _wsOuterCap.Set($"OUTER RING · {_wsOuter.Count} · " + (more > 0 ? more + " MORE FIT" : "FULL"));
            RefreshSelection();
            var add = new List<UiListItem>(_wsOff.Count);
            foreach (var id in _wsOff)
            {
                var m = _wsById[id];
                add.Add(new UiListItem { Icon = m.Glyph, Title = m.Label, Sub = m.Plugin ? "plugin · " + m.Source : "app", Badge = "+ ADD", Tag = id });
            }
            _wsAdd.SelectedIndex = -1; // a tap adds the row's item: no row stays chosen
            _wsAdd.SetItems(add, keepScroll: true);
            _wsAddCap.Set(_wsOff.Count > 0 ? $"OFF THE MENU · {_wsOff.Count} · TAP ONE TO ADD IT" : "OFF THE MENU");
            FillTwin();
            RefreshWristState();
        }

        private void FillSlots(List<UiButton> pool, List<string> ring)
        {
            for (int k = 0; k < pool.Count; k++)
            {
                var b = pool[k];
                bool has = k < ring.Count;
                b.SetVisible(has);
                if (!has) continue;
                var m = _wsById[ring[k]];
                b.SetText(m.Glyph + " " + m.Label);
                b.SetSelected(ring[k] == _wsSel);
            }
        }

        /// The chosen item's line, the actions it allows (dimmed ones say why when pressed), its icon row.
        private void RefreshSelection()
        {
            var t = UiTheme.Current;
            int ii = _wsSel != null ? _wsInner.IndexOf(_wsSel) : -1, oi = _wsSel != null ? _wsOuter.IndexOf(_wsSel) : -1;
            bool any = ii >= 0 || oi >= 0;
            var m = any ? _wsById[_wsSel] : null;
            if (!any) _wsSelLine.Set("Choose a tile above: then move it, take it to the other ring, or hide it.", t.TextDim);
            else _wsSelLine.Set(UiChatView.Esc(m.Label) + " · " + (ii >= 0 ? $"inner ring, {ii + 1} of {_wsInner.Count}" : $"outer ring, {oi + 1} of {_wsOuter.Count}")
                + (m.Plugin ? " · plugin " + UiChatView.Esc(m.Source) : ""), t.Text);
            int ringCount = ii >= 0 ? _wsInner.Count : _wsOuter.Count;
            _wsLeft.SetDimmed(!any || ringCount < 2);
            _wsRight.SetDimmed(!any || ringCount < 2);
            _wsToInner.SetDimmed(oi < 0 || _wsInner.Count >= UiWristMenu.MaxInner);
            _wsToOuter.SetDimmed(ii < 0 || _wsInner.Count <= UiWristMenu.MinInner || _wsOuter.Count >= UiWristMenu.MaxOuter);
            _wsHide.SetDimmed(!any || _wsSel == ControlItemId || ii >= 0 && _wsInner.Count <= UiWristMenu.MinInner);
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

        // ------------------------------------------------------------------ the page's actions (all on the draft)

        private void WsPick(bool outer, int slot)
        {
            var ring = outer ? _wsOuter : _wsInner;
            if (_wd == null || slot >= ring.Count) return;
            _wsSel = ring[slot];
            FillSlots(_wsInnerBtns, _wsInner);
            FillSlots(_wsOuterBtns, _wsOuter);
            RefreshSelection();
            _wsTwin?.Ping(SelectedPart());
        }

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

        private void WsMove(int dir)
        {
            if (_wsSel == null) { Toast("Choose a tile first.", "info"); return; }
            string id = _wsSel;
            WsEdit((inner, outer, off) =>
            {
                var ring = inner.Contains(id) ? inner : outer;
                if (ring.Count < 2) return "It is the only tile on its ring.";
                int i = ring.IndexOf(id), j = (i + dir + ring.Count) % ring.Count;
                ring.RemoveAt(i);
                ring.Insert(j, id); // past either end it goes round to the other end, as on the ring
                return null;
            });
        }

        private void WsToInner()
        {
            if (_wsSel == null) { Toast("Choose a tile first.", "info"); return; }
            string id = _wsSel;
            WsEdit((inner, outer, off) =>
            {
                if (inner.Contains(id)) return "It is on the inner ring already.";
                if (inner.Count >= UiWristMenu.MaxInner) return $"The inner ring holds {UiWristMenu.MaxInner} at most: move one out first.";
                outer.Remove(id);
                inner.Add(id);
                return null;
            });
        }

        private void WsToOuter()
        {
            if (_wsSel == null) { Toast("Choose a tile first.", "info"); return; }
            string id = _wsSel;
            WsEdit((inner, outer, off) =>
            {
                if (outer.Contains(id)) return "It is on the outer ring already.";
                if (inner.Count <= UiWristMenu.MinInner) return $"The inner ring keeps {UiWristMenu.MinInner} at least.";
                if (outer.Count >= UiWristMenu.MaxOuter) return $"The outer ring holds {UiWristMenu.MaxOuter} at most: hide one first.";
                inner.Remove(id);
                outer.Add(id);
                return null;
            });
        }

        private void WsHide()
        {
            if (_wsSel == null) { Toast("Choose a tile first.", "info"); return; }
            string id = _wsSel;
            if (id == ControlItemId) { Toast("CONTROL stays on the menu: it is the way back to this page.", "warn"); return; }
            WsEdit((inner, outer, off) =>
            {
                if (inner.Contains(id) && inner.Count <= UiWristMenu.MinInner) return $"The inner ring keeps {UiWristMenu.MinInner} at least.";
                inner.Remove(id);
                outer.Remove(id);
                off.Insert(0, id);
                _wsSel = null;
                return null;
            });
        }

        private void WsAdd(string id)
        {
            WsEdit((inner, outer, off) =>
            {
                if (!off.Remove(id)) return null;
                if (outer.Count < UiWristMenu.MaxOuter) outer.Add(id);
                else if (inner.Count < UiWristMenu.MaxInner) inner.Add(id);
                else { off.Insert(0, id); return "Both rings are full: hide a tile first."; }
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

        /// MENU SIZE, GLASS, the core's parts, the fold after a tap: the twin takes them at once (no labels made again).
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

        /// The plugins' tiles changed: an open page takes them into its copy.
        private void OnMenuCatalogChanged()
        {
            if (_wd != null) RefreshWristPage();
        }

        // ------------------------------------------------------------------ self-tests

        public UiWristMenu WristTwinForTest => _wsTwin;
        public WristMenuSettings WristDraftForTest => _wd;
        public void WristPageSelectForTest(string id) { _wsSel = id; RefreshWristPage(); }
    }
}
