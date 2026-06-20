using System;
using System.Collections.Generic;

namespace Follower
{
    internal sealed class PartyChatCommands
    {
        private const int MaxNewChatLinesPerScan = 3;
        private const int FallbackScanMs = 3000;
        private const int MinPollMs = 1000;
        private const int ArmDelayMs = 2500;

        private readonly Follower _plugin;
        private DateTime _lastScan = DateTime.UtcNow.AddSeconds(-5);
        private DateTime _lastFallbackScan = DateTime.UtcNow.AddSeconds(-5);
        private DateTime _armAt = DateTime.MinValue;
        private bool _initialized;
        private bool _armed;
        private bool _wasEnabled;
        private long _lastSeenTotal = -1;
        private string _lastSeenLatestKey = string.Empty;

        public PartyChatCommands(Follower plugin)
        {
            _plugin = plugin;
        }

        private dynamic UI => _plugin.GameController.IngameState?.IngameUi;

        public void Tick()
        {
            try
            {
                var s = _plugin.Settings;
                if (s == null || !(s.Enable?.Value ?? false))
                {
                    ResetScannerState();
                    return;
                }

                if (!(s.PartyChatLeaderCommands.Enabled?.Value ?? false))
                {
                    ResetScannerState();
                    return;
                }

                if (!_wasEnabled)
                {
                    ResetScannerState();
                    _wasEnabled = true;
                }

                // This feature runs from Render(), so it must only touch cheap counters during normal frames.
                // It ignores the existing chat backlog while arming. This prevents a stale "-p" message from
                // disabling follow immediately after plugin reload or after the chat UI finishes populating.
                var pollMs = Math.Max(MinPollMs, s.PartyChatLeaderCommands.PollMs.Value);
                var now = DateTime.UtcNow;
                if ((now - _lastScan).TotalMilliseconds < pollMs)
                    return;
                _lastScan = now;

                var leaderName = (s.General.LeaderName?.Value ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(leaderName))
                    return;

                dynamic chatBox = null;
                try { chatBox = UI?.ChatPanel?.ChatBox; } catch { chatBox = null; }
                if (chatBox == null)
                    return;

                var currentTotal = TotalMessageCountOf(chatBox);

                if (!_initialized)
                {
                    _lastSeenTotal = currentTotal;
                    _lastSeenLatestKey = string.Empty;
                    _armAt = now.AddMilliseconds(ArmDelayMs);
                    _initialized = true;
                    _armed = false;
                    return;
                }

                if (!_armed)
                {
                    // While arming, keep advancing the baseline but do not process any chat lines.
                    // This absorbs already-visible history and late chat-buffer population.
                    if (currentTotal >= 0)
                        _lastSeenTotal = Math.Max(_lastSeenTotal, currentTotal);

                    if (now < _armAt)
                        return;

                    _lastSeenTotal = currentTotal;
                    _lastSeenLatestKey = string.Empty;
                    if (currentTotal < 0)
                    {
                        var latestBaseline = ReadLatestChatEntry(chatBox, currentTotal);
                        _lastSeenLatestKey = latestBaseline.Key;
                    }
                    _armed = true;
                    return;
                }

                if (currentTotal >= 0 && _lastSeenTotal >= 0)
                {
                    if (currentTotal < _lastSeenTotal)
                    {
                        // Chat buffer was reset/rebuilt. Re-arm and ignore the fresh backlog.
                        _lastSeenTotal = currentTotal;
                        _armAt = now.AddMilliseconds(ArmDelayMs);
                        _armed = false;
                        return;
                    }

                    if (currentTotal == _lastSeenTotal)
                        return;

                    var delta = currentTotal - _lastSeenTotal;
                    var linesToRead = (int)Math.Min(delta, MaxNewChatLinesPerScan);
                    ProcessLatestChatLines(chatBox, linesToRead, currentTotal, leaderName);
                    _lastSeenTotal = currentTotal;
                    return;
                }

                // Compatibility fallback for API builds where TotalMessageCount is unavailable.
                // It remains intentionally slow and only reads one latest visible line after arming.
                if ((now - _lastFallbackScan).TotalMilliseconds < FallbackScanMs)
                    return;
                _lastFallbackScan = now;

                var latest = ReadLatestChatEntry(chatBox, currentTotal);
                if (latest.Text.Length == 0 || string.Equals(latest.Key, _lastSeenLatestKey, StringComparison.Ordinal))
                    return;

                _lastSeenLatestKey = latest.Key;
                ProcessEntry(latest.Text, leaderName);
            }
            catch (Exception ex)
            {
                try { _plugin.LogMessage("PartyChatCommands error: " + ex.Message, 5); } catch { }
            }
        }

        private void ResetScannerState()
        {
            _initialized = false;
            _armed = false;
            _wasEnabled = false;
            _lastSeenTotal = -1;
            _lastSeenLatestKey = string.Empty;
            _armAt = DateTime.MinValue;
            _lastScan = DateTime.UtcNow.AddSeconds(-5);
            _lastFallbackScan = DateTime.UtcNow.AddSeconds(-5);
        }

        private void ProcessLatestChatLines(dynamic chatBox, int linesToRead, long totalMessageCount, string leaderName)
        {
            if (linesToRead <= 0)
                return;

            dynamic messageElements = null;
            try { messageElements = chatBox.MessageElements; } catch { messageElements = null; }

            var count = CountOf(messageElements);
            if (count <= 0)
                return;

            // Read only the newest visible elements. Do not walk the whole chat history.
            var start = Math.Max(0, count - Math.Min(linesToRead, MaxNewChatLinesPerScan));
            for (var i = start; i < count; i++)
            {
                dynamic node = null;
                try { node = messageElements[i]; } catch { node = null; }
                if (node == null)
                    continue;

                var text = NormalizeText(TextOf(node));
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var key = totalMessageCount.ToString() + ":" + IndexInParentOf(node, i).ToString() + ":" + text;
                if (string.Equals(key, _lastSeenLatestKey, StringComparison.Ordinal))
                    continue;

                _lastSeenLatestKey = key;
                ProcessEntry(text, leaderName);
            }
        }

        private void ProcessEntry(string text, string leaderName)
        {
            var s = _plugin.Settings;

            if (TryParseLeaderPluginPauseCommand(
                    text,
                    leaderName,
                    s.PartyChatLeaderCommands.PausePluginCommand.Value,
                    s.PartyChatLeaderCommands.ResumePluginCommand.Value,
                    out var pluginEnabled,
                    out var pluginCommandText))
            {
                _plugin.SetWholePluginPausedFromPartyChat(!pluginEnabled, leaderName, pluginCommandText);
                return;
            }

            // When the leader paused the whole plugin with -pp, keep only this chat-command scanner alive.
            // Ignore -d/-p/-s until -ss arrives, so no old command is queued and executed after resume.
            if (_plugin.IsWholePluginPausedByPartyChat)
                return;

            if ((s.TpTrade.AutoDumpInventoryToTrade?.Value ?? false) &&
                TryParseLeaderSimpleCommand(
                    text,
                    leaderName,
                    s.PartyChatLeaderCommands.DumpInventoryCommand.Value,
                    out var dumpCommandText))
            {
                _plugin.StartTradeInventoryDumpFromPartyChat(leaderName, dumpCommandText);
                return;
            }

            if (TryParseLeaderCommand(
                    text,
                    leaderName,
                    s.PartyChatLeaderCommands.StopCommand.Value,
                    s.PartyChatLeaderCommands.StartCommand.Value,
                    out var followEnabled,
                    out var commandText))
            {
                _plugin.SetFollowEnabledFromPartyChat(followEnabled, leaderName, commandText);
            }
        }

        private ChatEntry ReadLatestChatEntry(dynamic chatBox, long totalMessageCount)
        {
            dynamic messageElements = null;
            try { messageElements = chatBox.MessageElements; } catch { messageElements = null; }

            var count = CountOf(messageElements);
            if (count <= 0)
                return ChatEntry.Empty;

            dynamic node = null;
            try { node = messageElements[count - 1]; } catch { node = null; }
            if (node == null)
                return ChatEntry.Empty;

            var text = NormalizeText(TextOf(node));
            if (string.IsNullOrWhiteSpace(text))
                return ChatEntry.Empty;

            return new ChatEntry(IndexInParentOf(node, count - 1), totalMessageCount, text);
        }

        private static bool TryParseLeaderPluginPauseCommand(
            string rawText,
            string leaderName,
            string pauseCommand,
            string resumeCommand,
            out bool pluginEnabled,
            out string commandText)
        {
            pluginEnabled = false;
            commandText = string.Empty;

            pauseCommand = string.IsNullOrWhiteSpace(pauseCommand) ? "-pp" : pauseCommand.Trim();
            resumeCommand = string.IsNullOrWhiteSpace(resumeCommand) ? "-ss" : resumeCommand.Trim();

            if (!TryExtractPartyLeaderMessage(rawText, leaderName, out var message))
                return false;

            if (string.Equals(message, pauseCommand, StringComparison.OrdinalIgnoreCase))
            {
                pluginEnabled = false;
                commandText = pauseCommand;
                return true;
            }

            if (string.Equals(message, resumeCommand, StringComparison.OrdinalIgnoreCase))
            {
                pluginEnabled = true;
                commandText = resumeCommand;
                return true;
            }

            return false;
        }

        private static bool TryParseLeaderCommand(
            string rawText,
            string leaderName,
            string stopCommand,
            string startCommand,
            out bool followEnabled,
            out string commandText)
        {
            followEnabled = false;
            commandText = string.Empty;

            stopCommand = string.IsNullOrWhiteSpace(stopCommand) ? "-p" : stopCommand.Trim();
            startCommand = string.IsNullOrWhiteSpace(startCommand) ? "-s" : startCommand.Trim();

            if (!TryExtractPartyLeaderMessage(rawText, leaderName, out var message))
                return false;

            if (string.Equals(message, stopCommand, StringComparison.OrdinalIgnoreCase))
            {
                followEnabled = false;
                commandText = stopCommand;
                return true;
            }

            if (string.Equals(message, startCommand, StringComparison.OrdinalIgnoreCase))
            {
                followEnabled = true;
                commandText = startCommand;
                return true;
            }

            return false;
        }

        private static bool TryParseLeaderSimpleCommand(
            string rawText,
            string leaderName,
            string command,
            out string commandText)
        {
            commandText = string.Empty;
            command = string.IsNullOrWhiteSpace(command) ? "-d" : command.Trim();

            if (!TryExtractPartyLeaderMessage(rawText, leaderName, out var message))
                return false;

            if (!string.Equals(message, command, StringComparison.OrdinalIgnoreCase))
                return false;

            commandText = command;
            return true;
        }

        private static bool TryExtractPartyLeaderMessage(
            string rawText,
            string leaderName,
            out string message)
        {
            message = string.Empty;

            if (string.IsNullOrWhiteSpace(rawText) || string.IsNullOrWhiteSpace(leaderName))
                return false;

            var text = NormalizeText(StripSimpleTags(rawText));
            if (string.IsNullOrWhiteSpace(text))
                return false;

            // Some chat layouts prepend timestamps before the channel marker. Keep the party marker and following text.
            var percentIndex = text.IndexOf('%');
            var firstColon = text.IndexOf(':');
            if (percentIndex > 0 && (firstColon < 0 || percentIndex < firstColon))
                text = text.Substring(percentIndex).Trim();

            var isParty = false;
            if (text.StartsWith("%", StringComparison.Ordinal))
            {
                isParty = true;
                text = text.Substring(1).TrimStart();
            }
            else if (text.StartsWith("[Party]", StringComparison.OrdinalIgnoreCase))
            {
                isParty = true;
                text = text.Substring("[Party]".Length).TrimStart();
            }
            else if (text.StartsWith("Party", StringComparison.OrdinalIgnoreCase))
            {
                var markerEnd = text.IndexOf(']');
                if (markerEnd >= 0)
                {
                    isParty = true;
                    text = text.Substring(markerEnd + 1).TrimStart();
                }
            }

            // Leader commands are intentionally limited to party chat.
            if (!isParty)
                return false;

            var colon = text.IndexOf(':');
            if (colon <= 0)
                return false;

            var sender = text.Substring(0, colon).Trim();
            message = text.Substring(colon + 1).Trim();

            return string.Equals(sender, leaderName, StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrWhiteSpace(message);
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

        private static int CountOf(dynamic collection)
        {
            try { return (int)collection.Count; } catch { return 0; }
        }

        private static int IndexInParentOf(dynamic node, int fallback)
        {
            try { return (int)node.IndexInParent; } catch { return fallback; }
        }

        private static long TotalMessageCountOf(dynamic chatBox)
        {
            try { return Convert.ToInt64(chatBox.TotalMessageCount); } catch { return -1; }
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

        private readonly struct ChatEntry
        {
            public static ChatEntry Empty => new ChatEntry(-1, -1, string.Empty);

            public ChatEntry(int index, long totalMessageCount, string text)
            {
                Index = index;
                TotalMessageCount = totalMessageCount;
                Text = text ?? string.Empty;
                Key = totalMessageCount.ToString() + ":" + index.ToString() + ":" + Text;
            }

            public int Index { get; }
            public long TotalMessageCount { get; }
            public string Text { get; }
            public string Key { get; }
        }
    }
}
