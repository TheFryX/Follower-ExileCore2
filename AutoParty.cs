
using System;
using System.Linq;
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
        private DateTime _lastAttempt = DateTime.UtcNow.AddSeconds(-5);

        public AutoParty(Follower plugin) { _plugin = plugin; }

        private dynamic Ingame => _plugin.GameController.IngameState;
        private dynamic UI => _plugin.GameController.IngameState?.IngameUi;

        public void Tick()
        {
            var s = _plugin.Settings;
            if (!s.Enable || (!s.AutoAcceptParty.Value && !s.AutoAcceptTrade.Value))
                return;

            if ((DateTime.UtcNow - _lastAttempt).TotalMilliseconds < s.AutoPartyPollMs.Value) return;
            _lastAttempt = DateTime.UtcNow;

            try
            {
                if (s.AutoAcceptParty.Value)
                    TryAcceptInvites(s.AutoAcceptParty.Value, s.AutoAcceptTrade.Value);

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
                var raw = s.AcceptFrom?.Value;
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    foreach (var part in raw.Split(new[] {',',';','\n','\r','\t'}, System.StringSplitOptions.RemoveEmptyEntries))
                    {
                        var name = part.Trim();
                        if (!string.IsNullOrEmpty(name)) list.Add(name);
                    }
                }
                if (list.Count == 0)
                {
                    var leader = s.LeaderName?.Value;
                    if (!string.IsNullOrWhiteSpace(leader)) list.Add(leader.Trim());
                }
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
            if (!acceptParty && !acceptTrade) return;

            // Prefer the small popup accept; fallback to Social panel accept.
            bool ok = FindAndClickPopupAccept(acceptParty, acceptTrade);
            if (!ok)
                ok = FindAndClickSocialAccept(acceptParty, acceptTrade);
        }

        private bool InSameAreaAsLeader()
        {
            var leaderName = _plugin.Settings.LeaderName.Value?.Trim();
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
            var leaderName = _plugin.Settings.LeaderName.Value?.Trim();
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

            void Scan(dynamic node)
            {
                if (node == null || clicked) return;
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
                                    Mouse.SetCursorPosAndLeftClick(c, 120);
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
                    Scan(ch);
                    if (clicked) return;
                }
            }

            Scan(root);
        }

        // === Helpers for accepting invite based on your UI logs ===

        
private bool FindAndClickPopupAccept(bool acceptParty, bool acceptTrade)
{
    try
    {
        dynamic ui = UI;
        if (ui == null) return false;

        bool clicked = false;
        bool inviteFound = false;
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

        void Scan(dynamic node, int depth)
        {
            if (node == null || clicked) return;
            if (!Visible(node)) return;

            string txt = TextOf(node);
            if (!string.IsNullOrEmpty(txt))
            {
                if (!inviterMatched && TextMatchesAny(txt, allowed)) inviterMatched = true;
                if (acceptParty && txt.IndexOf("sent you a party invite", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _plugin.LogMessage($"AutoParty: found party invite text at depth {depth}", 1);
                    inviteFound = true;
                }
                {
                    _plugin.LogMessage($"AutoParty: found trade request text at depth {depth}", 1);
                    inviteFound = true;
                }

                if (inviteFound && inviterMatched && txt.Equals("accept", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var r = node.GetClientRect();
                        var center = new System.Numerics.Vector2(r.Center.X, r.Center.Y);
                        Mouse.SetCursorPosAndLeftClick(center, 150);
                        _plugin.LogMessage("AutoParty: accepted invite (popup).", 1);
                        clicked = true;
                        return;
                    }
                    catch (Exception ex)
                    {
                        _plugin.LogMessage("AutoParty: click failed: " + ex.Message, 1);
                    }
                }
            }

            dynamic kids = null;
            try { kids = node.Children; } catch { }
            if (kids == null) return;

            int n = 0; try { n = (int)kids.Count; } catch { }
            for (int i = 0; i < n; i++)
            {
                dynamic ch = null; try { ch = kids[i]; } catch { }
                if (ch == null) continue;
                Scan(ch, depth + 1);
                if (clicked) return;
            }
        }

        Scan(ui, 0);
        return clicked;
    }
    catch (Exception ex)
    {
        _plugin.LogMessage("FindAndClickPopupAccept error: " + ex.Message, 1);
        return false;
    }
}

private bool FindAndClickSocialAccept(bool acceptParty, bool acceptTrade)
        {
            try
            {
                dynamic ui = UI;
                if (ui == null) return false;
                bool clicked = false;
                bool inviterMatched = false;
                var allowed = GetAllowedInviters(_plugin.Settings);

                bool Visible(dynamic n)
                {
                    try { return (bool)n.IsVisible; } catch { return false; }
                }

                void Scan(dynamic node, bool underInvitesHeader)
                {
                    if (node == null || clicked) return;
                    if (!Visible(node)) return;

                    string t = null;
                    try { t = (string)node.Text; } catch { }

                    if (!string.IsNullOrEmpty(t) && !inviterMatched && TextMatchesAny(t, allowed)) inviterMatched = true;

                    if (!underInvitesHeader && !string.IsNullOrEmpty(t) &&
                        t.IndexOf("Invitation", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        t.IndexOf("Received", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        underInvitesHeader = true;
                    }

                    if (underInvitesHeader && inviterMatched && !string.IsNullOrEmpty(t) && t.Equals("accept", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var r = node.GetClientRect();
                            var center = new Vector2(r.Center.X, r.Center.Y);
                            Mouse.SetCursorPosAndLeftClick(center, 150);
                            _plugin.LogMessage("AutoParty: accepted invite (Social).", 1);
                            clicked = true;
                            return;
                        }
                        catch { }
                    }

                    dynamic kids = null;
                    try { kids = node.Children; } catch { }
                    if (kids == null) return;
                    int n = 0; try { n = (int)kids.Count; } catch { }
                    for (int i = 0; i < n; i++)
                    {
                        dynamic ch = null; try { ch = kids[i]; } catch { }
                        if (ch == null) continue;
                        Scan(ch, underInvitesHeader);
                        if (clicked) return;
                    }
                }

                Scan(ui, false);
                return clicked;
            }
            catch { return false; }
        }
    }
}
