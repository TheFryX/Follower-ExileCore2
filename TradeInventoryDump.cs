using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
using System.IO;
using System.Threading;
using ExileCore2.PoEMemory.MemoryObjects;

namespace Follower
{
    internal sealed class TradeInventoryDump
    {
        private const int KeyDelayMs = 10;
        private const int MouseMoveDelayMs = 20;
        private const int MouseDownDelayMs = 10;
        private const int MouseUpDelayMs = 5;
        private const int AcceptRetryDelayMs = 500;
        private const int MaxAcceptAttempts = 6;
        private const int AcceptGlobalScanMaxNodes = 20000;
        private const int AcceptGlobalScanMaxDepth = 32;
        private const float CapturedAcceptCenterClientX = 115f;
        private const float CapturedAcceptCenterClientY = 984.25f;
        private const int MinimumAcceptDelayAfterDumpMs = 1600;
        private const int AcceptMoveSettleMs = 120;
        private const int AcceptMouseDownMs = 85;
        private const float AcceptScreenBottomOffsetY = 52f;
        private const string AcceptDebugFileName = "TradeDumpAcceptDebug.txt";
        private static int _globalSequenceCounter;


        private readonly Follower _plugin;

        private DumpState _state = DumpState.Idle;
        private ClickPhase _clickPhase = ClickPhase.None;
        private List<ServerInventory.InventSlotItem> _items = new List<ServerInventory.InventSlotItem>();
        private int _ignoredItemsSkippedInSnapshot;
        private int _itemIndex;
        private DateTime _nextActionAt = DateTime.MinValue;
        private DateTime _startedAt = DateTime.MinValue;
        private DateTime _finishDeadline = DateTime.MinValue;
        private DateTime _nextFinishChatScanAt = DateTime.MinValue;
        private Vector2 _currentItemScreenPos = Vector2.Zero;
        private Vector2 _previousMousePos = Vector2.Zero;
        private bool _ctrlHeld;
        private int _acceptAttempts;
        private long _lastFinishChatTotal = -1;
        private string _lastFinishChatKey = string.Empty;
        private string _startedByLeader = string.Empty;
        private string _startedCommand = string.Empty;
        private int _sequenceId;
        private bool _debugPathLoggedThisSequence;
        private bool _hasCachedAcceptScreenPoint;
        private Vector2 _cachedAcceptScreenPoint = Vector2.Zero;

        public TradeInventoryDump(Follower plugin)
        {
            _plugin = plugin;
        }

        private Vector2 WindowOffset => _plugin.GameController.Window.GetWindowRectangleTimeCache.TopLeft;

        public bool IsActive => _state != DumpState.Idle;

        public void Start(string leaderName, string commandText)
        {
            try
            {
                if (!(_plugin.Settings.TpTrade.AutoDumpInventoryToTrade?.Value ?? false))
                    return;

                if (_state != DumpState.Idle)
                {
                    _plugin.LogMessage("TradeDump: ignored duplicate dump command; sequence already running.", 3);
                    return;
                }

                _startedByLeader = leaderName ?? string.Empty;
                _startedCommand = commandText ?? string.Empty;
                _sequenceId = System.Threading.Interlocked.Increment(ref _globalSequenceCounter);
                _debugPathLoggedThisSequence = false;
                _startedAt = DateTime.Now;
                _nextActionAt = DateTime.MinValue;
                _finishDeadline = DateTime.MinValue;
                _nextFinishChatScanAt = DateTime.MinValue;
                _itemIndex = 0;
                _ignoredItemsSkippedInSnapshot = 0;
                _items.Clear();
                _clickPhase = ClickPhase.None;
                _acceptAttempts = 0;
                _previousMousePos = Mouse.GetCursorPositionVector();
                CaptureFinishChatBaseline();

                _plugin.ReleaseAllPluginInputsNow(force: true, reason: "TradeDump.Start.ReleaseFollowInputs");
                _state = DumpState.WaitForTradeWindow;
                WriteAcceptDebug("START leader=" + _startedByLeader + " command=" + _startedCommand);
                _plugin.LogMessage($"TradeDump: leader {_startedByLeader} sent {_startedCommand}; waiting for trade window.", 3);
            }
            catch (Exception ex)
            {
                Abort("start error: " + ex.Message);
            }
        }

        /// <summary>
        /// Returns true when the normal follower loop should be blocked for this frame.
        /// Waiting for the trade window does not block; dumping and accepting do.
        /// </summary>
        public bool Tick()
        {
            if (_state == DumpState.Idle)
                return false;

            try
            {
                if (!(_plugin.Settings.TpTrade.AutoDumpInventoryToTrade?.Value ?? false))
                {
                    Abort("setting disabled");
                    return false;
                }

                var now = DateTime.Now;
                switch (_state)
                {
                    case DumpState.WaitForTradeWindow:
                        return TickWaitForTradeWindow(now);

                    case DumpState.PrepareInventorySnapshot:
                        return TickPrepareInventorySnapshot(now);

                    case DumpState.DumpingItems:
                        return TickDumpingItems(now);

                    case DumpState.WaitBeforeAccept:
                        return TickWaitBeforeAccept(now);

                    case DumpState.Accepting:
                        return TickAccepting(now);

                    case DumpState.WaitingForFinish:
                        return TickWaitingForFinish(now);

                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                Abort("tick error: " + ex.Message);
                return false;
            }
        }

        private bool TickWaitForTradeWindow(DateTime now)
        {
            var timeoutMs = Math.Max(1000, _plugin.Settings.TpTrade.TradeDumpWindowWaitTimeoutMs.Value);
            if ((now - _startedAt).TotalMilliseconds > timeoutMs)
            {
                Abort("trade window wait timeout");
                return false;
            }

            if (!IsTradeWindowOpen())
                return false;

            _plugin.ReleaseAllPluginInputsNow(force: true, reason: "TradeDump.TradeWindowOpen.ReleaseFollowInputs");
            _state = DumpState.PrepareInventorySnapshot;
            _nextActionAt = now.AddMilliseconds(100);
            _plugin.LogMessage("TradeDump: trade window detected; preparing inventory dump.", 3);
            return true;
        }

        private bool TickPrepareInventorySnapshot(DateTime now)
        {
            if (now < _nextActionAt)
                return true;

            if (!IsTradeWindowOpen())
            {
                Abort("trade window closed before dump");
                return false;
            }

            if (!IsInventoryPanelVisible())
            {
                _nextActionAt = now.AddMilliseconds(100);
                return true;
            }

            _items = GetInventoryItemsSnapshot();
            _itemIndex = 0;
            _clickPhase = ClickPhase.None;

            if (_items.Count == 0)
            {
                var noDumpableItemsMessage = _ignoredItemsSkippedInSnapshot > 0
                    ? $"TradeDump: no dumpable inventory items; skipped {_ignoredItemsSkippedInSnapshot} ignored item(s); clicking trade accept."
                    : "TradeDump: inventory is empty; clicking trade accept.";
                _plugin.LogMessage(noDumpableItemsMessage, 3);
                _state = DumpState.WaitBeforeAccept;
                _nextActionAt = now.AddMilliseconds(GetAcceptDelayAfterDumpMs());
                return true;
            }

            var dumpMessage = _ignoredItemsSkippedInSnapshot > 0
                ? $"TradeDump: dumping {_items.Count} inventory item(s) to trade; skipped {_ignoredItemsSkippedInSnapshot} ignored item(s)."
                : $"TradeDump: dumping {_items.Count} inventory item(s) to trade.";
            _plugin.LogMessage(dumpMessage, 3);
            _state = DumpState.DumpingItems;
            _nextActionAt = now;
            return true;
        }

        private bool TickDumpingItems(DateTime now)
        {
            if (!IsTradeWindowOpen())
            {
                Abort("trade window closed during dump");
                return false;
            }

            if (now < _nextActionAt)
                return true;

            if (_itemIndex >= _items.Count)
            {
                ReleaseCtrlIfHeld();
                _state = DumpState.WaitBeforeAccept;
                _nextActionAt = now.AddMilliseconds(GetAcceptDelayAfterDumpMs());
                _plugin.LogMessage("TradeDump: inventory dump clicks finished; preparing accept click.", 3);
                return true;
            }

            if (_clickPhase == ClickPhase.None || _clickPhase == ClickPhase.BetweenItems)
            {
                BeginCurrentItemClick(now);
                return true;
            }

            switch (_clickPhase)
            {
                case ClickPhase.AfterCtrlDown:
                    if (Mouse.IsGuardLocked)
                    {
                        _nextActionAt = now.AddMilliseconds(50);
                        return true;
                    }
                    Mouse.SetCursorPos(_currentItemScreenPos);
                    _clickPhase = ClickPhase.AfterMouseMove;
                    _nextActionAt = now.AddMilliseconds(MouseMoveDelayMs);
                    return true;

                case ClickPhase.AfterMouseMove:
                    Mouse.LeftMouseDown();
                    _clickPhase = ClickPhase.AfterMouseDown;
                    _nextActionAt = now.AddMilliseconds(MouseDownDelayMs);
                    return true;

                case ClickPhase.AfterMouseDown:
                    Mouse.LeftMouseUp();
                    _clickPhase = ClickPhase.AfterMouseUp;
                    _nextActionAt = now.AddMilliseconds(MouseUpDelayMs);
                    return true;

                case ClickPhase.AfterMouseUp:
                    ReleaseCtrlIfHeld();
                    _plugin.CompletePluginMouseAction("TradeDump.ItemClick.Complete");
                    _itemIndex++;
                    _clickPhase = ClickPhase.BetweenItems;
                    _nextActionAt = now.AddMilliseconds(Math.Max(20, _plugin.Settings.TpTrade.TradeDumpItemDelayMs.Value));
                    return true;

                default:
                    _clickPhase = ClickPhase.None;
                    return true;
            }
        }

        private void BeginCurrentItemClick(DateTime now)
        {
            var item = _items[_itemIndex];
            _currentItemScreenPos = GetItemCenterScreen(item);
            if (!IsReasonablePoint(_currentItemScreenPos))
            {
                _plugin.LogMessage($"TradeDump: skipped item #{_itemIndex + 1}; invalid screen position {_currentItemScreenPos}.", 3);
                _itemIndex++;
                _clickPhase = ClickPhase.BetweenItems;
                _nextActionAt = now.AddMilliseconds(Math.Max(20, _plugin.Settings.TpTrade.TradeDumpItemDelayMs.Value));
                return;
            }

            _plugin.PrepareForPluginMouseAction("TradeDump.ItemClick.Prepare");
            Keyboard.KeyDown(Keys.LControlKey);
            _ctrlHeld = true;
            _clickPhase = ClickPhase.AfterCtrlDown;
            _nextActionAt = now.AddMilliseconds(KeyDelayMs);
        }

        private bool TickWaitBeforeAccept(DateTime now)
        {
            ReleaseCtrlIfHeld();

            if (!IsTradeWindowOpen())
            {
                Complete("trade window closed before accept");
                return false;
            }

            if (now < _nextActionAt)
                return true;

            _state = DumpState.Accepting;
            _nextActionAt = now;
            _acceptAttempts = 0;
            return true;
        }

        private bool TickAccepting(DateTime now)
        {
            if (!IsTradeWindowOpen())
            {
                Complete("trade window closed before accept click");
                return false;
            }

            if (now < _nextActionAt)
                return true;

            _acceptAttempts++;

            // First try the direct known Accept path and cache the real screen coordinate. Do not do any deep/global UI scan here.
            // After the first successful resolve, later trades use the cached point first.
            if (ClickTradeAcceptFastPath())
            {
                MarkAcceptClicked(now);
                return true;
            }

            // Last resort: click a fixed area at the bottom-left of the current screen/window. This avoids the DPI/virtual-screen
            // mismatch where ExileCore reports Y~984 but SetCursorPos clamps to the physical/logical screen bottom.
            if (ClickTradeAcceptFallback())
            {
                MarkAcceptClicked(now);
                return true;
            }

            if (_acceptAttempts >= MaxAcceptAttempts)
            {
                _plugin.LogMessage("TradeDump: could not find/click trade Accept after retries; ending sequence.", 5);
                Complete("accept button unavailable");
                return false;
            }

            _nextActionAt = now.AddMilliseconds(AcceptRetryDelayMs);
            return true;
        }

        private void MarkAcceptClicked(DateTime now)
        {
            _state = DumpState.WaitingForFinish;
            _finishDeadline = now.AddMilliseconds(Math.Max(5000, _plugin.Settings.TpTrade.TradeDumpFinishTimeoutMs.Value));
            _nextFinishChatScanAt = DateTime.MinValue;
            _plugin.LogMessage("TradeDump: clicked trade Accept; waiting for finish/cancel.", 3);
        }

        private bool TickWaitingForFinish(DateTime now)
        {
            ReleaseCtrlIfHeld();

            if (!IsTradeWindowOpen())
            {
                Complete("trade window closed");
                return false;
            }

            if (now >= _finishDeadline)
            {
                Complete("finish timeout");
                return false;
            }

            if (now >= _nextFinishChatScanAt)
            {
                _nextFinishChatScanAt = now.AddMilliseconds(500);
                if (SawTradeFinishedChatMessage())
                {
                    Complete("trade chat finish message");
                    return false;
                }
            }

            return true;
        }

        private List<ServerInventory.InventSlotItem> GetInventoryItemsSnapshot()
        {
            _ignoredItemsSkippedInSnapshot = 0;

            try
            {
                _plugin.Settings.TpTrade.TradeDumpIgnoredCells = InventoryGridIgnoreHelper.Normalize(_plugin.Settings.TpTrade.TradeDumpIgnoredCells);
                var ignoredCells = _plugin.Settings.TpTrade.TradeDumpIgnoredCells;

                var inventoryItems = _plugin.GameController.IngameState.ServerData.PlayerInventories[0]
                    .Inventory.InventorySlotItems
                    .Where(x => x.Item != null)
                    .OrderBy(x => x.PosY)
                    .ThenBy(x => x.PosX)
                    .ToList();

                if (InventoryGridIgnoreHelper.CountIgnoredCells(ignoredCells) == 0)
                    return inventoryItems;

                var dumpableItems = new List<ServerInventory.InventSlotItem>(inventoryItems.Count);
                foreach (var item in inventoryItems)
                {
                    if (InventoryGridIgnoreHelper.IsIgnored(item, ignoredCells))
                    {
                        _ignoredItemsSkippedInSnapshot++;
                        continue;
                    }

                    dumpableItems.Add(item);
                }

                return dumpableItems;
            }
            catch (Exception ex)
            {
                _plugin.LogMessage("TradeDump: inventory read failed: " + ex.Message, 5);
                return new List<ServerInventory.InventSlotItem>();
            }
        }

        private Vector2 GetItemCenterScreen(ServerInventory.InventSlotItem item)
        {
            try
            {
                var center = item.GetClientRect().Center;
                return center + WindowOffset;
            }
            catch
            {
                return Vector2.Zero;
            }
        }

        private bool IsTradeWindowOpen()
        {
            try
            {
                dynamic ui = _plugin.GameController.IngameState?.IngameUi;
                if (ui == null) return false;

                dynamic tradeWindow = null;
                try { tradeWindow = ui.TradeWindow; } catch { tradeWindow = null; }
                if (IsVisibleElement(tradeWindow)) return true;

                dynamic cardTradeWindow = null;
                try { cardTradeWindow = ui.CardTradeWindow; } catch { cardTradeWindow = null; }
                if (IsVisibleElement(cardTradeWindow)) return true;

                // Captured PoE2 path from DevTree: IngameUi -> Children[83] is CardTradeWindow in this build.
                var pathRoot = GetChild(ui, 83);
                if (IsVisibleElement(pathRoot)) return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        private bool IsInventoryPanelVisible()
        {
            try
            {
                dynamic ui = _plugin.GameController.IngameState?.IngameUi;
                if (ui == null) return false;

                dynamic panel = null;
                try { panel = ui.InventoryPanel; } catch { panel = null; }
                if (IsVisibleElement(panel)) return true;

                try { panel = ui.OpenRightPanel?.InventoryPanel; } catch { panel = null; }
                if (IsVisibleElement(panel)) return true;

                try { panel = ui.Inventory; } catch { panel = null; }
                if (IsVisibleElement(panel)) return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        private dynamic FindTradeAcceptButton()
        {
            try
            {
                dynamic ui = _plugin.GameController.IngameState?.IngameUi;
                if (ui == null) return null;

                // Captured hover path points at the clickable button itself:
                // IngameUi -> 83 -> 3 -> 1 -> 0 -> 0 -> 5.
                // Its child [0] is only the text node: "accept". Clicking the text node is unreliable,
                // so we resolve the parent button and click its rectangle center.
                var knownButton = GetChildPath(ui, new[] { 83, 3, 1, 0, 0, 5 });
                if (IsVisibleElement(knownButton))
                    return knownButton;

                var knownText = GetChildPath(ui, new[] { 83, 3, 1, 0, 0, 5, 0 });
                if (IsVisibleElement(knownText) && TextEquals(knownText, "accept"))
                    return PreferClickableParent(knownText);

                // Some ExileCore2 builds expose the same UI object in the dev tree, but Children.Count
                // can be unreliable after the trade window has been reopened. A global PathFromRoot scan
                // is slower, but it runs only during the final Accept click and reliably catches the
                // captured button path: 83->3->1->0->0->5.
                var pathButton = FindNodeByPathFromRoot(ui, "83->3->1->0->0->5", AcceptGlobalScanMaxNodes, AcceptGlobalScanMaxDepth);
                if (IsVisibleElement(pathButton))
                    return pathButton;

                var pathText = FindNodeByPathFromRoot(ui, "83->3->1->0->0->5->0", AcceptGlobalScanMaxNodes, AcceptGlobalScanMaxDepth);
                if (IsVisibleElement(pathText) && TextEquals(pathText, "accept"))
                    return PreferClickableParent(pathText);

                // Same path, but relative to CardTradeWindow / TradeWindow root if the root is exposed as a property.
                dynamic root = null;
                try { root = ui.CardTradeWindow; } catch { root = null; }
                var relativeButton = GetChildPath(root, new[] { 3, 1, 0, 0, 5 });
                if (IsVisibleElement(relativeButton))
                    return relativeButton;

                var relativeText = GetChildPath(root, new[] { 3, 1, 0, 0, 5, 0 });
                if (IsVisibleElement(relativeText) && TextEquals(relativeText, "accept"))
                    return PreferClickableParent(relativeText);

                try { root = ui.TradeWindow; } catch { root = null; }
                relativeButton = GetChildPath(root, new[] { 3, 1, 0, 0, 5 });
                if (IsVisibleElement(relativeButton))
                    return relativeButton;

                relativeText = GetChildPath(root, new[] { 3, 1, 0, 0, 5, 0 });
                if (IsVisibleElement(relativeText) && TextEquals(relativeText, "accept"))
                    return PreferClickableParent(relativeText);

                // First do a bounded scan of the trade root. If ExileCore2 exposes a stale or partial
                // trade root after reopening trade, fall back to a full IngameUi scan. This is only done
                // during the Accept phase, so the extra cost is negligible compared to missing the button.
                root = GetTradeRoot(ui);
                if (root != null)
                {
                    var scan = new AcceptScanState { MaxNodes = 5000, MaxDepth = 22, UiHeight = GetUiHeight(ui) };
                    ScanForAccept(root, 0, scan);
                    if (scan.AcceptNode != null)
                        return scan.AcceptNode;
                }

                var globalScan = new AcceptScanState { MaxNodes = AcceptGlobalScanMaxNodes, MaxDepth = AcceptGlobalScanMaxDepth, UiHeight = GetUiHeight(ui) };
                ScanForAccept(ui, 0, globalScan);
                return globalScan.AcceptNode;
            }
            catch
            {
                return null;
            }
        }

        private dynamic GetTradeRoot(dynamic ui)
        {
            if (ui == null) return null;

            dynamic root = null;
            try { root = ui.CardTradeWindow; } catch { root = null; }
            if (IsVisibleElement(root)) return root;

            try { root = ui.TradeWindow; } catch { root = null; }
            if (IsVisibleElement(root)) return root;

            root = GetChild(ui, 83);
            if (IsVisibleElement(root)) return root;

            return null;
        }

        private void ScanForAccept(dynamic node, int depth, AcceptScanState state)
        {
            if (node == null || state.AcceptNode != null || state.NodesVisited >= state.MaxNodes || depth > state.MaxDepth)
                return;

            state.NodesVisited++;
            if (!IsVisibleElement(node))
                return;

            if (TextEquals(node, "accept"))
            {
                state.AcceptNode = PreferClickableParent(node);
                return;
            }

            if (LooksLikeAcceptButton(node))
            {
                state.AcceptNode = node;
                return;
            }

            if (LooksLikeAcceptButtonGeometry(node, state.UiHeight))
            {
                state.AcceptNode = node;
                return;
            }

            dynamic children = null;
            try { children = node.Children; } catch { children = null; }
            if (children == null) return;

            var count = CountOf(children);
            for (var i = 0; i < count; i++)
            {
                dynamic child = null;
                try { child = children[i]; } catch { child = null; }
                if (child == null) continue;
                ScanForAccept(child, depth + 1, state);
                if (state.AcceptNode != null || state.NodesVisited >= state.MaxNodes)
                    return;
            }
        }

        private bool ClickTradeAcceptFastPath()
        {
            try
            {
                if (_hasCachedAcceptScreenPoint && IsReasonablePoint(_cachedAcceptScreenPoint))
                {
                    _plugin.LogMessage($"TradeDump: using cached Accept click at {_cachedAcceptScreenPoint}.", 3);
                    WriteAcceptDebug($"FASTPATH cachedAccept={FormatPoint(_cachedAcceptScreenPoint)} cursor={FormatPoint(Mouse.GetCursorPositionVector())} screen={FormatScreenBounds(GetCurrentScreenBounds())}");
                    return ClickScreenPoint(_cachedAcceptScreenPoint, "TradeDump.AcceptCachedPoint");
                }

                var button = FindTradeAcceptButtonFast();
                if (!IsVisibleElement(button))
                {
                    WriteAcceptDebug("FASTPATH direct Accept path not visible/found");
                    return false;
                }

                var center = CenterOfElementOnScreen(button);
                var client = CenterOfElementClient(button);
                WriteAcceptDebug($"FASTPATH found button clientCenter={FormatPoint(client)} screenCenter={FormatPoint(center)} " +
                                 $"buttonX={FloatPropertyOf(button, "X", -1):0.##} buttonY={FloatPropertyOf(button, "Y", -1):0.##} " +
                                 $"buttonW={FloatPropertyOf(button, "Width", -1):0.##} buttonH={FloatPropertyOf(button, "Height", -1):0.##} " +
                                 $"screen={FormatScreenBounds(GetCurrentScreenBounds())}");

                if (!IsReasonablePoint(center))
                    return false;

                var clicked = ClickScreenPoint(center, "TradeDump.AcceptDirectPathCached");
                if (clicked)
                {
                    _cachedAcceptScreenPoint = center;
                    _hasCachedAcceptScreenPoint = true;
                }
                return clicked;
            }
            catch (Exception ex)
            {
                WriteAcceptDebug("FASTPATH exception: " + ex.Message);
                return false;
            }
        }

        private dynamic FindTradeAcceptButtonFast()
        {
            try
            {
                dynamic ui = _plugin.GameController.IngameState?.IngameUi;
                if (ui == null) return null;

                // Full path from the user's hover: IngameUi -> 83 -> 3 -> 1 -> 0 -> 0 -> 5.
                var button = GetChildPath(ui, new[] { 83, 3, 1, 0, 0, 5 });
                if (IsVisibleElement(button)) return button;

                var text = GetChildPath(ui, new[] { 83, 3, 1, 0, 0, 5, 0 });
                if (IsVisibleElement(text) && TextEquals(text, "accept"))
                    return PreferClickableParent(text);

                // Same thing, but relative to CardTradeWindow / TradeWindow if exposed as properties.
                dynamic root = null;
                try { root = ui.CardTradeWindow; } catch { root = null; }
                button = GetChildPath(root, new[] { 3, 1, 0, 0, 5 });
                if (IsVisibleElement(button)) return button;

                text = GetChildPath(root, new[] { 3, 1, 0, 0, 5, 0 });
                if (IsVisibleElement(text) && TextEquals(text, "accept"))
                    return PreferClickableParent(text);

                try { root = ui.TradeWindow; } catch { root = null; }
                button = GetChildPath(root, new[] { 3, 1, 0, 0, 5 });
                if (IsVisibleElement(button)) return button;

                text = GetChildPath(root, new[] { 3, 1, 0, 0, 5, 0 });
                if (IsVisibleElement(text) && TextEquals(text, "accept"))
                    return PreferClickableParent(text);

                return null;
            }
            catch
            {
                return null;
            }
        }

        private bool ClickElement(dynamic node, string reason)
        {
            try
            {
                var center = CenterOfElementOnScreen(node);
                if (!IsReasonablePoint(center))
                    return false;

                return ClickScreenPoint(center, reason);
            }
            catch (Exception ex)
            {
                _plugin.LogMessage("TradeDump: click element failed: " + ex.Message, 5);
                return false;
            }
        }

        private bool ClickTradeAcceptFallback()
        {
            try
            {
                if (!TryGetTradeAcceptFallbackPoint(out var point, out var source))
                    return false;

                _plugin.LogMessage($"TradeDump: using no-scan Accept click ({source}) at {point}.", 3);
                return ClickScreenPoint(point, "TradeDump.AcceptFallback");
            }
            catch (Exception ex)
            {
                _plugin.LogMessage("TradeDump: accept fallback failed: " + ex.Message, 5);
                return false;
            }
        }

        private bool ClickScreenPoint(Vector2 center, string reason)
        {
            try
            {
                if (!IsReasonablePoint(center))
                {
                    WriteAcceptDebug($"CLICK skipped: unreasonable target={FormatPoint(center)} reason={reason}");
                    return false;
                }

                ReleaseCtrlIfHeld();
                ForceReleaseModifierKeys();
                _plugin.PrepareForPluginMouseAction(reason + ".Prepare");

                var before = Mouse.GetCursorPositionVector();
                var guardBefore = Mouse.IsGuardLocked;
                if (guardBefore)
                {
                    WriteAcceptDebug($"CLICK skipped: MouseGuard locked before click target={FormatPoint(center)} cursorBefore={FormatPoint(before)} reason={reason}");
                    return false;
                }

                // Do the Accept click with raw SetCursorPos + explicit down/up instead of the helper.
                // This gives us a reliable debug trail and avoids silent no-op paths inside guarded helpers.
                var targetX = (int)Math.Round(center.X);
                var targetY = (int)Math.Round(center.Y);
                var setOk = Mouse.SetCursorPos(targetX, targetY);
                Thread.Sleep(AcceptMoveSettleMs);
                var afterMove = Mouse.GetCursorPositionVector();
                var moveDistance = Vector2.Distance(afterMove, new Vector2(targetX, targetY));

                Mouse.LeftMouseUp();
                Thread.Sleep(20);
                Mouse.LeftMouseDown();
                Thread.Sleep(AcceptMouseDownMs);
                Mouse.LeftMouseUp();
                Thread.Sleep(40);

                var afterClick = Mouse.GetCursorPositionVector();
                _plugin.CompletePluginMouseAction(reason + ".Complete");

                WriteAcceptDebug(
                    $"CLICK reason={reason} target={targetX},{targetY} setCursorOk={setOk} " +
                    $"cursorBefore={FormatPoint(before)} cursorAfterMove={FormatPoint(afterMove)} " +
                    $"moveDistance={moveDistance:0.0} cursorAfterClick={FormatPoint(afterClick)} " +
                    $"windowOffset={FormatPoint(WindowOffset)} tradeOpen={IsTradeWindowOpen()} inventoryVisible={IsInventoryPanelVisible()}");

                return setOk && moveDistance <= 12f;
            }
            catch (Exception ex)
            {
                WriteAcceptDebug("CLICK exception: " + ex);
                _plugin.LogMessage("TradeDump: screen click failed: " + ex.Message, 5);
                return false;
            }
        }

        private bool TryGetTradeAcceptFallbackPoint(out Vector2 screenPoint, out string source)
        {
            screenPoint = Vector2.Zero;
            source = string.Empty;

            dynamic ui = null;
            try { ui = _plugin.GameController.IngameState?.IngameUi; } catch { ui = null; }

            var bounds = GetCurrentScreenBounds();
            var windowOffset = WindowOffset;

            // X from the captured hover. Y must be adapted because ExileCore can report a 2560x1600 UI
            // while WinAPI SetCursorPos works in a smaller DPI-virtualized coordinate space.
            // In the user's debug, target Y=984 was clamped to Y=761, so the real button is near the screen bottom,
            // not at ExileCore's raw Y coordinate.
            var rawHover = new Vector2(CapturedAcceptCenterClientX, CapturedAcceptCenterClientY) + windowOffset;
            var x = rawHover.X;
            var y = rawHover.Y;

            var bottomAnchoredY = bounds.Bottom - AcceptScreenBottomOffsetY;
            if (y >= bounds.Bottom - 5)
                y = bottomAnchoredY;

            x = ClampFloat(x, bounds.Left + 5, bounds.Right - 5);
            y = ClampFloat(y, bounds.Top + 5, bounds.Bottom - 5);

            var point = new Vector2(x, y);
            source = $"fixed hover X + screen-bottom Y fallback; rawHover={FormatPoint(rawHover)} bounds={FormatScreenBounds(bounds)} bottomOffset={AcceptScreenBottomOffsetY:0.##}";

            WriteAcceptDebug(
                $"POINT source={source} screen={FormatPoint(point)} windowOffset={FormatPoint(windowOffset)} " +
                $"uiWidth={GetUiWidth(ui):0.##} uiHeight={GetUiHeight(ui):0.##} " +
                $"tradeOpen={IsTradeWindowOpen()} inventoryVisible={IsInventoryPanelVisible()} cursor={FormatPoint(Mouse.GetCursorPositionVector())}");

            if (!IsReasonablePoint(point))
                return false;

            screenPoint = point;
            return true;
        }

        private int GetAcceptDelayAfterDumpMs()
        {
            try
            {
                return Math.Max(MinimumAcceptDelayAfterDumpMs, _plugin.Settings.TpTrade.TradeDumpAcceptDelayMs.Value);
            }
            catch
            {
                return MinimumAcceptDelayAfterDumpMs;
            }
        }

        private void ForceReleaseModifierKeys()
        {
            try { Keyboard.KeyUp(Keys.LControlKey); } catch { }
            try { Keyboard.KeyUp(Keys.RControlKey); } catch { }
            try { Keyboard.KeyUp(Keys.LShiftKey); } catch { }
            try { Keyboard.KeyUp(Keys.RShiftKey); } catch { }
            try { Keyboard.KeyUp(Keys.Menu); } catch { }
        }

        private void WriteAcceptDebug(string message)
        {
            try
            {
                if (!_plugin.Settings.Debug.DebugTradeDumpAcceptToTxt.Value)
                    return;

                var dir = DebugDirectory();
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, AcceptDebugFileName);
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} seq={_sequenceId} state={_state} attempt={_acceptAttempts} {message}{Environment.NewLine}";
                File.AppendAllText(file, line);

                if (!_debugPathLoggedThisSequence && _sequenceId > 0)
                {
                    _debugPathLoggedThisSequence = true;
                    _plugin.LogMessage("TradeDump: accept debug txt: " + file, 3);
                }
            }
            catch { }
        }

        private string DebugDirectory()
        {
            try
            {
                var configured = _plugin.Settings.Debug.AutoPartyDebugDirectory.Value;
                if (!string.IsNullOrWhiteSpace(configured))
                    return configured;
            }
            catch { }

            return Path.Combine(Path.GetTempPath(), "FollowerDebug");
        }

        private static string FormatPoint(Vector2 point)
        {
            return $"<{point.X:0.##}, {point.Y:0.##}>";
        }

        private static float ClampFloat(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static System.Drawing.Rectangle GetCurrentScreenBounds()
        {
            try
            {
                var cursor = System.Windows.Forms.Cursor.Position;
                return System.Windows.Forms.Screen.FromPoint(cursor).Bounds;
            }
            catch { }

            try
            {
                return System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            }
            catch { }

            return new System.Drawing.Rectangle(0, 0, 2560, 768);
        }

        private static string FormatScreenBounds(System.Drawing.Rectangle bounds)
        {
            return $"<L={bounds.Left},T={bounds.Top},R={bounds.Right},B={bounds.Bottom},W={bounds.Width},H={bounds.Height}>";
        }

        private static void AddIfNotNull(List<dynamic> list, dynamic value)
        {
            if (value != null) list.Add(value);
        }

        private void CaptureFinishChatBaseline()
        {
            try
            {
                dynamic chatBox = GetChatBox();
                if (chatBox == null)
                {
                    _lastFinishChatTotal = -1;
                    _lastFinishChatKey = string.Empty;
                    return;
                }

                _lastFinishChatTotal = TotalMessageCountOf(chatBox);
                var latest = ReadLatestChatEntries(chatBox, 1, _lastFinishChatTotal).LastOrDefault();
                _lastFinishChatKey = latest.Key ?? string.Empty;
            }
            catch
            {
                _lastFinishChatTotal = -1;
                _lastFinishChatKey = string.Empty;
            }
        }

        private bool SawTradeFinishedChatMessage()
        {
            try
            {
                dynamic chatBox = GetChatBox();
                if (chatBox == null) return false;

                var currentTotal = TotalMessageCountOf(chatBox);
                var entriesToRead = 5;
                if (currentTotal >= 0 && _lastFinishChatTotal >= 0)
                {
                    if (currentTotal <= _lastFinishChatTotal)
                        return false;

                    entriesToRead = (int)Math.Min(5, currentTotal - _lastFinishChatTotal);
                }

                var entries = ReadLatestChatEntries(chatBox, entriesToRead, currentTotal);
                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Text))
                        continue;

                    if (string.Equals(entry.Key, _lastFinishChatKey, StringComparison.Ordinal))
                        continue;

                    _lastFinishChatKey = entry.Key;
                    if (LooksLikeTradeFinished(entry.Text))
                    {
                        _lastFinishChatTotal = currentTotal;
                        return true;
                    }
                }

                if (currentTotal >= 0)
                    _lastFinishChatTotal = currentTotal;

                return false;
            }
            catch
            {
                return false;
            }
        }

        private dynamic GetChatBox()
        {
            try { return _plugin.GameController.IngameState?.IngameUi?.ChatPanel?.ChatBox; }
            catch { return null; }
        }

        private static List<ChatEntry> ReadLatestChatEntries(dynamic chatBox, int entriesToRead, long totalMessageCount)
        {
            var result = new List<ChatEntry>();
            if (chatBox == null || entriesToRead <= 0) return result;

            dynamic messageElements = null;
            try { messageElements = chatBox.MessageElements; } catch { messageElements = null; }
            if (messageElements == null) return result;

            var count = CountOf(messageElements);
            if (count <= 0) return result;

            var start = Math.Max(0, count - Math.Min(entriesToRead, count));
            for (var i = start; i < count; i++)
            {
                dynamic node = null;
                try { node = messageElements[i]; } catch { node = null; }
                if (node == null) continue;

                var text = NormalizeText(StripSimpleTags(TextOf(node)));
                if (string.IsNullOrWhiteSpace(text)) continue;
                result.Add(new ChatEntry(IndexInParentOf(node, i), totalMessageCount, text));
            }

            return result;
        }

        private static bool LooksLikeTradeFinished(string text)
        {
            var clean = NormalizeText(StripSimpleTags(text));
            return clean.IndexOf("Trade accepted", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   clean.IndexOf("Trade cancelled", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   clean.IndexOf("Trade canceled", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void Complete(string reason)
        {
            ReleaseCtrlIfHeld();
            try
            {
                if (_previousMousePos != Vector2.Zero && IsReasonablePoint(_previousMousePos) && !Mouse.IsGuardLocked)
                    Mouse.SetCursorPos(_previousMousePos);
            }
            catch { }

            _plugin.ReleaseAllPluginInputsNow(force: true, reason: "TradeDump.Complete.ReleaseFollowInputs");
            _plugin.LogMessage("TradeDump: sequence finished (" + reason + ").", 3);
            Reset();
        }

        private void Abort(string reason)
        {
            ReleaseCtrlIfHeld();
            _plugin.ReleaseAllPluginInputsNow(force: true, reason: "TradeDump.Abort.ReleaseFollowInputs");
            try { _plugin.LogMessage("TradeDump: aborted (" + reason + ").", 5); } catch { }
            Reset();
        }

        private void Reset()
        {
            _state = DumpState.Idle;
            _clickPhase = ClickPhase.None;
            _items.Clear();
            _itemIndex = 0;
            _ignoredItemsSkippedInSnapshot = 0;
            _nextActionAt = DateTime.MinValue;
            _startedAt = DateTime.MinValue;
            _finishDeadline = DateTime.MinValue;
            _nextFinishChatScanAt = DateTime.MinValue;
            _currentItemScreenPos = Vector2.Zero;
            _previousMousePos = Vector2.Zero;
            _ctrlHeld = false;
            _acceptAttempts = 0;
            _startedByLeader = string.Empty;
            _startedCommand = string.Empty;
            _debugPathLoggedThisSequence = false;
        }

        private void ReleaseCtrlIfHeld()
        {
            if (!_ctrlHeld) return;
            try { Keyboard.KeyUp(Keys.LControlKey); } catch { }
            _ctrlHeld = false;
        }

        private static bool IsVisibleElement(dynamic element)
        {
            if (element == null) return false;
            try { if ((bool)element.IsVisible) return true; } catch { }
            try { if ((bool)element.IsVisibleLocal) return true; } catch { }
            return false;
        }

        private static bool TextEquals(dynamic element, string expected)
        {
            var text = NormalizeText(StripSimpleTags(TextOf(element)));
            return string.Equals(text, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeAcceptButton(dynamic element)
        {
            if (!IsVisibleElement(element)) return false;

            if (TextEquals(element, "accept"))
                return true;

            var firstChild = GetChild(element, 0);
            if (IsVisibleElement(firstChild) && TextEquals(firstChild, "accept"))
                return true;

            return false;
        }

        private static bool LooksLikeAcceptButtonGeometry(dynamic element, float uiHeight)
        {
            if (!IsVisibleElement(element)) return false;

            var width = FloatPropertyOf(element, "Width", -1);
            var height = FloatPropertyOf(element, "Height", -1);
            var x = FloatPropertyOf(element, "X", -1);
            var y = FloatPropertyOf(element, "Y", -1);
            var childCount = ChildCountOf(element);

            if (width < 150f || width > 420f) return false;
            if (height < 35f || height > 110f) return false;
            if (x < -10f || x > 320f) return false;
            if (uiHeight > 0 && (y < uiHeight * 0.45f || y > uiHeight * 0.85f)) return false;

            // In the captured tree, the button has exactly one text child. Keep this loose enough
            // for UI changes, but strict enough to avoid clicking offer grid slots.
            return childCount <= 3;
        }

        private static dynamic PreferClickableParent(dynamic textNode)
        {
            if (textNode == null) return null;

            dynamic parent = null;
            try { parent = textNode.Parent; } catch { parent = null; }

            if (IsVisibleElement(parent))
                return parent;

            return textNode;
        }

        private Vector2 CenterOfElementOnScreen(dynamic element)
        {
            var center = CenterOfElementClient(element);
            if (!IsReasonablePoint(center))
                return Vector2.Zero;

            // Element positions from the dev tree are client/window coordinates, like inventory item rects.
            // WinAPI mouse input expects screen coordinates.
            var withWindowOffset = center + WindowOffset;
            return IsReasonablePoint(withWindowOffset) ? withWindowOffset : center;
        }

        private static Vector2 CenterOfElementClient(dynamic element)
        {
            if (element == null) return Vector2.Zero;

            try
            {
                var rect = element.GetClientRect();
                var center = rect.Center;
                var result = new Vector2(Convert.ToSingle(center.X), Convert.ToSingle(center.Y));
                if (IsReasonablePoint(result)) return result;
            }
            catch { }

            try
            {
                var center = element.Center;
                var result = new Vector2(Convert.ToSingle(center.X), Convert.ToSingle(center.Y));
                if (IsReasonablePoint(result)) return result;
            }
            catch { }

            try
            {
                var x = Convert.ToSingle(element.X);
                var y = Convert.ToSingle(element.Y);
                var width = Convert.ToSingle(element.Width);
                var height = Convert.ToSingle(element.Height);
                var result = new Vector2(x + width / 2f, y + height / 2f);
                if (IsReasonablePoint(result)) return result;
            }
            catch { }

            return Vector2.Zero;
        }

        private static dynamic FindNodeByPathFromRoot(dynamic root, string expectedPath, int maxNodes, int maxDepth)
        {
            if (root == null || string.IsNullOrWhiteSpace(expectedPath)) return null;
            var state = new PathScanState { ExpectedPath = expectedPath, MaxNodes = maxNodes, MaxDepth = maxDepth };
            ScanForPathFromRoot(root, 0, state);
            return state.Result;
        }

        private static void ScanForPathFromRoot(dynamic node, int depth, PathScanState state)
        {
            if (node == null || state.Result != null || state.NodesVisited >= state.MaxNodes || depth > state.MaxDepth)
                return;

            state.NodesVisited++;

            var path = PathFromRootOf(node);
            if (string.Equals(path, state.ExpectedPath, StringComparison.Ordinal))
            {
                state.Result = node;
                return;
            }

            dynamic children = null;
            try { children = node.Children; } catch { children = null; }
            if (children == null) return;

            var count = CountOf(children);
            if (count > 0)
            {
                for (var i = 0; i < count; i++)
                {
                    dynamic child = null;
                    try { child = children[i]; } catch { child = null; }
                    if (child == null) continue;
                    ScanForPathFromRoot(child, depth + 1, state);
                    if (state.Result != null || state.NodesVisited >= state.MaxNodes)
                        return;
                }

                return;
            }

            // If Count is unavailable for this Children wrapper, probe a safe index range.
            for (var i = 0; i < 256; i++)
            {
                dynamic child = null;
                try { child = children[i]; } catch { break; }
                if (child == null) continue;
                ScanForPathFromRoot(child, depth + 1, state);
                if (state.Result != null || state.NodesVisited >= state.MaxNodes)
                    return;
            }
        }

        private static string PathFromRootOf(dynamic node)
        {
            try
            {
                string path = node.PathFromRoot;
                return path ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        private static dynamic GetChildPath(dynamic node, int[] path)
        {
            if (node == null || path == null) return null;
            dynamic current = node;
            for (var i = 0; i < path.Length; i++)
            {
                current = GetChild(current, path[i]);
                if (current == null) return null;
            }
            return current;
        }

        private static dynamic GetChild(dynamic node, int index)
        {
            if (node == null || index < 0) return null;

            try
            {
                dynamic children = node.Children;
                if (children == null) return null;

                // Try the indexer first. In a few ExileCore2 UI wrappers the indexer works even when
                // Count resolves to 0 through dynamic binding after the window is recreated.
                try { return children[index]; } catch { }

                var count = CountOf(children);
                if (index >= count) return null;
                try { return children[index]; } catch { return null; }
            }
            catch { return null; }
        }

        private static int CountOf(dynamic collection)
        {
            if (collection == null) return 0;
            try { return (int)collection.Count; } catch { }
            try { return (int)collection.Length; } catch { }
            try
            {
                if (collection is System.Collections.ICollection c)
                    return c.Count;
            }
            catch { }

            // Safe probing fallback for indexable wrappers without Count/Length.
            var count = 0;
            for (var i = 0; i < 512; i++)
            {
                try
                {
                    var ignored = collection[i];
                    count++;
                }
                catch
                {
                    break;
                }
            }

            return count;
        }

        private static int ChildCountOf(dynamic node)
        {
            try { return CountOf(node.Children); } catch { return 0; }
        }

        private static float GetUiHeight(dynamic ui)
        {
            return FloatPropertyOf(ui, "Height", 0);
        }

        private static float GetUiWidth(dynamic ui)
        {
            return FloatPropertyOf(ui, "Width", 0);
        }

        private static float FloatPropertyOf(dynamic node, string name, float fallback)
        {
            if (node == null || string.IsNullOrWhiteSpace(name)) return fallback;

            try
            {
                switch (name)
                {
                    case "X": return Convert.ToSingle(node.X);
                    case "Y": return Convert.ToSingle(node.Y);
                    case "Width": return Convert.ToSingle(node.Width);
                    case "Height": return Convert.ToSingle(node.Height);
                }
            }
            catch { }

            try
            {
                var prop = node.GetType().GetProperty(name);
                if (prop == null) return fallback;
                var value = prop.GetValue(node, null);
                if (value == null) return fallback;
                return Convert.ToSingle(value);
            }
            catch { return fallback; }
        }

        private static string TextOf(dynamic node)
        {
            try
            {
                string textNoTags = null;
                try { textNoTags = (string)node.TextNoTags; } catch { }
                if (!string.IsNullOrWhiteSpace(textNoTags)) return textNoTags;

                string text = null;
                try { text = (string)node.Text; } catch { }
                return text;
            }
            catch { return null; }
        }

        private static long TotalMessageCountOf(dynamic chatBox)
        {
            try { return Convert.ToInt64(chatBox.TotalMessageCount); } catch { return -1; }
        }

        private static int IndexInParentOf(dynamic node, int fallback)
        {
            try { return (int)node.IndexInParent; } catch { return fallback; }
        }

        private static string NormalizeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var normalized = text.Replace('\0', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            while (normalized.Contains("  "))
                normalized = normalized.Replace("  ", " ");
            return normalized;
        }

        private static string StripSimpleTags(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var chars = new List<char>(text.Length);
            var insideTag = false;
            foreach (var ch in text)
            {
                if (ch == '<')
                {
                    insideTag = true;
                    continue;
                }

                if (ch == '>' && insideTag)
                {
                    insideTag = false;
                    continue;
                }

                if (!insideTag)
                    chars.Add(ch);
            }

            return new string(chars.ToArray());
        }

        private static bool IsReasonablePoint(Vector2 point)
        {
            return !(float.IsNaN(point.X) || float.IsNaN(point.Y) || float.IsInfinity(point.X) || float.IsInfinity(point.Y)) &&
                   point.X >= 5 && point.Y >= 5 && point.X < 10000 && point.Y < 10000;
        }

        private enum DumpState
        {
            Idle,
            WaitForTradeWindow,
            PrepareInventorySnapshot,
            DumpingItems,
            WaitBeforeAccept,
            Accepting,
            WaitingForFinish
        }

        private enum ClickPhase
        {
            None,
            AfterCtrlDown,
            AfterMouseMove,
            AfterMouseDown,
            AfterMouseUp,
            BetweenItems
        }

        private sealed class AcceptScanState
        {
            public dynamic AcceptNode;
            public int NodesVisited;
            public int MaxNodes;
            public int MaxDepth;
            public float UiHeight;
        }

        private sealed class PathScanState
        {
            public string ExpectedPath;
            public dynamic Result;
            public int NodesVisited;
            public int MaxNodes;
            public int MaxDepth;
        }

        private readonly struct ChatEntry
        {
            public ChatEntry(int index, long totalMessageCount, string text)
            {
                Text = text ?? string.Empty;
                Key = totalMessageCount.ToString() + ":" + index.ToString() + ":" + Text;
            }

            public string Text { get; }
            public string Key { get; }
        }
    }
}
