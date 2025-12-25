
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

	                static bool Visible(dynamic n) { try { return (bool)n.IsVisible; } catch { return false; } }
	                static bool Active(dynamic n) { try { return (bool)n.IsActive; } catch { return true; } }
	                static string TextOf(dynamic n) { try { return (string)n.Text; } catch { return null; } }
	                static RectangleF RectOf(dynamic n) { try { return n.GetClientRect(); } catch { return new RectangleF(); } }

	                static IEnumerable<dynamic> Descendants(dynamic root, int maxDepth)
	                {
	                    if (root == null || maxDepth < 0) yield break;
	                    yield return root;
	                    if (maxDepth == 0) yield break;
	                    dynamic kids = null; try { kids = root.Children; } catch { kids = null; }
	                    if (kids == null) yield break;
	                    int n = 0; try { n = (int)kids.Count; } catch { n = 0; }
	                    for (int i = 0; i < n; i++)
	                    {
	                        dynamic ch = null; try { ch = kids[i]; } catch { ch = null; }
	                        if (ch == null) continue;
	                        foreach (var d in Descendants(ch, maxDepth - 1))
	                            yield return d;
	                    }
	                }

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

	                    // NOTE: after recent PoE2 UI updates, the teleport icon started exposing a glyph "Text"
	                    // (private-use character). The old logic filtered out non-empty text which made TP stop.
	                    // We now use a geometry + activity heuristic and scan a few levels deep.
	                    var rowRect = RectOf(row);
	                    var nameRect = rowRect;
	                    try { nameRect = row.Children[0].GetClientRect(); } catch { /* ignore */ }

	                    dynamic bestEl = null;
	                    var bestScore = float.PositiveInfinity;

	                    foreach (var el in Descendants(row, maxDepth: 4))
	                    {
	                        if (el == null || !Visible(el) || !Active(el)) continue;
	
	                        var r = RectOf(el);
	                        if (r.Width <= 0 || r.Height <= 0) continue;

	                        // Must be within the row band vertically.
	                        if (r.Center.Y < rowRect.Y - 5 || r.Center.Y > rowRect.Y + rowRect.Height + 5) continue;

	                        // Teleport icon is on the left side of the name.
	                        if (r.Center.X >= nameRect.X) continue;

	                        // Reject obvious text blocks: very wide elements or elements containing leader name.
	                        var t = TextOf(el);
	                        if (!string.IsNullOrEmpty(t) && t.IndexOf(leaderName, StringComparison.OrdinalIgnoreCase) >= 0)
	                            continue;
	
	                        // Typical icon sizes: 12..64px, roughly square.
	                        var area = r.Width * r.Height;
	                        if (area < 64 || area > 7000) continue;
	                        var aspect = r.Width > r.Height ? (r.Width / r.Height) : (r.Height / r.Width);
	                        if (aspect > 2.2f) continue;

	                        // Score: prefer smaller, square-ish, closest to the name from the left.
	                        var gapToName = Math.Max(0f, nameRect.X - r.Right);
	                        var score = (area * 0.01f) + (gapToName * 0.15f) + ((aspect - 1f) * 2.5f);
	
	                        if (score < bestScore)
	                        {
	                            bestScore = score;
	                            bestEl = el;
	                        }
	                    }

	                    if (bestEl != null)
	                    {
	                        ClickCenter(RectOf(bestEl));
	                        MarkTeleportAttemptStarted();
	                        return true;
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
	                try { text = (string)node.Children[0]?.Text; } catch { /* ignore */ }
	                if (string.IsNullOrWhiteSpace(text)) return false;

	                // UI text is localized/variable; keep a loose check.
	                var looksLikeConfirm =
	                    text.IndexOf("teleport", StringComparison.OrdinalIgnoreCase) >= 0 &&
	                    (text.IndexOf("sure", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("confirm", StringComparison.OrdinalIgnoreCase) >= 0);
	                if (!looksLikeConfirm) return false;

	                // Prefer an explicit OK/Yes button; otherwise click the right-most bottom button.
	                dynamic bestBtn = null;
	                RectangleF bestRect = default;
	
	                void Consider(dynamic btn)
	                {
	                    if (btn == null) return;
	                    bool vis; try { vis = btn.IsVisible == true; } catch { vis = true; }
	                    if (!vis) return;
	                    RectangleF r; try { r = btn.GetClientRect(); } catch { return; }
	                    if (r.Width < 60 || r.Height < 18) return;
	
	                    string bt = null; try { bt = (string)btn.Text; } catch { bt = null; }
	                    if (!string.IsNullOrEmpty(bt) &&
	                        (bt.Equals("OK", StringComparison.OrdinalIgnoreCase) ||
	                         bt.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
	                         bt.IndexOf("accept", StringComparison.OrdinalIgnoreCase) >= 0))
	                    {
	                        bestBtn = btn;
	                        bestRect = r;
	                        return;
	                    }

	                    // Fallback heuristic: keep the right-most candidate.
	                    if (bestBtn == null || r.X > bestRect.X)
	                    {
	                        bestBtn = btn;
	                        bestRect = r;
	                    }
	                }

	                try
	                {
	                    dynamic btnContainer = null;
	                    try { btnContainer = node.Children[3]; } catch { btnContainer = null; }
	                    if (btnContainer != null)
	                    {
	                        dynamic kids = null; try { kids = btnContainer.Children; } catch { kids = null; }
	                        int n = 0; try { n = (int)kids.Count; } catch { n = 0; }
	                        for (int i = 0; i < n; i++)
	                        {
	                            dynamic b = null; try { b = kids[i]; } catch { b = null; }
	                            Consider(b);
	                        }
	                    }
	                }
	                catch { /* ignore */ }

	                if (bestBtn == null)
	                {
	                    // Last resort: scan a few descendants and pick something button-shaped.
	                    dynamic kids = null; try { kids = node.Children; } catch { kids = null; }
	                    int n = 0; try { n = (int)kids.Count; } catch { n = 0; }
	                    for (int i = 0; i < n; i++)
	                    {
	                        dynamic b = null; try { b = kids[i]; } catch { b = null; }
	                        Consider(b);
	                    }
	                }

	                if (bestBtn == null) return false;
	                ClickCenter(bestRect);
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
