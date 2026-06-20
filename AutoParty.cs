
using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using ExileCore2;
using ExileCore2.Shared;
using ExileCore2.Shared.Enums;

namespace Follower
{
    internal sealed class AutoParty
    {
        private readonly Follower _plugin;
        private readonly AutoPartyScanDebugLogger _scanDebug;
        private DateTime _lastAttempt = DateTime.UtcNow.AddSeconds(-5);

        public AutoParty(Follower plugin) { _plugin = plugin; _scanDebug = new AutoPartyScanDebugLogger(plugin); }

        public void TickDebugHotkey()
        {
            using var __profileScope = _plugin.ProfileScope("AutoParty.TickDebugHotkey");
            try { _scanDebug.CaptureHoverContextHotkey(); }
            catch (Exception ex) { try { _plugin.LogMessage("AutoParty hover context debug error: " + ex.Message, 5); } catch { } }
        }

        private dynamic Ingame => _plugin.GameController.IngameState;
        private dynamic UI => _plugin.GameController.IngameState?.IngameUi;

        public void Tick()
        {
            using var __profileScope = _plugin.ProfileScope("AutoParty.Tick.Total");
            var s = _plugin.Settings;
            if (!s.Enable || (!s.TpTrade.AutoAcceptParty.Value && !s.TpTrade.AutoAcceptTrade.Value))
                return;

            if ((DateTime.UtcNow - _lastAttempt).TotalMilliseconds < s.TpTrade.AutoPartyPollMs.Value) return;
            _lastAttempt = DateTime.UtcNow;

            try
            {
                if (s.TpTrade.AutoAcceptParty.Value || s.TpTrade.AutoAcceptTrade.Value)
                    TryAcceptInvites(s.TpTrade.AutoAcceptParty.Value, s.TpTrade.AutoAcceptTrade.Value);

            }
            catch (Exception ex)
            {
                _plugin.LogMessage($"AutoParty error: {ex.Message}", 5);
            }
        }

        private static System.Collections.Generic.IReadOnlyList<string> GetAllowedInviters(FollowerSettings s)
        {
            var list = new System.Collections.Generic.List<string>();
            try
            {
                // Single source of truth: every module uses General -> Leader name.
                // Auto party/trade no longer has its own invite whitelist field.
                var leader = s.General.LeaderName?.Value;
                if (!string.IsNullOrWhiteSpace(leader)) list.Add(leader.Trim());
            }
            catch { }
            return list;
        }

        private static bool TextMatchesAny(string text, System.Collections.Generic.IReadOnlyList<string> names)
        {
            if (string.IsNullOrEmpty(text) || names == null || names.Count == 0) return false;
            foreach (var n in names)
                if (!string.IsNullOrEmpty(n) && text.IndexOf(n, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private void TryAcceptInvites(bool acceptParty, bool acceptTrade)
        {
            using var __profileScope = _plugin.ProfileScope("AutoParty.TryAcceptInvites");
            if (!acceptParty && !acceptTrade) return;

            bool ok = false;
            _scanDebug.BeginTick(acceptParty, acceptTrade);
            try
            {
                // Prefer the small popup accept; fallback to Social panel accept.
                ok = FindAndClickPopupAccept(acceptParty, acceptTrade);
                _scanDebug.Event("TryAcceptInvites", "Popup scan result=" + ok);
                if (!ok)
                {
                    ok = FindAndClickSocialAccept(acceptParty, acceptTrade);
                    _scanDebug.Event("TryAcceptInvites", "Social scan result=" + ok);
                }
            }
            finally
            {
                _scanDebug.EndTick(ok);
            }
        }

        private bool InSameAreaAsLeader()
        {
            var leaderName = _plugin.Settings.General.LeaderName.Value?.Trim();
            if (string.IsNullOrEmpty(leaderName)) return true;

            try
            {
                var ent = _plugin.GameController.Entities
                    .Where(x => x.Type == EntityType.Player)
                    .FirstOrDefault(x =>
                    {
                        try
                        {
                            var comp = x.GetComponent<ExileCore2.PoEMemory.Components.Player>();
                            return comp != null && string.Equals(comp.PlayerName, leaderName, StringComparison.OrdinalIgnoreCase);
                        }
                        catch { return false; }
                    });

                return ent != null;
            }
            catch { return true; }
        }

        private void TryTeleportToLeader()
        {
            var leaderName = _plugin.Settings.General.LeaderName.Value?.Trim();
            if (string.IsNullOrEmpty(leaderName)) return;
            if (InSameAreaAsLeader()) return;

            try
            {
                var area = _plugin.GameController.Area.CurrentArea;
                bool safePlace = false;
                try { safePlace = area.IsTown || area.IsHideout || (area.Name?.IndexOf("Hideout", StringComparison.OrdinalIgnoreCase) >= 0); } catch { }
                if (!safePlace) return;
            }
            catch { return; }

            // Try to locate Social/Party and click a "Visit/Join/Go to/Hideout" button near leader name
            try
            {
                dynamic social = null;
                try { social = UI?.Social; } catch { }
                if (social == null) try { social = UI?.SocialElement; } catch { }
                if (social == null) social = UI;

                ClickJoinNearLeaderName(social, leaderName);
            }
            catch { }
        }

        private void ClickJoinNearLeaderName(dynamic root, string leaderName)
        {
            if (root == null) return;

            bool clicked = false;
                bool inviterMatched = false;
                var allowed = GetAllowedInviters(_plugin.Settings);

                bool Visible(dynamic n)
            {
                try { return (bool)n.IsVisible; } catch { return false; }
            }
            string TextOf(dynamic n)
            {
                try { return (string)n.Text; } catch { return null; }
            }

            void Scan(dynamic node, string path)
            {
                if (node == null || clicked) return;
                _scanDebug.Node("ClickJoinNearLeaderName", 0, path, node, "visit");
                if (!Visible(node)) return;

                var txt = TextOf(node);
                if (!string.IsNullOrEmpty(txt) && txt.IndexOf(leaderName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    dynamic parent = node;
                    try { parent = node.Parent; } catch { }
                    if (parent == null) parent = node;

                    dynamic kids = null;
                    try { kids = parent.Children; } catch { }
                    if (kids != null)
                    {
                        int n = 0; try { n = (int)kids.Count; } catch { }
                        for (int i = 0; i < n; i++)
                        {
                            dynamic ch = null; try { ch = kids[i]; } catch { }
                            if (ch == null || !Visible(ch)) continue;
                            var bt = TextOf(ch);
                            if (!string.IsNullOrEmpty(bt) && (
                                    bt.IndexOf("visit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    bt.IndexOf("join", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    bt.IndexOf("go to", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    bt.IndexOf("hideout", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                try
                                {
                                    var r = ch.GetClientRect();
                                    var c = new Vector2(r.Center.X, r.Center.Y);
                                    _plugin.PrepareForPluginMouseAction("AutoParty.JoinVisit.Click.Prepare");
                                    Mouse.SetCursorPosAndLeftClick(c, 0);
                                    _plugin.CompletePluginMouseAction("AutoParty.JoinVisit.Click.Complete");
                                    _plugin.LogMessage("AutoParty: clicked join/visit near leader.", 1);
                                    clicked = true;
                                    return;
                                }
                                catch { }
                            }
                        }
                    }
                }

                dynamic children = null;
                try { children = node.Children; } catch { }
                if (children == null) return;
                int count = 0; try { count = (int)children.Count; } catch { }
                for (int i = 0; i < count; i++)
                {
                    dynamic ch = null; try { ch = children[i]; } catch { }
                    if (ch == null) continue;
                    Scan(ch, path + "->" + i);
                    if (clicked) return;
                }
            }

            Scan(root, "root");
        }

        // === Fast, bounded AutoParty accept based on captured UI paths ===

        private bool FindAndClickPopupAccept(bool acceptParty, bool acceptTrade)
        {
            using var __profileScope = _plugin.ProfileScope("AutoParty.FindAndClickPopupAccept");
            try
            {
                dynamic ui = UI;
                if (ui == null) return false;

                // Known PoE2 bottom-right invite toast paths captured from DevTree/F10.
                // The top-level toast index is not stable across UI scale/resolution/session:
                //   older dumps: IngameUi->100
                //   latest party dump: IngameUi->101
                // Therefore we try the cheap direct paths first for both roots, then a tiny
                // bounded fallback on only those toast roots. No whole-IngameUi scanning.
                if (acceptParty)
                {
                    if (TryClickKnownToastAcceptPath(ui, new[] { 100, 0, 0, 2, 0 }, "KnownPartyToast100"))
                        return true;

                    if (TryClickKnownToastAcceptPath(ui, new[] { 101, 0, 0, 2, 0 }, "KnownPartyToast101"))
                        return true;

                    // Some sessions expose the clickable text leaf under the button.
                    if (TryClickKnownToastAcceptPath(ui, new[] { 101, 0, 0, 2, 0, 0 }, "KnownPartyToast101TextLeaf"))
                        return true;
                }

                if (acceptTrade)
                {
                    if (TryClickKnownToastAcceptPath(ui, new[] { 100, 0, 2, 0 }, "KnownTradeToast100"))
                        return true;

                    if (TryClickKnownToastAcceptPath(ui, new[] { 101, 0, 2, 0 }, "KnownTradeToast101"))
                        return true;

                    if (TryClickKnownToastAcceptPath(ui, new[] { 101, 0, 2, 0, 0 }, "KnownTradeToast101TextLeaf"))
                        return true;
                }

                if (TryAcceptFromKnownToastRoots(ui, acceptParty, acceptTrade))
                    return true;

                return false;
            }
            catch (Exception ex)
            {
                _plugin.LogMessage("FindAndClickPopupAccept error: " + ex.Message, 1);
                return false;
            }
        }

        private bool TryAcceptFromKnownToastRoots(dynamic ui, bool acceptParty, bool acceptTrade)
        {
            using var __profileScope = _plugin.ProfileScope("AutoParty.TryAcceptFromKnownToastRoots");
            if (ui == null) return false;

            // Keep this deliberately tiny. These are the only roots observed for the bottom-right toast.
            int[] roots = { 100, 101 };
            for (int i = 0; i < roots.Length; i++)
            {
                int rootIndex = roots[i];
                dynamic toastRoot = GetChild(ui, rootIndex);
                if (toastRoot != null && TryAcceptFromToastRoot(toastRoot, "IngameUi->" + rootIndex, acceptParty, acceptTrade))
                    return true;
            }

            return false;
        }

        private bool TryClickKnownToastAcceptPath(dynamic ui, int[] path, string source)
        {
            using var __profileScope = _plugin.ProfileScope("AutoParty.TryClickKnownToastAcceptPath." + source);
            dynamic node = GetChildPath(ui, path);
            if (node == null || !IsVisible(node))
            {
                _scanDebug.Event(source, "known path not visible path=" + FormatPath(path));
                return false;
            }

            return ClickNode(node, "IngameUi->" + FormatPath(path), source);
        }

        private bool TryAcceptFromToastRoot(dynamic root, string rootPath, bool acceptParty, bool acceptTrade)
        {
            using var __profileScope = _plugin.ProfileScope("AutoParty.TryAcceptFromToastRoot");
            if (root == null) return false;
            if (!IsVisible(root)) return false;

            var allowed = GetAllowedInviters(_plugin.Settings);
            var state = new ToastScanState();

            ScanToast(root, rootPath, 0, state, allowed, maxDepth: 6, maxNodes: 40);

            bool wantedInvite = (acceptParty && state.PartyInviteFound) || (acceptTrade && state.TradeRequestFound);

            // Current ExileCore2 toast exposes account/realm names inconsistently (for example
            // TheFryX#3718 while settings contain TheFry_BMs). Preserve the original working
            // behaviour: once the toast itself contains a party/trade invite and an accept button, click it.
            bool allowByName = true;

            if (!wantedInvite || !allowByName || state.AcceptNode == null)
            {
                _scanDebug.Event("DirectToast", "skip " + rootPath +
                    " party=" + state.PartyInviteFound +
                    " trade=" + state.TradeRequestFound +
                    " accept=" + (state.AcceptNode != null) +
                    " allowedSeen=" + state.AllowedNameSeen +
                    " playerLike=" + state.AnyPlayerLikeTextSeen);
                return false;
            }

            return ClickNode(state.AcceptNode, state.AcceptPath, "DirectToast");
        }

        private sealed class ToastScanState
        {
            public bool PartyInviteFound;
            public bool TradeRequestFound;
            public bool AllowedNameSeen;
            public bool AnyPlayerLikeTextSeen;
            public dynamic AcceptNode;
            public string AcceptPath;
            public int NodesVisited;
        }

        private void ScanToast(dynamic node, string path, int depth, ToastScanState state, IReadOnlyList<string> allowed, int maxDepth, int maxNodes)
        {
            using var __profileScope = _plugin.ProfileScope("AutoParty.ScanToast");
            if (node == null || state.NodesVisited >= maxNodes || depth > maxDepth) return;
            state.NodesVisited++;

            _scanDebug.Node("DirectToast", depth, path, node, "bounded visit");

            if (!IsVisible(node)) return;

            string text = TextOf(node);
            string clean = NormalizeText(text);
            if (!string.IsNullOrEmpty(clean))
            {
                if (TextMatchesAny(clean, allowed))
                {
                    state.AllowedNameSeen = true;
                    _scanDebug.Reaction("DirectToast", "ALLOWED_NAME", path, node, clean, "");
                }

                if (LooksLikePlayerName(clean))
                    state.AnyPlayerLikeTextSeen = true;

                if (clean.IndexOf("sent you a party invite", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    state.PartyInviteFound = true;
                    _scanDebug.Reaction("DirectToast", "PARTY_INVITE", path, node, clean, "");
                }

                if (clean.IndexOf("sent you a trade request", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    state.TradeRequestFound = true;
                    _scanDebug.Reaction("DirectToast", "TRADE_REQUEST", path, node, clean, "");
                }

                if (state.AcceptNode == null && clean.Equals("accept", StringComparison.OrdinalIgnoreCase))
                {
                    state.AcceptNode = node;
                    state.AcceptPath = path;
                    _scanDebug.Reaction("DirectToast", "ACCEPT_NODE", path, node, clean, "captured path from F10 debug pattern");
                }
            }

            dynamic children = GetChildren(node);
            if (children == null) return;

            int count = GetCount(children);
            for (int i = 0; i < count; i++)
            {
                dynamic child = null;
                try { child = children[i]; } catch { }
                if (child == null) continue;
                ScanToast(child, path + "->" + i, depth + 1, state, allowed, maxDepth, maxNodes);
                if (state.NodesVisited >= maxNodes) return;
            }
        }

        private bool ClickNode(dynamic node, string path, string source)
        {
            using var __profileScope = _plugin.ProfileScope("AutoParty.ClickNode");
            try
            {
                var r = node.GetClientRect();
                var center = new Vector2(r.Center.X, r.Center.Y);
                _plugin.PrepareForPluginMouseAction("AutoParty.ClickNode.Prepare");
                Mouse.SetCursorPosAndLeftClick(center, 0);
                _plugin.CompletePluginMouseAction("AutoParty.ClickNode.Complete");
                _plugin.LogMessage("AutoParty: accepted invite (" + source + ").", 1);
                _scanDebug.Event(source, "CLICK accept path=" + path + " center=" + center);
                _scanDebug.Reaction(source, "CLICK_ACCEPT", path, node, TextOf(node), "center=" + center);
                return true;
            }
            catch (Exception ex)
            {
                _plugin.LogMessage("AutoParty: click failed: " + ex.Message, 1);
                return false;
            }
        }

        private bool FindAndClickSocialAccept(bool acceptParty, bool acceptTrade)
        {
            using var __profileScope = _plugin.ProfileScope("AutoParty.FindAndClickSocialAccept.Disabled");
            // Intentionally disabled for performance. The old implementation recursively scanned
            // the whole IngameUi/Social/Chat tree and caused heavy CPU load. Current invite toast
            // was captured under IngameUi->100/101 and is handled by FindAndClickPopupAccept().
            return false;
        }

        private static dynamic GetChildPath(dynamic node, int[] path)
        {
            if (node == null || path == null) return null;
            dynamic current = node;
            for (int i = 0; i < path.Length; i++)
            {
                current = GetChild(current, path[i]);
                if (current == null) return null;
            }
            return current;
        }

        private static string FormatPath(int[] path)
        {
            if (path == null || path.Length == 0) return string.Empty;
            return string.Join("->", path);
        }

        private static dynamic GetChild(dynamic node, int index)
        {
            try
            {
                dynamic children = node.Children;
                if (children == null) return null;
                int count = GetCount(children);
                if (index < 0 || index >= count) return null;
                return children[index];
            }
            catch { return null; }
        }

        private static dynamic GetChildren(dynamic node)
        {
            try { return node.Children; } catch { return null; }
        }

        private static int GetCount(dynamic children)
        {
            try { return (int)children.Count; } catch { return 0; }
        }

        private static bool IsVisible(dynamic node)
        {
            try { return (bool)node.IsVisible; } catch { return false; }
        }

        private static string TextOf(dynamic node)
        {
            try
            {
                string textNoTags = null;
                try { textNoTags = (string)node.TextNoTags; } catch { }
                if (!string.IsNullOrWhiteSpace(textNoTags)) return textNoTags;
                return (string)node.Text;
            }
            catch { return null; }
        }

        private static string NormalizeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            return text.Replace("\0", string.Empty).Trim();
        }

        private static bool LooksLikePlayerName(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.IndexOf("#", StringComparison.Ordinal) >= 0) return true;
            if (text.IndexOf("sent you", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf("party", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf("trade", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }
}
