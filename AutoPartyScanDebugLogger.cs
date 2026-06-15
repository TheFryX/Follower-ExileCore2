using System;
using System.Globalization;
using System.IO;
using System.Text;
using ExileCore2.Shared.Nodes;

namespace Follower
{
    internal sealed class AutoPartyScanDebugLogger
    {
        private readonly Follower _plugin;
        private string _sessionFile;
        private int _tickIndex;
        private int _nodesThisTick;
        private bool _limitLoggedThisTick;

        public AutoPartyScanDebugLogger(Follower plugin)
        {
            _plugin = plugin;
        }

        public bool NodeEnabled
        {
            get
            {
                try { return _plugin.Settings.Debug.DebugAutoPartyScannerToTxt.Value; }
                catch { return false; }
            }
        }

        public bool ReactionEnabled
        {
            get
            {
                try
                {
                    if (_plugin.Settings.Debug.DebugAutoPartyScannerToTxt.Value) return true;
                    return _plugin.Settings.Debug.DebugAutoPartyReactionsToTxt.Value;
                }
                catch { return NodeEnabled; }
            }
        }



        public void CaptureHoverContextHotkey()
        {
            bool enabled = false;
            try { enabled = _plugin.Settings.Debug.DebugAutoPartyHoverContextToTxt.Value; } catch { }
            if (!enabled) return;

            bool pressed = false;
            try { pressed = _plugin.Settings.Debug.AutoPartyHoverContextDumpHotkey.PressedOnce(); } catch { }
            if (!pressed) return;

            CaptureHoverContextNow("hotkey");
        }

        private void CaptureHoverContextNow(string reason)
        {
            object ui = null;
            try { ui = _plugin.GameController.IngameState?.IngameUi; } catch { }

            int mouseScreenX = 0;
            int mouseScreenY = 0;
            try
            {
                var p = Mouse.GetCursorPosition();
                mouseScreenX = p.X;
                mouseScreenY = p.Y;
            }
            catch { }

            float windowX = 0;
            float windowY = 0;
            try
            {
                dynamic wr = _plugin.GameController.Window.GetWindowRectangle();
                windowX = Convert.ToSingle(wr.X, CultureInfo.InvariantCulture);
                windowY = Convert.ToSingle(wr.Y, CultureInfo.InvariantCulture);
            }
            catch { }

            float mouseClientX = mouseScreenX - windowX;
            float mouseClientY = mouseScreenY - windowY;

            int maxDepth = 8;
            int childLimit = 300;
            try { maxDepth = Math.Max(1, _plugin.Settings.Debug.AutoPartyHoverContextMaxDepth.Value); } catch { }
            try { childLimit = Math.Max(5, _plugin.Settings.Debug.AutoPartyHoverContextChildLimit.Value); } catch { }

            WriteLine("================================================================================");
            WriteLine($"HOVER_CONTEXT_DUMP reason={reason} time={DateTime.Now:O}");
            WriteLine($"Area={Safe(() => _plugin.GameController.Area.CurrentArea.Name)} PlayerAlive={Safe(() => _plugin.GameController.Player.IsAlive.ToString())}");
            WriteLine($"MouseScreen=({mouseScreenX},{mouseScreenY}) WindowOffset=({windowX.ToString(CultureInfo.InvariantCulture)},{windowY.ToString(CultureInfo.InvariantCulture)}) MouseClient=({mouseClientX.ToString(CultureInfo.InvariantCulture)},{mouseClientY.ToString(CultureInfo.InvariantCulture)}) MaxDepth={maxDepth} ChildLimit={childLimit}");

            DumpKnownHoverObjects(mouseClientX, mouseClientY, maxDepth, childLimit);

            if (ui == null)
            {
                WriteLine("HOVER_CONTEXT no IngameUi");
                return;
            }

            WriteCompactNode("HoverContext:IngameUi", 0, "IngameUi", ui);

            var hits = new System.Collections.Generic.List<HitPath>();
            var stack = new System.Collections.Generic.List<PathNode>(64);
            FindHitPaths(ui, "IngameUi", 0, maxDepth, childLimit, mouseClientX, mouseClientY, mouseScreenX, mouseScreenY, stack, hits);

            WriteLine($"HOVER_CONTEXT hitCount={hits.Count}");
            for (int i = 0; i < hits.Count; i++)
            {
                var hit = hits[i];
                WriteLine($"HIT index={i} depth={hit.Depth} path={hit.Path} mode={hit.Mode}");
                WriteCompactNode("HoverContext:hit", hit.Depth, hit.Path, hit.Node);
            }

            if (hits.Count > 0)
            {
                hits.Sort((a, b) => b.Depth.CompareTo(a.Depth));
                var deepest = hits[0];
                WriteLine($"DEEPEST_HIT path={deepest.Path} depth={deepest.Depth} mode={deepest.Mode}");
                DumpTreeLimited("HoverContext:deepest-subtree", deepest.Node, 0, maxDepth, deepest.Path, childLimit);

                var top = GetTopLevelFromHit(deepest);
                if (top.Node != null && !ReferenceEquals(top.Node, deepest.Node))
                {
                    WriteLine($"TOPLEVEL_FOR_DEEPEST path={top.Path}");
                    DumpTreeLimited("HoverContext:toplevel-subtree", top.Node, 0, maxDepth, top.Path, childLimit);
                }

                var parent = GetParentFromHit(deepest);
                if (parent.Node != null && !ReferenceEquals(parent.Node, deepest.Node))
                {
                    WriteLine($"PARENT_FOR_DEEPEST path={parent.Path}");
                    DumpTreeLimited("HoverContext:parent-subtree", parent.Node, 0, Math.Min(maxDepth, 6), parent.Path, childLimit);
                }
            }

            WriteLine("END_HOVER_CONTEXT_DUMP");
            WriteLine(string.Empty);
        }

        private void DumpKnownHoverObjects(float mouseClientX, float mouseClientY, int maxDepth, int childLimit)
        {
            string[] names = new[] { "HighlightedElement", "Hover", "HoveredElement", "MouseOverElement", "UIHover" };
            object ingame = null;
            object ui = null;
            try { ingame = _plugin.GameController.IngameState; } catch { }
            try { ui = _plugin.GameController.IngameState?.IngameUi; } catch { }

            foreach (var name in names)
            {
                object value = TryGetPropertyObject(ingame, name);
                if (value == null) value = TryGetPropertyObject(ui, name);
                if (value == null)
                {
                    WriteLine($"KNOWN_HOVER {name}=Null");
                    continue;
                }

                WriteLine($"KNOWN_HOVER {name}=present containsClient={ContainsPoint(value, mouseClientX, mouseClientY)}");
                DumpTreeLimited("HoverContext:" + name, value, 0, Math.Min(maxDepth, 6), name, childLimit);
            }
        }

        private void FindHitPaths(object node, string path, int depth, int maxDepth, int childLimit, float clientX, float clientY, float screenX, float screenY, System.Collections.Generic.List<PathNode> stack, System.Collections.Generic.List<HitPath> hits)
        {
            if (node == null || depth > maxDepth) return;

            stack.Add(new PathNode(path, node, depth));

            bool hitClient = ContainsPoint(node, clientX, clientY);
            bool hitScreen = ContainsPoint(node, screenX, screenY);
            if (hitClient || hitScreen)
            {
                string mode = hitClient && hitScreen ? "client+screen" : (hitClient ? "client" : "screen");
                hits.Add(new HitPath(path, node, depth, mode, stack.ToArray()));
            }

            object children = GetChildrenObject(node);
            int count = GetChildrenCount(children);
            if (count > 0 && depth < maxDepth)
            {
                int take = Math.Min(count, childLimit);
                for (int i = 0; i < take; i++)
                {
                    object child = GetChildAt(children, i);
                    if (child == null) continue;
                    FindHitPaths(child, path + "->" + i.ToString(CultureInfo.InvariantCulture), depth + 1, maxDepth, childLimit, clientX, clientY, screenX, screenY, stack, hits);
                }
                if (count > childLimit)
                    WriteLine($"HOVER_CONTEXT_CHILD_LIMIT path={path} count={count} scanned={childLimit}");
            }

            stack.RemoveAt(stack.Count - 1);
        }

        private void DumpTreeLimited(string scanName, object node, int depth, int maxDepth, string path, int childLimit)
        {
            if (node == null || depth > maxDepth) return;
            WriteCompactNode(scanName, depth, path, node);
            if (depth == maxDepth) return;

            object children = GetChildrenObject(node);
            int count = GetChildrenCount(children);
            if (count <= 0) return;
            int take = Math.Min(count, childLimit);
            if (count > childLimit)
                WriteLine($"TREE_CHILD_LIMIT scan={scanName} path={path} count={count} showing={take}");

            for (int i = 0; i < take; i++)
            {
                object child = GetChildAt(children, i);
                if (child == null) continue;
                DumpTreeLimited(scanName, child, depth + 1, maxDepth, path + "->" + i.ToString(CultureInfo.InvariantCulture), childLimit);
            }
        }

        private static PathNode GetTopLevelFromHit(HitPath hit)
        {
            if (hit.Stack == null || hit.Stack.Length == 0) return default(PathNode);
            if (hit.Stack.Length >= 2) return hit.Stack[1];
            return hit.Stack[0];
        }

        private static PathNode GetParentFromHit(HitPath hit)
        {
            if (hit.Stack == null || hit.Stack.Length == 0) return default(PathNode);
            if (hit.Stack.Length >= 2) return hit.Stack[hit.Stack.Length - 2];
            return hit.Stack[0];
        }

        private static bool ContainsPoint(object node, float x, float y)
        {
            float rx, ry, rw, rh;
            if (!TryGetRectNumbers(node, out rx, out ry, out rw, out rh)) return false;
            if (rw <= 0 || rh <= 0) return false;
            return x >= rx && x <= rx + rw && y >= ry && y <= ry + rh;
        }

        private static bool TryGetRectNumbers(object node, out float x, out float y, out float width, out float height)
        {
            x = y = width = height = 0;
            if (node == null) return false;
            try
            {
                dynamic d = node;
                dynamic r = d.GetClientRect();
                x = Convert.ToSingle(r.X, CultureInfo.InvariantCulture);
                y = Convert.ToSingle(r.Y, CultureInfo.InvariantCulture);
                width = Convert.ToSingle(r.Width, CultureInfo.InvariantCulture);
                height = Convert.ToSingle(r.Height, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                try
                {
                    x = Convert.ToSingle(ReadProperty(node, "X"), CultureInfo.InvariantCulture);
                    y = Convert.ToSingle(ReadProperty(node, "Y"), CultureInfo.InvariantCulture);
                    width = Convert.ToSingle(ReadProperty(node, "Width"), CultureInfo.InvariantCulture);
                    height = Convert.ToSingle(ReadProperty(node, "Height"), CultureInfo.InvariantCulture);
                    return true;
                }
                catch { return false; }
            }
        }

        private static object ReadProperty(object node, string property)
        {
            if (node == null) return null;
            var p = node.GetType().GetProperty(property);
            return p == null ? null : p.GetValue(node);
        }

        private static object TryGetPropertyObject(object node, string property)
        {
            try { return ReadProperty(node, property); } catch { return null; }
        }

        private static object GetChildrenObject(object node)
        {
            try
            {
                dynamic d = node;
                return d.Children;
            }
            catch { return null; }
        }

        private static int GetChildrenCount(object children)
        {
            if (children == null) return 0;
            try
            {
                dynamic c = children;
                return (int)c.Count;
            }
            catch { return 0; }
        }

        private static object GetChildAt(object children, int index)
        {
            try
            {
                dynamic c = children;
                return c[index];
            }
            catch { return null; }
        }

        private struct PathNode
        {
            public readonly string Path;
            public readonly object Node;
            public readonly int Depth;

            public PathNode(string path, object node, int depth)
            {
                Path = path;
                Node = node;
                Depth = depth;
            }
        }

        private sealed class HitPath
        {
            public readonly string Path;
            public readonly object Node;
            public readonly int Depth;
            public readonly string Mode;
            public readonly PathNode[] Stack;

            public HitPath(string path, object node, int depth, string mode, PathNode[] stack)
            {
                Path = path;
                Node = node;
                Depth = depth;
                Mode = mode;
                Stack = stack;
            }
        }

        public void BeginTick(bool acceptParty, bool acceptTrade)
        {
            if (!ReactionEnabled) return;

            _tickIndex++;
            _nodesThisTick = 0;
            _limitLoggedThisTick = false;

            WriteLine("================================================================================");
            WriteLine($"AutoParty scan tick #{_tickIndex} | {DateTime.Now:O}");
            WriteLine($"Area={Safe(() => _plugin.GameController.Area.CurrentArea.Name)} | PlayerAlive={Safe(() => _plugin.GameController.Player.IsAlive.ToString())}");
            WriteLine($"AcceptParty={acceptParty} | AcceptTrade={acceptTrade} | LeaderName={Safe(() => _plugin.Settings.General.LeaderName.Value)} | AcceptFrom={Safe(() => _plugin.Settings.TpTrade.AcceptFrom.Value)}");
        }

        public void EndTick(bool clicked)
        {
            if (!ReactionEnabled) return;
            WriteLine($"EndTick clicked={clicked} nodesLogged={_nodesThisTick}");
            WriteLine(string.Empty);
        }

        public void Event(string scanName, string message)
        {
            if (!NodeEnabled) return;
            WriteLine($"EVENT [{scanName}] {message}");
        }

        public void Node(string scanName, int depth, string path, object node, string reason)
        {
            if (!NodeEnabled) return;

            int limit = 2000;
            try { limit = Math.Max(10, _plugin.Settings.Debug.AutoPartyDebugMaxNodesPerTick.Value); } catch { }
            if (_nodesThisTick >= limit)
            {
                if (!_limitLoggedThisTick)
                {
                    _limitLoggedThisTick = true;
                    WriteLine($"NODE_LIMIT_HIT limit={limit}; further nodes suppressed for this tick.");
                }
                return;
            }

            _nodesThisTick++;
            var line = new StringBuilder(512);
            line.Append("NODE ");
            line.Append(scanName);
            line.Append(" depth=").Append(depth.ToString(CultureInfo.InvariantCulture));
            line.Append(" path=").Append(path ?? "?");
            line.Append(" reason=").Append(reason ?? "visit");
            line.Append(" type=").Append(GetTypeName(node));
            line.Append(" address=").Append(SafeValue(node, "Address"));
            line.Append(" visible=").Append(SafeValue(node, "IsVisible"));
            line.Append(" visibleLocal=").Append(SafeValue(node, "IsVisibleLocal"));
            line.Append(" active=").Append(SafeValue(node, "IsActive"));
            line.Append(" childCount=").Append(GetChildCount(node));
            line.Append(" rect=").Append(GetRect(node));

            string text = SafeString(node, "Text");
            string textNoTags = SafeString(node, "TextNoTags");
            string texture = SafeString(node, "TextureName");
            if (!string.IsNullOrEmpty(text)) line.Append(" Text=\"").Append(Escape(text)).Append('"');
            if (!string.IsNullOrEmpty(textNoTags)) line.Append(" TextNoTags=\"").Append(Escape(textNoTags)).Append('"');
            if (!string.IsNullOrEmpty(texture)) line.Append(" Texture=\"").Append(Escape(texture)).Append('"');

            WriteLine(line.ToString());
        }


        public void Reaction(string scanName, string kind, string path, object node, string text, string details)
        {
            if (!ReactionEnabled) return;
            WriteLine($"REACTION [{scanName}] kind={kind} path={path ?? "?"} text=\"{Escape(text ?? string.Empty)}\" details={details ?? string.Empty}");
            Node(scanName + ":reaction", 0, path, node, kind);
            DumpContext(scanName, path, node, 3);
        }

        public void DumpContext(string scanName, string path, object node, int maxDepth)
        {
            if (!ReactionEnabled || node == null) return;
            WriteLine($"CONTEXT [{scanName}] path={path ?? "?"} maxDepth={maxDepth}");
            DumpTree(scanName + ":context", node, 0, maxDepth, path ?? "node");
            WriteLine($"END_CONTEXT [{scanName}] path={path ?? "?"}");
        }

        private void DumpTree(string scanName, object node, int depth, int maxDepth, string path)
        {
            if (node == null || depth > maxDepth) return;
            WriteCompactNode(scanName, depth, path, node);
            if (depth == maxDepth) return;

            object children = null;
            try
            {
                dynamic d = node;
                children = d.Children;
            }
            catch { }

            if (children == null) return;
            int count = 0;
            try
            {
                dynamic c = children;
                count = (int)c.Count;
            }
            catch { return; }

            int childLimit = 80;
            try { childLimit = Math.Max(5, _plugin.Settings.Debug.AutoPartyReactionContextChildLimit.Value); } catch { }
            if (count > childLimit)
            {
                WriteLine($"CONTEXT_CHILD_LIMIT path={path} count={count} showing={childLimit}");
                count = childLimit;
            }

            for (int i = 0; i < count; i++)
            {
                object child = null;
                try
                {
                    dynamic c = children;
                    child = c[i];
                }
                catch { }
                if (child == null) continue;
                DumpTree(scanName, child, depth + 1, maxDepth, path + "->" + i.ToString(CultureInfo.InvariantCulture));
            }
        }

        private void WriteCompactNode(string scanName, int depth, string path, object node)
        {
            var line = new StringBuilder(512);
            line.Append("CTXNODE ");
            line.Append(scanName);
            line.Append(" depth=").Append(depth.ToString(CultureInfo.InvariantCulture));
            line.Append(" path=").Append(path ?? "?");
            line.Append(" type=").Append(GetTypeName(node));
            line.Append(" address=").Append(SafeValue(node, "Address"));
            line.Append(" visible=").Append(SafeValue(node, "IsVisible"));
            line.Append(" visibleLocal=").Append(SafeValue(node, "IsVisibleLocal"));
            line.Append(" active=").Append(SafeValue(node, "IsActive"));
            line.Append(" childCount=").Append(GetChildCount(node));
            line.Append(" rect=").Append(GetRect(node));
            string text = SafeString(node, "Text");
            string textNoTags = SafeString(node, "TextNoTags");
            string texture = SafeString(node, "TextureName");
            if (!string.IsNullOrEmpty(text)) line.Append(" Text=\"").Append(Escape(text)).Append('"');
            if (!string.IsNullOrEmpty(textNoTags)) line.Append(" TextNoTags=\"").Append(Escape(textNoTags)).Append('"');
            if (!string.IsNullOrEmpty(texture)) line.Append(" Texture=\"").Append(Escape(texture)).Append('"');
            WriteLine(line.ToString());
        }

        private string GetSessionFile()
        {
            if (!string.IsNullOrEmpty(_sessionFile)) return _sessionFile;

            string dir = null;
            try { dir = _plugin.Settings.Debug.AutoPartyDebugDirectory.Value; } catch { }
            if (string.IsNullOrWhiteSpace(dir))
                dir = @"D:\cookie\exile2 — kopia\testy\FollowerDebug";

            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            {
                dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FollowerDebug");
                Directory.CreateDirectory(dir);
            }

            _sessionFile = Path.Combine(dir, $"Follower_AUTOPARTY_SCAN_Debug_SESSION_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.AppendAllText(_sessionFile, "AutoParty scanner debug started " + DateTime.Now.ToString("O") + Environment.NewLine, Encoding.UTF8);
            return _sessionFile;
        }

        private void WriteLine(string text)
        {
            try
            {
                File.AppendAllText(GetSessionFile(), text + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                try { _plugin.LogMessage("AutoParty debug write failed: " + ex.Message, 5); } catch { }
            }
        }

        private static string GetTypeName(object node)
        {
            if (node == null) return "Null";
            try { return node.GetType().FullName ?? node.GetType().Name; } catch { return "?"; }
        }

        private static string SafeValue(object node, string property)
        {
            if (node == null) return "Null";
            try
            {
                var p = node.GetType().GetProperty(property);
                if (p == null) return "?";
                var value = p.GetValue(node);
                return value == null ? "Null" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            }
            catch { return "?"; }
        }

        private static string SafeString(object node, string property)
        {
            if (node == null) return null;
            try
            {
                var p = node.GetType().GetProperty(property);
                if (p == null) return null;
                return p.GetValue(node) as string;
            }
            catch { return null; }
        }

        private static int GetChildCount(object node)
        {
            if (node == null) return -1;
            try
            {
                dynamic d = node;
                dynamic children = d.Children;
                if (children == null) return 0;
                return (int)children.Count;
            }
            catch { return -1; }
        }

        private static string GetRect(object node)
        {
            if (node == null) return "Null";
            try
            {
                dynamic d = node;
                dynamic r = d.GetClientRect();
                return $"X={r.X},Y={r.Y},W={r.Width},H={r.Height},Center=({r.Center.X},{r.Center.Y})";
            }
            catch
            {
                try
                {
                    return $"X={SafeValue(node, "X")},Y={SafeValue(node, "Y")},W={SafeValue(node, "Width")},H={SafeValue(node, "Height")}";
                }
                catch { return "?"; }
            }
        }

        private static string Escape(string value)
        {
            if (value == null) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\"", "\\\"");
        }

        private static string Safe(Func<string> f)
        {
            try { return f() ?? "Null"; } catch { return "?"; }
        }
    }
}
