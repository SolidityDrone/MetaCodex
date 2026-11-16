using System;
using System.Collections.Generic;
using MetaCodex.Scripting;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// What plugins (Lua, ScriptHost) show: their windows (view specs drawn by ShellViews), their tiles on the dock and
    /// the wrist menu, callouts and sounds. The host keeps the specs and the timers; the shell only draws them and sends
    /// the user's actions back (AgentView.OnEvent -> ScriptHost.WindowEvent, panel Opened / Closed -> "open" / "close").
    public sealed partial class MetaCodexShell
    {
        // ------------------------------------------------------------------ small services

        double IScriptServices.WallClock() => (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

        void IScriptServices.PlaySound(string name)
        {
            if (!string.IsNullOrEmpty(name)) UiSounds.Fire(name);
        }

        /// A callout by the Whisp: a toast (tap = onTap), an optional sound and an optional spoken line.
        void IScriptServices.Notify(string text, string tone, string sound, string speak, Action onTap)
        {
            Log("plugin notify: " + text);
            if (Toasts != null)
            {
                if (Toasts.Register == null && Router != null) { Toasts.Register = Router.Register; Toasts.Unregister = Router.Unregister; }
                Toasts.Show(onTap != null ? text + "  ›" : text, string.IsNullOrEmpty(tone) ? "info" : tone, 6f, onTap);
            }
            if (!string.IsNullOrEmpty(sound)) UiSounds.Fire(sound);
            if (!string.IsNullOrEmpty(speak)) VoiceSay(speak);
        }

        // ------------------------------------------------------------------ windows

        /// Makes or replaces a plugin window. Not open and not built yet: only the spec is kept (built on the first open).
        void IScriptServices.PluginWindow(string windowId, string specJson, bool open)
        {
            if (string.IsNullOrEmpty(windowId)) return;
            JObject spec;
            try { spec = JObject.Parse(specJson ?? "{}"); }
            catch (Exception ex) { Log("plugin window " + windowId + ": bad spec: " + ex.Message); return; }
            var v = FindView(windowId);
            if (v == null) { v = new AgentView { Id = windowId }; _viewList.Add(v); }
            v.Spec = spec;
            v.Title = (string)spec["title"] ?? windowId;
            v.Source = "plugin";
            v.At = DateTime.Now;
            v.Error = null;
            string wid = windowId;
            v.OnEvent = (type, id, value) => Scripts?.WindowEvent(wid, type, id, value);
            // the same window made again soon after its plugin unloaded (a reload: the agent edited main.lua): it comes
            // back where it was, open if it was open
            var gone = default((Vector3 pos, Quaternion rot, bool open, float at));
            bool back = !_panels.ContainsKey(v.PanelId) && _pluginWinGone.TryGetValue(windowId, out gone) && Time.unscaledTime - gone.at < 10f;
            if (back) { _pluginWinGone.Remove(windowId); open |= gone.open; }
            if (open || _panels.ContainsKey(v.PanelId))
            {
                try
                {
                    BuildViewPanel(v, open);
                    if (back && _panels.TryGetValue(v.PanelId, out var np) && np != null) { np.transform.SetPositionAndRotation(gone.pos, gone.rot); _placed.Add(v.PanelId); }
                }
                catch (Exception ex) { v.Error = ex.Message; Log("plugin window " + windowId + " failed: " + ex); }
            }
            TrimViewPanels();
            RefreshWorkspaceViews();
        }

        /// LIVE texts: sets the labels of a built window (and keeps the spec current for the next build). False: not built.
        bool IScriptServices.PluginWindowText(string windowId, IDictionary<string, string> texts)
        {
            var v = FindView(windowId);
            if (v == null || v.Source != "plugin" || !_panels.ContainsKey(v.PanelId)) return false;
            if (texts == null) return true;
            foreach (var kv in texts)
            {
                if (!v.Live.TryGetValue(kv.Key, out var lab) || lab == null) return false; // not a live text of what is built: the host sends the whole spec
                string shown = MdLite(kv.Value ?? "");
                if (lab.Text != shown) lab.Set(shown);
            }
            if (v.Spec != null && v.Spec["components"] is JArray comps)
                foreach (var tok in comps)
                    if (tok is JObject c && (string)c["id"] is string cid && texts.TryGetValue(cid, out var txt)) c["text"] = txt;
            return true;
        }

        /// mode 1 = open, 0 = close, 2 = toggle; returns the open state after.
        bool IScriptServices.PluginWindowShow(string windowId, int mode)
        {
            var v = FindView(windowId);
            if (v == null || v.Source != "plugin" || v.Spec == null) return false;
            bool open = PluginPanelOpen(v);
            bool want = mode == 2 ? !open : mode == 1;
            try
            {
                if (want)
                {
                    if (!_panels.ContainsKey(v.PanelId)) BuildViewPanel(v, true);
                    else OpenPanel(v.PanelId);
                }
                else if (open) ClosePanel(v.PanelId);
            }
            catch (Exception ex) { v.Error = ex.Message; Log("plugin window " + windowId + " failed: " + ex); }
            return PluginPanelOpen(v);
        }

        bool IScriptServices.PluginWindowIsOpen(string windowId)
        {
            var v = FindView(windowId);
            return v != null && v.Source == "plugin" && PluginPanelOpen(v);
        }

        void IScriptServices.PluginWindowRemove(string windowId)
        {
            var v = FindView(windowId);
            if (v == null || v.Source != "plugin") return;
            v.OnEvent = null;
            v.Shown = false;
            if (_panels.TryGetValue(v.PanelId, out var p) && p != null)
                _pluginWinGone[windowId] = (p.transform.position, p.transform.rotation, p.IsOpen, Time.unscaledTime);
            DestroyViewPanel(v.PanelId);
            _viewList.Remove(v);
            RefreshWorkspaceViews();
        }

        /// Plugin windows removed in this session (pose, open, when): a reload puts them back in place.
        private readonly Dictionary<string, (Vector3 pos, Quaternion rot, bool open, float at)> _pluginWinGone = new Dictionary<string, (Vector3, Quaternion, bool, float)>();

        private bool PluginPanelOpen(AgentView v) => _panels.TryGetValue(v.PanelId, out var p) && p != null && p.IsOpen;

        /// Called by BuildViewPanel for a plugin window's new panel: its open / close reach the plugin.
        private void HookPluginWindow(AgentView v, UiPanel p)
        {
            p.Opened += _ => PluginShownSync(v, p);
            p.Closed += _ => PluginShownSync(v, p);
        }

        /// Sends "open" / "close" to the plugin when the panel's open state differs from the one it last got. The
        /// event is queued for the next frame: the host may be inside a Lua call (ui.open) when the panel opens.
        private void PluginShownSync(AgentView v, UiPanel p)
        {
            bool now = p != null && p.IsOpen;
            if (v.Shown == now) return;
            v.Shown = now;
            string wid = v.Id, type = now ? "open" : "close";
            _main.Enqueue(() => { if (FindView(wid) == v && v.OnEvent != null) Scripts?.WindowEvent(wid, type, "", ""); });
        }

        // ------------------------------------------------------------------ tiles (dock + wrist menu)

        private IReadOnlyList<PluginTile> _pluginTiles = new PluginTile[0];       // what the plugins want now
        private IReadOnlyList<PluginTile> _pluginTilesApplied = new PluginTile[0]; // what the menus were last built with
        private float _pluginTilesDirtyAt;
        private bool _pluginTilesPending, _wristStale;
        private bool _pluginTickHooked;

        /// The whole list of plugin tiles now. The DOCK and the wrist menu are rebuilt once, 0.3 s after the last change.
        void IScriptServices.SetPluginTiles(IReadOnlyList<PluginTile> tiles)
        {
            var copy = new List<PluginTile>(tiles != null ? tiles.Count : 0);
            if (tiles != null) for (int i = 0; i < tiles.Count; i++) if (tiles[i] != null) copy.Add(tiles[i]);
            if (SameTiles(copy, _pluginTiles)) return;
            _pluginTiles = copy;
            _pluginTilesDirtyAt = Time.unscaledTime;
            _pluginTilesPending = true;
            // per frame through the script host's tick (the shell calls it every frame; nothing to do = two bool checks)
            if (!_pluginTickHooked && Scripts != null) { _pluginTickHooked = true; Scripts.AfterTick += TickPluginUi; }
        }

        private static bool SameTiles(IReadOnlyList<PluginTile> a, IReadOnlyList<PluginTile> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                var x = a[i]; var y = b[i];
                if (x.Owner != y.Owner || x.Id != y.Id || x.Label != y.Label || x.Glyph != y.Glyph || x.Icon != y.Icon || x.Window != y.Window) return false;
            }
            return true;
        }

        /// Adds the app plugins' tiles to the main menu's catalog (MenuCatalog), by default on the outer ring of the wrist
        /// menu. Only APPS plugins (PluginKindOf) have tiles on the menu (owner 2026-11-16: "installed plugins, not gesture
        /// plugins, just app-level plugins like the timer"). A tile's icon: the user's pick (CONTROL > WRIST MENU, `ws`),
        /// else the plugin's if it is one of the generic icons (UiIconAtlas.PluginIcons), else the puzzle piece "plugin".
        private void AddPluginMenuItems(List<MainMenuItem> list, bool outer, WristMenuSettings ws)
        {
            var tiles = _pluginTiles;
            _pluginTilesApplied = tiles;
            for (int i = 0; i < tiles.Count; i++)
            {
                var tile = tiles[i];
                string owner = tile.Owner, id = tile.Id;
                var entry = FindPluginEntry(owner);
                if (entry != null && PluginKindOf(entry) != KindApps) continue;
                string itemId = PluginTileId(owner, id);
                string own = PluginIconOrDefault(tile.Icon);
                Func<bool> lit = null;
                if (!string.IsNullOrEmpty(tile.Window))
                {
                    string pid = "view-" + tile.Window; // built once here: Lit runs every frame
                    lit = () => IsPanelOpen(pid);
                }
                list.Add(new MainMenuItem
                {
                    Id = itemId,
                    Glyph = string.IsNullOrEmpty(tile.Glyph) ? "◆" : tile.Glyph,
                    Icon = PluginIconOrDefault(ws?.IconOf(itemId) ?? own),
                    OwnIcon = own,
                    Label = string.IsNullOrEmpty(tile.Label) ? id : tile.Label,
                    Act = () => Scripts?.TileTapped(owner, id),
                    Lit = lit,
                    Outer = outer,
                    Plugin = true,
                    Source = entry != null && !string.IsNullOrEmpty(entry.Title) ? entry.Title : owner,
                });
            }
        }

        /// A plugin tile's id in the saved layout.
        public static string PluginTileId(string plugin, string tile) => "plugin:" + plugin + "/" + tile;

        /// The icon a plugin's tile shows: one of the generic icons, else the puzzle piece.
        public static string PluginIconOrDefault(string icon) =>
            !string.IsNullOrEmpty(icon) && Array.IndexOf(UiIconAtlas.PluginIcons, icon) >= 0 ? icon : "plugin";

        /// The plugin (else the script) of that name: a tile's owner.
        private ScriptHost.Entry FindPluginEntry(string name)
        {
            if (Scripts == null || string.IsNullOrEmpty(name)) return null;
            var all = Scripts.Entries;
            ScriptHost.Entry script = null;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Name != name) continue;
                if (all[i].Kind == "plugin") return all[i];
                script ??= all[i];
            }
            return script;
        }

        private void TickPluginUi()
        {
            if (_pluginTilesPending)
            {
                if (Time.unscaledTime - _pluginTilesDirtyAt < 0.3f) return;
                _pluginTilesPending = false;
                try { ApplyPluginTiles(); }
                catch (Exception ex) { Log("plugin tiles: rebuild failed: " + ex); }
            }
            // the wrist menu is only rebuilt while it is the main menu (its build is slow): when the user switches to it
            if (_wristStale && Settings.wristMenu && Wrist != null)
            {
                try { RebuildWrist(); }
                catch (Exception ex) { _wristStale = false; Log("plugin tiles: wrist rebuild failed: " + ex); }
            }
        }

        /// Self-tests: the pending tile rebuild now.
        public void FlushPluginTilesForTest() { _pluginTilesDirtyAt = -10f; TickPluginUi(); }

        private void ApplyPluginTiles()
        {
            if (SameTiles(_pluginTiles, _pluginTilesApplied)) return;
            RebuildMainMenus();
            OnMenuCatalogChanged(); // the WRIST MENU page, when it shows, takes the new tiles into its copy
        }

        /// The main menu's table changed (a plugin's tile, the user's layout or icons): the dock and the wrist menu are built
        /// again from it. The dock is destroyed and built again (pose and open state stay); the wrist menu takes the new items
        /// in place, after the dock is gone (so its mode switch leaves the dock's state alone), and only while it is the main
        /// menu (else when the user switches to it).
        private void RebuildMainMenus()
        {
            bool hadDock = false, dockOpen = false;
            Vector3 pos = default; Quaternion rot = default;
            if (_panels.TryGetValue("dock", out var old) && old != null)
            {
                hadDock = true;
                pos = old.transform.position; rot = old.transform.rotation; dockOpen = old.IsOpen;
                DestroyViewPanel("dock"); // the generic panel teardown (router, slots, tables)
            }
            if (Wrist != null)
            {
                if (Settings.wristMenu) RebuildWrist();
                else _wristStale = true;
            }
            if (hadDock)
            {
                _selectButton = _focusButton = null; // the new dock sets them again unless the user took SELECT / FOCUS off the menu
                BuildDock();
                if (_panels.TryGetValue("dock", out var nd) && nd != null)
                {
                    nd.transform.SetPositionAndRotation(pos, rot);
                    if (dockOpen) { nd.gameObject.SetActive(true); nd.ForceRefresh(); }
                }
            }
        }

        /// The wrist menu takes the new table in place (ShellWrist.RebuildWristItems: its items only, not the whole menu). It
        /// used to be destroyed and made again through BuildWrist, but BuildWrist runs once per session now (the wrist menu
        /// is made lazily, in WRIST mode only): a second call does nothing, and the menu would have been left gone.
        private void RebuildWrist()
        {
            if (Wrist == null) return;
            _wristStale = false;
            RebuildWristItems();
        }
    }
}
