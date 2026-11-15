using System;
using System.Collections.Generic;
using System.Linq;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// CONTROL (owner 2026-11-11, docs/control-center.md): one place for everything that sets the workbench up. Each
    /// category keeps its own window (SETTINGS' pages, VOICE, AGENTS, MQH, VAULT, SYSTEM: their tabs and tests);
    /// a sidebar rides on the left of the one shown, with the ten categories in three groups and a live status each.
    /// One category at a time: another one takes the shown window's place (same left and top edges, and since all six
    /// windows have one size, ControlPageW x ControlPageH, the same rectangle), the rail stays.
    /// Work windows (terminals, files, threads, web, clipboard) stay windows of their own.
    public sealed partial class MetaCodexShell
    {
        public sealed class ControlCategory
        {
            public string Key, Label, Glyph, Group, Panel, Blurb;
            public int Tab = -1;                 // the SETTINGS tab, for its pages
            public Func<string> Status;          // a few words on the right of the rail
        }

        public const string ControlId = "control";
        public const float RailW = 0.17f, RailGap = 0.010f, RailYaw = 12f;
        public static readonly string[] ControlPages = { "settings", "voice", "agents", "mqh", "vault", "system" };
        public static bool IsControlPage(string id) => id != null && Array.IndexOf(ControlPages, id) >= 0;
        /// The one size of CONTROL's six windows: a category taking another's place never grows over a neighbour.
        public const float ControlPageW = 0.76f, ControlPageH = 0.66f;

        private UiPanel _rail;
        private string _railPage;               // the window the rail is beside
        private string _controlLast = "display";
        private bool _railDragged, _settingsShown;
        private float _nextRailStatus;
        private UiLabel _settingsBlurb;
        private List<ControlCategory> _categories = new List<ControlCategory>();
        private readonly List<(ControlCategory cat, UiButton button, UiLabel name, UiLabel status)> _railRows = new List<(ControlCategory, UiButton, UiLabel, UiLabel)>();

        public IReadOnlyList<ControlCategory> ControlCategories => _categories;
        public UiPanel ControlRail => _rail;
        public string ControlRailPage => _rail != null && _rail.IsOpen ? _railPage : null;

        // settings' own tab numbers (BuildSettings)
        private const int SetHands = 0, SetDisplay = 1, SetFocusTab = 2, SetLook = 3, SetAbout = 4;

        private void DefineCategories()
        {
            string On(bool b) => b ? "on" : "off";
            _categories = new List<ControlCategory>
            {
                new ControlCategory { Key = "display", Label = "DISPLAY", Glyph = "◧", Group = "THIS HEADSET", Panel = "settings", Tab = SetDisplay,
                    Blurb = "Where windows go and how big they are: fixed or floating layout, distance, scale, bending, the main menu, saved layouts.",
                    Status = () => Settings.layout == "float" ? "float" : "fixed" },
                new ControlCategory { Key = "look", Label = "LOOK & SOUND", Glyph = "◈", Group = "THIS HEADSET", Panel = "settings", Tab = SetLook,
                    Blurb = "How the glass looks and sounds: opacity, foveation, sounds and volume, the Whisp.",
                    Status = () => Settings.sounds ? "sound" : "muted" },
                new ControlCategory { Key = "focus", Label = "FOCUS", Glyph = "◐", Group = "THIS HEADSET", Panel = "settings", Tab = SetFocusTab,
                    Blurb = "Ambient focus: the room fades beyond a dome around you; real things in front of windows.",
                    Status = () => On(Settings.focusEnabled) },
                new ControlCategory { Key = "hands", Label = "HANDS", Glyph = "✋", Group = "THIS HEADSET", Panel = "settings", Tab = SetHands,
                    Blurb = "How your hands drive it: pinch or poke, moving windows, the ray, the clap; GESTURE STUDIO: your own gestures.",
                    Status = () => Settings.gestures.clickMode == 1 ? "poke" : Settings.gestures.clickMode == 2 ? "both" : "pinch" },
                new ControlCategory { Key = "voice", Label = "VOICE", Glyph = "♫", Group = "AI", Panel = "voice",
                    Status = () => Qvac == null ? "off" : Qvac.IsRunning ? "ready" : Qvac.State.ToString().ToLowerInvariant() },
                new ControlCategory { Key = "agents", Label = "AGENTS", Glyph = "◆", Group = "AI", Panel = "agents",
                    Status = () => { int n = _providerStates.Count(x => (string)x["status"] == "ready" || (string)x["status"] == "authenticated"); return n > 0 ? n + " ready" : "sign in"; } },
                new ControlCategory { Key = "mqh", Label = "MQH", Glyph = "⬡", Group = "AI", Panel = "mqh",
                    Status = () => MqhHost != null ? MqhHost.State.ToString().ToLowerInvariant() : Mqh != null && Mqh.Online ? "online" : "offline" },
                new ControlCategory { Key = "vault", Label = "VAULT", Glyph = "⚿", Group = "AI", Panel = "vault",
                    Status = () => Vault != null ? AI.ThreadPulse.Plural(Vault.Entries.Count, "key") : "locked" },
                new ControlCategory { Key = "system", Label = "SYSTEM", Glyph = "⬢", Group = "LINUX & APP", Panel = "system",
                    Status = () => FirstUseStatus.StartsWith("done") ? "ready" : FirstUseStatus.StartsWith("running") ? "setting up" : FirstUseStatus.StartsWith("failed") ? "setup failed" : "—" },
                new ControlCategory { Key = "about", Label = "ABOUT", Glyph = "✦", Group = "LINUX & APP", Panel = "settings", Tab = SetAbout,
                    Blurb = "MetaCodex, the licences of what it carries, the app's permissions.",
                    Status = () => Application.version },
            };
        }

        // ------------------------------------------------------------------ the rail

        private void BuildControl()
        {
            DefineCategories();
            _railRows.Clear();
            var t = UiTheme.Current;
            const float handle = 0.016f, pad = 0.008f, rowH = 0.034f, rowGap = 0.003f, capH = 0.017f, titleH = 0.026f;
            int groups = _categories.Select(c => c.Group).Distinct().Count();
            float h = handle + pad + titleH + groups * (capH + 0.004f) + _categories.Count * (rowH + rowGap) + pad * 1.5f;
            // as tall as the page beside it (owner 2026-11-13: "the sidebar is shorter than the window we show content in");
            // top edges are level (RailPose), so the bottoms are too; the rows stay at the top
            h = Mathf.Max(h, ControlPageH);
            var rail = UiPanel.Create<UiPanel>(ControlId, "", RailW, h, _root, header: false);
            rail.Closable = false;
            rail.AddGrabHandle(handle); // pinch its bar to move the control center (its window comes along)
            _panels[ControlId] = rail;
            Router.Register(rail);
            float y = handle + pad * 0.6f;
            rail.Add(new UiLabel("CONTROL", 0.0145f) { Rect = new Rect(0.012f, y, RailW - 0.024f, titleH), UseDisplay = true, Color = t.TextAccent });
            y += titleH;
            string group = null;
            foreach (var cat in _categories)
            {
                if (cat.Group != group)
                {
                    group = cat.Group;
                    rail.Add(new UiLabel(group, 0.0098f) { Rect = new Rect(0.012f, y + 0.002f, RailW - 0.024f, capH), UseDisplay = true, Color = t.TextDim });
                    y += capH + 0.004f;
                }
                var c = cat;
                var r = new Rect(pad, y, RailW - pad * 2f, rowH);
                var b = rail.Add(new UiButton("", () => OpenControl(c.Key), UiButtonStyle.Tab) { Rect = r });
                // the console voice (Rajdhani capitals, as the title and the group captions): "◈  LOOK & SOUND", the longest, is ~7 cm of 10
                var name = rail.Add(new UiLabel(c.Glyph + "  " + c.Label, 0.0118f) { Rect = new Rect(r.x + 0.008f, r.y, r.width * 0.66f, r.height), UseDisplay = true, Color = t.Text });
                var st = rail.Add(new UiLabel("", 0.0092f) { Rect = new Rect(r.x + r.width * 0.60f, r.y, r.width * 0.40f - 0.006f, r.height), Color = t.TextDim, Align = TextAlignmentOptions.MidlineRight });
                _railRows.Add((c, b, name, st));
                y += rowH + rowGap;
            }
            rail.Tick += _ => { if (Time.unscaledTime >= _nextRailStatus) { _nextRailStatus = Time.unscaledTime + 2f; RefreshRail(); } };
            rail.gameObject.SetActive(false);
            _rail = rail;
            AdaptSettingsToRail();
        }

        /// SETTINGS' own tab bar goes (the rail is the navigation); in its place a line says what the page is for.
        private void AdaptSettingsToRail()
        {
            if (_settingsTabs == null || !_panels.TryGetValue("settings", out var sp)) return;
            var t = UiTheme.Current;
            foreach (var b in sp.Elements.OfType<UiButton>().Where(x => x.Style == UiButtonStyle.Tab && _settingsTabs.Names.Contains(x.Text)).ToList()) b.SetVisible(false);
            var c = sp.Content;
            _settingsBlurb = sp.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(c.x, c.y, c.width, t.ButtonHeight), Color = t.TextDim, Wrap = true, Align = TextAlignmentOptions.MidlineLeft });
            var before = _settingsTabs.OnChanged;
            _settingsTabs.OnChanged = i =>
            {
                before?.Invoke(i);
                ShowSettingsBlurb(i);
                // a page chosen any way (rail, ui.open("display"), voice, a button) is the one CONTROL comes back to
                var page = _categories.FirstOrDefault(k => k.Panel == "settings" && k.Tab == i);
                if (page != null && ControlRailPage == "settings") _controlLast = page.Key;
                RefreshRail();
            };
            ShowSettingsBlurb(_settingsTabs.Selected);
        }

        private void ShowSettingsBlurb(int tab)
        {
            var cat = _categories.FirstOrDefault(c => c.Panel == "settings" && c.Tab == tab);
            _settingsBlurb?.Set(cat != null ? $"<color=#8FEBFF>{cat.Label}</color>  ·  {cat.Blurb}" : "");
            if (cat != null && _panels.TryGetValue("settings", out var sp)) sp.SetTitle("SETTINGS · " + cat.Label);
        }

        /// The category shown now (from the window beside the rail and, for SETTINGS, its page).
        public string ControlCategoryNow()
        {
            string pid = ControlRailPage;
            if (pid == null) return null;
            if (pid == "settings") return _categories.FirstOrDefault(c => c.Panel == "settings" && c.Tab == (_settingsTabs?.Selected ?? -1))?.Key;
            return _categories.FirstOrDefault(c => c.Panel == pid)?.Key;
        }

        private void RefreshRail()
        {
            if (_rail == null) return;
            string now = ControlCategoryNow();
            foreach (var (cat, button, name, status) in _railRows)
            {
                bool sel = cat.Key == now;
                button.SetSelected(sel);
                int need = PanelNeedsStep(cat.Panel);
                bool locked = need > 0 && SetupLocked(need); // the first start's setup (ShellSetupLocks)
                button.SetDimmed(locked);
                name.Set(cat.Glyph + "  " + cat.Label, sel ? UiTheme.Current.Text : Color.Lerp(UiTheme.Current.Text, UiTheme.Current.TextDim, locked ? 0.7f : 0.3f));
                string s = "";
                try { s = locked ? "🔒 step " + need : cat.Status?.Invoke() ?? ""; } catch (Exception) { }
                status.Set(UiChatView.Esc(s.Length > 12 ? s.Substring(0, 11) + "…" : s));
            }
        }

        // ------------------------------------------------------------------ opening a category

        /// Opens CONTROL on a category (null: the last one); another category takes the shown window's place.
        public bool OpenControl(string key)
        {
            var cat = _categories.FirstOrDefault(c => c.Key == (key ?? _controlLast)) ?? _categories.FirstOrDefault();
            if (cat == null) return false;
            // the first start's setup (ShellSetupLocks): AGENTS waits for MQH; CONTROL keeps its last category
            int need = PanelNeedsStep(cat.Panel);
            if (need > 0 && SetupBlocks(need, cat.Label)) return false;
            _controlLast = cat.Key;
            if (cat.Tab >= 0) _settingsShown = true; // a chosen SETTINGS page wins over the first-time DISPLAY
            // a page of the window already shown (DISPLAY -> LOOK): only its page changes, no new cast
            if (ControlRailPage == cat.Panel && _panels.TryGetValue(cat.Panel, out var shownP) && shownP.IsOpen)
            {
                if (cat.Tab >= 0) _settingsTabs?.Select(cat.Tab);
                UiSounds.Fire("click");
                RefreshRail();
                return true;
            }
            bool ok = OpenPanel(cat.Panel);
            if (ok && cat.Tab >= 0) _settingsTabs?.Select(cat.Tab);
            RefreshRail();
            return ok;
        }

        /// The menu's CONTROL: closes the control center when it shows, else opens it on the last category.
        public void ToggleControl()
        {
            var shown = ShownControlPage(null);
            if (shown != null) { shown.Close(); return; }
            OpenControl(null);
        }

        /// The control window shown now (beside the rail), else any open one; `except` is left out.
        private UiPanel ShownControlPage(string except)
        {
            if (_railPage != null && _railPage != except && _panels.TryGetValue(_railPage, out var rp) && rp.IsOpen) return rp;
            foreach (var pid in ControlPages)
                if (pid != except && _panels.TryGetValue(pid, out var q) && q.IsOpen) return q;
            return null;
        }

        private void CloseOtherControlPages(string keep)
        {
            foreach (var pid in ControlPages)
                if (pid != keep && _panels.TryGetValue(pid, out var q) && q.IsOpen) q.Close();
        }

        /// `next` takes `prev`'s place: the same left edge and top edge (the rail does not move); FLOAT: its slot.
        private void TakePlaceOf(UiPanel next, UiPanel prev)
        {
            var pt = prev.transform;
            var local = new Vector3((next.Width - prev.Width) * 0.5f, (prev.Height - next.Height) * 0.5f, 0f);
            next.transform.SetPositionAndRotation(pt.TransformPoint(local), pt.rotation);
            next.SetBend(prev.BendRadius);
            next.SnapBend();
            if (Floating && _carousel != null && _slots.TryGetValue(pt, out var ps))
            {
                float r = Mathf.Max(_ringRadius, FloatRadius());
                _slots.Remove(pt);
                _slots[next.transform] = (ps.angle + HalfArc(next, r) - HalfArc(prev, r), ps.height);
            }
        }

        /// After the window opened: the rail beside it (and, FLOAT, room for it on the ring).
        private void AttachRail(string pageId)
        {
            if (_rail == null) return;
            _railPage = pageId;
            var cat = pageId == "settings" ? null : _categories.FirstOrDefault(c => c.Panel == pageId);
            if (cat != null) _controlLast = cat.Key;
            // the first time SETTINGS shows, it shows DISPLAY (its first tab, HANDS, is not where people look first)
            if (pageId == "settings" && !_settingsShown) { _settingsShown = true; if (cat == null && _settingsTabs != null && _settingsTabs.Selected == SetHands) _settingsTabs.Select(SetDisplay); }
            if (pageId == "settings") { var page0 = _categories.FirstOrDefault(c => c.Panel == "settings" && c.Tab == (_settingsTabs?.Selected ?? -1)); if (page0 != null) _controlLast = page0.Key; }
            if (_panels.TryGetValue(pageId, out var page)) { var (pos, rot) = RailPose(page); _rail.transform.SetPositionAndRotation(pos, rot); }
            if (!_rail.IsOpen) _rail.Open();
            RefreshRail();
        }

        /// Left of the page, top edges level, turned a little towards the user round its right edge; on a bent page it
        /// leaves from the page's own (curved) left edge.
        private (Vector3 pos, Quaternion rot) RailPose(UiPanel page)
        {
            float s = page.RestLossyScale.x; // the rest scale: steady while a window casts or closes
            float bend = page.BendNow;
            float th = bend > 0.05f ? page.Width * 0.5f * s / bend : 0f; // radians the left half turns through
            var edge = page.SurfacePoint(new Vector2(0f, 0f));         // its top-left corner, on the surface
            var rot = page.transform.rotation * Quaternion.Euler(0f, -(th * Mathf.Rad2Deg + RailYaw), 0f);
            float rs = _rail.RestLossyScale.x;
            var pos = edge + rot * new Vector3(-(RailGap * s + RailW * 0.5f * rs), -_rail.Height * 0.5f * rs, 0f);
            return (pos, rot);
        }

        /// Whether `p` is the window the rail rides beside (FLOAT reserves the rail's room on its left).
        private bool RailBeside(UiPanel p) => p != null && _rail != null && _rail.IsOpen && _railPage == p.Id;
        private float RailGapDeg(float r) => RailGap * (_rail != null ? _rail.RestLossyScale.x : 1f) / Mathf.Max(r, 0.1f) * Mathf.Rad2Deg;
        private float RailShift(float r) => _rail != null ? HalfArc(_rail, r) + RailGapDeg(r) * 0.5f : 0f;
        private bool RailCarries(UiPanel p) => RailBeside(p) && Router != null && Router.IsDragging(_rail);

        /// Every frame: the rail follows its window; carried by its bar, the window follows the rail. It closes with it.
        private void UpdateControlRail()
        {
            if (_rail == null) return;
            var page = _railPage != null && _panels.TryGetValue(_railPage, out var pg) ? pg : null;
            if (page == null || !page.IsOpen)
            {
                if (!_rail.IsOpen) return;
                var other = ShownControlPage(null);
                if (other != null) AttachRail(other.Id);
                else _rail.Close();
                return;
            }
            if (!_rail.IsOpen) return;
            if (Router != null && Router.IsDragging(_rail))
            {
                var want = (_rail.transform.position, _rail.transform.rotation);
                var (_, fr) = RailPose(page);
                page.transform.rotation = want.rotation * Quaternion.Inverse(fr) * page.transform.rotation;
                var (fp, _) = RailPose(page);
                page.transform.position += want.position - fp;
                _railDragged = true;
                return;
            }
            if (_railDragged)
            {
                _railDragged = false;
                if (Floating && _carousel != null) Slot(page.transform); // the pair stays where it was dropped
            }
            var (pos, rot) = RailPose(page);
            _rail.transform.SetPositionAndRotation(pos, rot);
        }

        // ------------------------------------------------------------------ smart places for work windows

        /// The window a work window belongs to, when it is opened from the menu or by voice (not from a window's button):
        /// it opens beside that one (ShellPlacement), else in front as before.
        private UiPanel ContextOpener(string id)
        {
            UiPanel OpenOne(string pid) => _panels.TryGetValue(pid, out var q) && q != null && q.IsOpen ? q : null;
            UiPanel Frame()
            {
                if (app != null && app.Active != null && _frames.TryGetValue(app.Active, out var f) && f != null && f.IsOpen) return f;
                return _frames.Values.FirstOrDefault(x => x != null && x.IsOpen);
            }
            switch (id)
            {
                case "terminals": return Frame();
                case "files": return Frame() ?? FocusedThreadAnchor();
                case "clipboard": return FocusedThreadAnchor() ?? OpenOne("web");
                case "browser": return OpenOne("web");
                case "thread-new": return OpenOne("threads");
                case "workspaces": return OpenOne("threads") ?? FocusedThreadAnchor();
                default: return IsThreadWindowId(id) ? _twOpener ?? OpenOne("threads") : null; // another thread window: beside the one it came from
            }
        }

        // ------------------------------------------------------------------ self-tests

        public UiButton RailButtonForTest(string key) => _railRows.FirstOrDefault(r => r.cat.Key == key).button;
        public string RailStatusForTest(string key) => ChatText.Strip(_railRows.FirstOrDefault(r => r.cat.Key == key).status?.Text ?? "");
        public void UpdateControlRailForTest() => UpdateControlRail();
        public UiPanel ContextOpenerForTest(string id) => ContextOpener(id);
    }
}
