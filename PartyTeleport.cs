
using System;
using System.Numerics;
using System.Collections.Generic;
using ExileCore2;
using ExileCore2.Shared;

namespace Follower
{

    internal sealed class PartyTeleport
    {
        private readonly Follower _plugin;

        private DateTime _nextAttempt = DateTime.UtcNow.AddMilliseconds(-500);
        private int _retryCount;
        private bool _hideoutTpDone;
        private bool _wasInHideout;
        private string _lastLeader = string.Empty;

        // Pending TP verification
        private bool _tpPending;
        private string _tpStartAreaName = string.Empty;
        private DateTime _tpStartTime;
        private int _tpAttemptCount;

        public PartyTeleport(Follower plugin) { _plugin = plugin; }

        private dynamic UI => _plugin.GameController.IngameState?.IngameUi;

        // PoE2 map names (treated as "leader is in a map")
        private static readonly string[] PoE2MapNames = new [] {
            "Alpine Ridge","Augury","Azmerian Ranges","Backwash","Bastille","Bloodwood","Blooming Field","Bluff",
            "Burial Bog","Caldera","Canyon","Castaway","Cenotes","Channel","Cliffside","Confluence","Creek",
            "Crimson Shores","Crypt","Decay","Derelict Mansion","Deserted","Digsite","Epitaph","Farmlands",
            "Flotsam","Forge","Fortress","Frozen Falls","Grimhaven","Headland","Hidden Grotto","Hive","Ice Cave",
            "Inferno","Lofty Summit","Lost Towers","Marrow","Merchant's Campsite","Mesa","Mineshaft","Mire",
            "Molten Vault","Moment of Zen","Necropolis","Oasis","Ornate Chambers","Outlands","Overgrown",
            "Penitentiary","Ravine","Razed Fields","Riverhold","Riverside","Rockpools","Rugosa","Rupture",
            "Rustbowl","Sacred Reservoir","Sandspit","Savannah","Sealed Vault","Seepage","Sinkhole",
            "Sinking Spire","Slick","Spider Woods","Spring","Steaming Springs","Steppe","Stronghold",
            "Sulphuric Caverns","Sump","Sun Temple","The Assembly","The Ezomyte Megaliths","The Fractured Lake",
            "The Jade Isles","The Silent Cave","The Viridian Wildwood","The Ziggurat Refuge","Trenches",
            "Untainted Paradise","Vaal City","Vaal Village","Vaults of Kamasa","Wayward Isle","Wetlands","Willow","Woodland"
        };

        public void Tick()
        {
            var s = _plugin.Settings;
            if (!s.Enable) return;

            // Pending TP handling: check success or retry
            if (_tpPending)
            {
                string leaderNow = (s.LeaderName?.Value ?? string.Empty);

                if (IsLoading() || AreaNameChangedSinceAttempt() || InSameAreaAsLeader(leaderNow))
                {
                    _tpPending = false;
                    _tpAttemptCount = 0;
                    _retryCount = 0;
                    return;
                }

                var elapsed = (DateTime.UtcNow - _tpStartTime).TotalMilliseconds;
                if (elapsed >= s.TpConfirmTimeoutMs.Value)
                {
                    if (_tpAttemptCount < s.TpConfirmRetries.Value)
                    {
                        // Re-try: click OK if dialog is open, else open dialog again
                        if (!(TryConfirmTeleportByPath() || TryConfirmTeleportByGeometry()))
                        {
                            TryClickPartyTp(leaderNow);
                        }
                        // MarkTeleportAttemptStarted() is called inside click helpers if they succeed
                    }
                    else
                    {
                        _tpPending = false; // give up for now
                        _tpAttemptCount = 0;
                    }
                }
                return; // pause normal flow while awaiting teleport
            }

            if (!s.TeleportToLeader.Value) return;

            // Keep confirming dialog fast if it exists
            if (s.AutoConfirmTeleportDialog.Value)
            {
                if (TryConfirmTeleportByPath() || TryConfirmTeleportByGeometry())
                    return; // we just pressed OK and started pending state
            }

            // throttle TP attempts
            if ((DateTime.UtcNow - _nextAttempt).TotalMilliseconds < s.TpPollMs.Value) return;
            _nextAttempt = DateTime.UtcNow;

            var area = _plugin.GameController.Area?.CurrentArea;
            bool inAnyHideout = false;
            try { inAnyHideout = area != null && (area.IsHideout || (area.Name?.IndexOf("Hideout", StringComparison.OrdinalIgnoreCase) >= 0)); } catch { }

            if (_wasInHideout && !inAnyHideout) _hideoutTpDone = false;
            _wasInHideout = inAnyHideout;

            var leader = (s.LeaderName?.Value ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(leader) && !string.Equals(_lastLeader, leader, StringComparison.OrdinalIgnoreCase))
            {
                _lastLeader = leader;
                _hideoutTpDone = false;
            }
            if (string.IsNullOrEmpty(leader)) return;

            if (InSameAreaAsLeader(leader)) { _retryCount = 0; return; }

            string rowText = GetLeaderRowText(leader) ?? string.Empty;
            string rowLower = rowText.ToLowerInvariant();
            bool leaderInHideout = rowText.IndexOf("Hideout", StringComparison.OrdinalIgnoreCase) >= 0;
            bool leaderInMap = false;
            for (int i = 0; i < PoE2MapNames.Length; i++)
            {
                var nm = PoE2MapNames[i];
                if (nm.Length > 0 && rowLower.Contains(nm.ToLowerInvariant())) { leaderInMap = true; break; }
            }

            // In your hideout: allow a single TP to leader HO if enabled; otherwise don't spam.
            if (inAnyHideout)
            {
                if (s.TeleportFromHideout.Value && !_hideoutTpDone && leaderInHideout)
                {
                    if (TryClickPartyTp(leader)) { _hideoutTpDone = true; _retryCount++; }
                }
                return;
            }

            // If leader is in a map (PoE2), don't TP; use follow/portal
            if (leaderInMap) { _retryCount = 0; return; }

            // From acts/towns/etc -> try TP
            if (TryClickPartyTp(leader)) _retryCount++;

            if (_retryCount > s.TpMaxRetries.Value) { _retryCount = 0; }
        }

        private string GetLeaderRowText(string leaderName)
        {
            try
            {
                dynamic party = UI?.PartyElement;
                if (party == null) return null;
                dynamic rows = null;
                try { rows = party.Children[0]?.Children; } catch { }
                if (rows == null) try { rows = party.Children; } catch { }
                if (rows == null) return null;
                int rowCount = 0; try { rowCount = (int)rows.Count; } catch { }
                for (int i = 0; i < rowCount; i++)
                {
                    dynamic row = null; try { row = rows[i]; } catch { }
                    if (row == null) continue;
                    if (!FindTextRecursive(row, leaderName)) continue;
                    return CollectAllText(row);
                }
            }
            catch { }
            return null;
        }

        private string CollectAllText(dynamic node)
        {
            try
            {
                string acc = string.Empty;
                string t = null; try { t = (string)node.Text; } catch { }
                if (!string.IsNullOrEmpty(t)) acc += t + " ";
                dynamic kids = null; try { kids = node.Children; } catch { }
                int n = 0; try { n = (int)kids.Count; } catch { }
                for (int i = 0; i < n; i++)
                {
                    dynamic ch = null; try { ch = kids[i]; } catch { }
                    if (ch == null) continue;
                    var s = CollectAllText(ch);
                    if (!string.IsNullOrEmpty(s)) acc += s + " ";
                }
                return acc;
            }
            catch { return null; }
        }

        private bool InSameAreaAsLeader(string leaderName)
        {
            try
            {
                foreach (var ent in _plugin.GameController.Entities)
                {
                    try
                    {
                        if (ent.Type != ExileCore2.Shared.Enums.EntityType.Player) continue;
                        var comp = ent.GetComponent<ExileCore2.PoEMemory.Components.Player>();
                        if (comp == null) continue;
                        if (string.Equals(comp.PlayerName, leaderName, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        private bool TryClickPartyTp(string leaderName)
        {
            try
            {
                dynamic party = UI?.PartyElement;
                if (party == null) return false;

                bool Visible(dynamic n) { try { return (bool)n.IsVisible; } catch { return false; } }
                string TextOf(dynamic n) { try { return (string)n.Text; } catch { return null; } }
                RectangleF RectOf(dynamic n) { try { return n.GetClientRect(); } catch { return new RectangleF(); } }

                dynamic rows = null;
                try { rows = party.Children[0]?.Children; } catch { }
                if (rows == null) try { rows = party.Children; } catch { }
                if (rows == null) return false;

                int rowCount = 0; try { rowCount = (int)rows.Count; } catch { }
                for (int i = 0; i < rowCount; i++)
                {
                    dynamic row = null; try { row = rows[i]; } catch { }
                    if (row == null || !Visible(row)) continue;

                    bool isLeaderRow = false;
                    try
                    {
                        var maybeName = TextOf(row.Children[0]?.Children[0]);
                        if (!string.IsNullOrEmpty(maybeName) &&
                            maybeName.IndexOf(leaderName, StringComparison.OrdinalIgnoreCase) >= 0)
                            isLeaderRow = true;
                    }
                    catch { }

                    if (!isLeaderRow && !FindTextRecursive(row, leaderName)) continue;

                    RectangleF nameRect = RectOf(row);
                    try { nameRect = row.Children[0].GetClientRect(); } catch { }
                    dynamic kids = null; try { kids = row.Children; } catch { }
                    int kcount = 0; try { kcount = (int)kids.Count; } catch { }

                    (dynamic el, float area)? best1 = null;
                    (dynamic el, float area)? best2 = null;

                    for (int k = 0; k < kcount; k++)
                    {
                        dynamic ch = null; try { ch = kids[k]; } catch { }
                        if (ch == null || !Visible(ch)) continue;
                        var txt = TextOf(ch);
                        if (!string.IsNullOrEmpty(txt)) continue;
                        var r = RectOf(ch);
                        if (r.Width <= 0 || r.Height <= 0) continue;
                        bool leftOfName = r.X < nameRect.X;
                        if (!leftOfName) continue;
                        float area = r.Width * r.Height;
                        if (area < 10 || area > 4096) continue;
                        if (best1 == null || area < best1.Value.area) { best2 = best1; best1 = (ch, area); }
                        else if (best2 == null || area < best2.Value.area) { best2 = (ch, area); }
                    }

                    if (best1?.el != null) { ClickCenter(RectOf(best1.Value.el)); MarkTeleportAttemptStarted(); return true; }
                    if (best2?.el != null)
                    {
                        var r = RectOf(best2.Value.el);
                        r = new RectangleF(r.X + r.Width * 0.35f, r.Y + r.Height * 0.5f, r.Width, r.Height);
                        ClickCenter(r); MarkTeleportAttemptStarted(); return true;
                    }
                }
            }
            catch (Exception ex) { _plugin.LogMessage("PartyTeleport error: " + ex.Message, 2); }
            return false;
        }

        private bool TryConfirmTeleportByPath()
        {
            try
            {
                dynamic popup = UI?.PopUpWindow;
                if (popup == null) return false;
                bool vis = false; try { vis = (bool)popup.IsVisible; } catch { vis = false; }
                if (!vis) return false;

                dynamic node = null;
                try { node = popup.Children[0]?.Children[0]; } catch { node = null; }
                if (node == null) return false;

                string text = null;
                try { text = (string)node.Children[0]?.Text; } catch { }
                if (string.IsNullOrEmpty(text) || !text.StartsWith("Are you sure you want to teleport", StringComparison.OrdinalIgnoreCase))
                    return false;

                dynamic okBtn = null;
                try { okBtn = node.Children[3]?.Children[0]; } catch { okBtn = null; }
                if (okBtn == null) return false;

                var r = okBtn.GetClientRect();
                ClickCenter(r);
                MarkTeleportAttemptStarted();
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Geometry fallback: find the dialog by its text and click the rightmost button on the baseline.
        /// Returns true if clicked.
        /// </summary>
        private bool TryConfirmTeleportByGeometry()
        {
            try
            {
                dynamic ui = UI;
                if (ui == null) return false;

                bool Visible(dynamic n) { try { return (bool)n.IsVisible; } catch { return false; } }
                RectangleF RectOf(dynamic n) { try { return n.GetClientRect(); } catch { return new RectangleF(); } }

                dynamic textNode = FindNodeContaining(ui, "ARE YOU SURE YOU WANT TO TELEPORT");
                if (textNode == null) textNode = FindNodeContaining(ui, "sure you want to teleport");
                if (textNode == null) return false;

                dynamic container = textNode;
                for (int up = 0; up < 4; up++)
                {
                    try { container = container.Parent; } catch { container = null; }
                    if (container == null) break;
                    var r = RectOf(container);
                    if (r.Width >= 400 && r.Height >= 150) break;
                }
                if (container == null) return false;

                var candidates = new List<RectangleF>();
                dynamic kids = null; try { kids = container.Children; } catch { }
                int n = 0; try { n = (int)kids.Count; } catch { }
                for (int i = 0; i < n; i++)
                {
                    dynamic ch = null; try { ch = kids[i]; } catch { }
                    if (ch == null || !Visible(ch)) continue;
                    var r = RectOf(ch);
                    if (r.Width < 80 || r.Width > 350 || r.Height < 24 || r.Height > 90) continue;
                    var cr = RectOf(container);
                    if (r.Y < cr.Y + cr.Height * 0.55f) continue;
                    candidates.Add(r);
                }

                if (candidates.Count == 0) return false;

                // take rightmost with similar baseline
                RectangleF best = candidates[0];
                foreach (var c in candidates)
                {
                    if (Math.Abs(c.Y - best.Y) < 10f && c.X > best.X) best = c;
                }

                ClickCenter(best);
                MarkTeleportAttemptStarted();
                return true;
            }
            catch { return false; }
        }

        private dynamic FindNodeContaining(dynamic node, string fragment)
        {
            try
            {
                if (node == null) return null;
                bool Visible(dynamic n) { try { return (bool)n.IsVisible; } catch { return false; } }
                if (!Visible(node)) return null;
                string t = null; try { t = (string)node.Text; } catch { }
                if (!string.IsNullOrEmpty(t) && t.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) return node;
                dynamic kids = null; try { kids = node.Children; } catch { }
                int n = 0; try { n = (int)kids.Count; } catch { }
                for (int i = 0; i < n; i++)
                {
                    dynamic ch = null; try { ch = kids[i]; } catch { }
                    var res = FindNodeContaining(ch, fragment);
                    if (res != null) return res;
                }
            }
            catch { }
            return null;
        }

        private bool FindTextRecursive(dynamic node, string needle)
        {
            try
            {
                if (node == null) return false;
                bool Visible(dynamic n) { try { return (bool)n.IsVisible; } catch { return false; } }
                if (!Visible(node)) return false;
                string t = null; try { t = (string)node.Text; } catch { }
                if (!string.IsNullOrEmpty(t) && t.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                dynamic kids = null; try { kids = node.Children; } catch { }
                if (kids == null) return false;
                int n = 0; try { n = (int)kids.Count; } catch { }
                for (int i = 0; i < n; i++)
                {
                    dynamic ch = null; try { ch = kids[i]; } catch { }
                    if (ch == null) continue;
                    if (FindTextRecursive(ch, needle)) return true;
                }
            }
            catch { }
            return false;
        }

        private void ClickCenter(RectangleF rect)
        {
            try
            {
                var c = new Vector2(rect.Center.X, rect.Center.Y);
                Mouse.SetCursorPosAndLeftClick(c, 1);
            }
            catch { }
        }

        private void MarkTeleportAttemptStarted()
        {
            try
            {
                _tpPending = true;
                _tpStartTime = DateTime.UtcNow;
                _tpAttemptCount++;
                _tpStartAreaName = _plugin.GameController.Area?.CurrentArea?.Name ?? string.Empty;
            }
            catch { _tpPending = true; }
        }

        private bool AreaNameChangedSinceAttempt()
        {
            try
            {
                var nm = _plugin.GameController.Area?.CurrentArea?.Name ?? string.Empty;
                return _tpPending && !string.Equals(nm, _tpStartAreaName, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private bool IsLoading()
        {
            // Avoid strong-typed properties that may not exist; try dynamic and UI heuristics.
            try
            {
                dynamic ingameDyn = _plugin.GameController.IngameState;
                if (ingameDyn != null)
                {
                    try { if (ingameDyn.IsLoading == true) return true; } catch { /* property may not exist */ }
                }
            }
            catch { }

            try
            {
                dynamic ui = _plugin.GameController.IngameState?.IngameUi;
                if (ui == null) return false;

                try { if (ui.WaitForLoading != null && ui.WaitForLoading.IsVisible == true) return true; } catch { }
                try { if (ui.LoadingMode != null && ui.LoadingMode.IsVisible == true) return true; } catch { }
                try { if (ui.WaitTillWorldLoad != null && ui.WaitTillWorldLoad.IsVisible == true) return true; } catch { }
                try { if (ui.WorldMapLoading != null && ui.WorldMapLoading.IsVisible == true) return true; } catch { }
                try { if (ui.LoadingState != null && ui.LoadingState.IsVisible == true) return true; } catch { }
            }
            catch { }

            return false;
        }
    }
}
