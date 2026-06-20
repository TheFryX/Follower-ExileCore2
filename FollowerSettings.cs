using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using System;
using System.IO;
using System.Windows.Forms;

namespace Follower;

public class FollowerSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    [Menu("General")]
    [Submenu(CollapsedByDefault = false)]
    public GeneralSettings General { get; set; } = new GeneralSettings();

    [Menu("TP / Trade")]
    [Submenu(CollapsedByDefault = false)]
    public TpTradeSettings TpTrade { get; set; } = new TpTradeSettings();


    [Menu("Party chat leader commands")]
    [Submenu(CollapsedByDefault = true)]
    public PartyChatLeaderCommandSettings PartyChatLeaderCommands { get; set; } = new PartyChatLeaderCommandSettings();

    [Menu("Transition")]
    [Submenu(CollapsedByDefault = true)]
    public TransitionSettings Transition { get; set; } = new TransitionSettings();

    [Menu("Debug")]
    [Submenu(CollapsedByDefault = true)]
    public DebugSettings Debug { get; set; } = new DebugSettings();
}

[Submenu(CollapsedByDefault = false)]
public class GeneralSettings
{
    [Menu("Follow enabled")]
    public ToggleNode IsFollowEnabled { get; set; } = new ToggleNode(false);

    [Menu("Toggle follower hotkey")]
    public HotkeyNode ToggleFollower { get; set; } = Keys.PageUp;


    [Menu("Leader name")]
    public TextNode LeaderName { get; set; } = new TextNode("");

    [Menu("Movement key")]
    public HotkeyNode MovementKey { get; set; } = Keys.T;

    [Menu("Pause when inventory is open")]
    public ToggleNode PauseWhenInventoryOpen { get; set; } = new ToggleNode(true);

    [Menu("Close follow")]
    public ToggleNode IsCloseFollowEnabled { get; set; } = new ToggleNode(false);

    [Menu("Pathfinding node distance")]
    public RangeNode<int> PathfindingNodeDistance { get; set; } = new RangeNode<int>(200, 10, 1000);

    [Menu("Bot input frequency (ms) - safety clamped internally to min 90 ms")]
    public RangeNode<int> BotInputFrequency { get; set; } = new RangeNode<int>(90, 80, 250);

    [Menu("Clear path distance")]
    public RangeNode<int> ClearPathDistance { get; set; } = new RangeNode<int>(500, 100, 5000);

    [Menu("Random click offset")]
    public RangeNode<int> RandomClickOffset { get; set; } = new RangeNode<int>(10, 1, 100);

    [Menu("Allow Dodge/Sprint")]
    public ToggleNode IsSprintEnabled { get; set; } = new ToggleNode(true);

    [Menu("Sprint key")]
    public HotkeyNode DodgeSprintKey { get; set; } = Keys.S;

    [Menu("Sprint distance to leader (world units)")]
    public RangeNode<int> SprintDistanceThreshold { get; set; } = new RangeNode<int>(500, 50, 2000);

    [Menu("Sprint re-trigger (ms)")]
    public RangeNode<int> SprintRetriggerCooldownMs { get; set; } = new RangeNode<int>(5000, 500, 15000);
}

[Submenu(CollapsedByDefault = true)]
public class PartyChatLeaderCommandSettings
{
    [Menu("Enabled")]
    public ToggleNode Enabled { get; set; } = new ToggleNode(true);

    [Menu("Stop command")]
    public TextNode StopCommand { get; set; } = new TextNode("-p");

    [Menu("Start command")]
    public TextNode StartCommand { get; set; } = new TextNode("-s");

    [Menu("Pause whole plugin + ESC command")]
    public TextNode PausePluginCommand { get; set; } = new TextNode("-pp");

    [Menu("Resume whole plugin + ESC command")]
    public TextNode ResumePluginCommand { get; set; } = new TextNode("-ss");

    [Menu("Dump inventory to trade command")]
    public TextNode DumpInventoryCommand { get; set; } = new TextNode("-d");

    [Menu("Command poll interval (ms)")]
    public RangeNode<int> PollMs { get; set; } = new RangeNode<int>(1000, 1000, 5000);
}

[Submenu(CollapsedByDefault = true)]
public class TransitionSettings
{
    [Menu("Auto-click boss Arena transition")]
    public ToggleNode AutoClickArenaTransition { get; set; } = new ToggleNode(true);

    [Menu("Arena transition label text")]
    public TextNode ArenaTransitionLabelText { get; set; } = new TextNode("Arena");

    [Menu("Auto-click Abyss sub-area transition")]
    public ToggleNode AutoClickAbyssSubAreaTransition { get; set; } = new ToggleNode(true);

    [Menu("Transition scan interval (ms)")]
    public RangeNode<int> ArenaTransitionScanMs { get; set; } = new RangeNode<int>(1000, 750, 5000);

    [Menu("Transition max click retries")]
    public RangeNode<int> ArenaTransitionMaxRetries { get; set; } = new RangeNode<int>(3, 1, 10);

    [Menu("Transition retry cooldown (ms)")]
    public RangeNode<int> ArenaTransitionRetryCooldownMs { get; set; } = new RangeNode<int>(5000, 500, 30000);
}

[Submenu(CollapsedByDefault = false)]
public class TpTradeSettings
{

    [Menu("Auto accept party invites")]
    public ToggleNode AutoAcceptParty { get; set; } = new ToggleNode(true);

    [Menu("Auto accept trade invites")]
    public ToggleNode AutoAcceptTrade { get; set; } = new ToggleNode(false);

    [Menu("Auto dump inventory to trade on leader command")]
    public ToggleNode AutoDumpInventoryToTrade { get; set; } = new ToggleNode(true);

    [Menu("Trade dump item delay (ms)")]
    public RangeNode<int> TradeDumpItemDelayMs { get; set; } = new RangeNode<int>(45, 20, 250);

    [Menu("Trade accept delay after dump (ms) - internally clamped to min 1600")]
    public RangeNode<int> TradeDumpAcceptDelayMs { get; set; } = new RangeNode<int>(1600, 0, 6000);

    [Menu("Trade window wait timeout (ms)")]
    public RangeNode<int> TradeDumpWindowWaitTimeoutMs { get; set; } = new RangeNode<int>(10000, 1000, 60000);

    [Menu("Trade finish timeout (ms)")]
    public RangeNode<int> TradeDumpFinishTimeoutMs { get; set; } = new RangeNode<int>(60000, 5000, 180000);

    [Menu("Auto party poll interval (ms)")]
    public RangeNode<int> AutoPartyPollMs { get; set; } = new RangeNode<int>(500, 200, 2000);

    [Menu("Teleport to leader (party TP)")]
    public ToggleNode TeleportToLeader { get; set; } = new ToggleNode(true);

    [Menu("Auto-confirm 'Teleport?' dialog (click OK)")]
    public ToggleNode AutoConfirmTeleportDialog { get; set; } = new ToggleNode(true);

    [Menu("TP check interval (ms)")]
    public RangeNode<int> TpPollMs { get; set; } = new RangeNode<int>(800, 200, 3000);

    [Menu("TP click retries")]
    public RangeNode<int> TpMaxRetries { get; set; } = new RangeNode<int>(3, 0, 10);

    [Menu("Stop after hideout TP")]
    public ToggleNode StopAfterHideoutTeleport { get; set; } = new ToggleNode(true);

    [Menu("Teleport from own Hideout to leader Hideout")]
    public ToggleNode TeleportFromHideout { get; set; } = new ToggleNode(true);

    [Menu("Teleport confirm timeout (ms)")]
    public RangeNode<int> TpConfirmTimeoutMs { get; set; } = new RangeNode<int>(2500, 500, 8000);

    [Menu("Teleport confirm retries")]
    public RangeNode<int> TpConfirmRetries { get; set; } = new RangeNode<int>(2, 0, 5);
}

[Submenu(CollapsedByDefault = true)]
public class DebugSettings
{
    [Menu("Debug generate terrain PNG on area change")]
    public ToggleNode DebugGeneratePngOnAreaChange { get; set; } = new ToggleNode(false);

    [Menu("Spike Profiler - write option cost to temp txt")]
    public ToggleNode EnableSpikeProfiler { get; set; } = new ToggleNode(false);

    [Menu("Spike Profiler spike threshold (ms)")]
    public RangeNode<int> SpikeProfilerThresholdMs { get; set; } = new RangeNode<int>(8, 1, 250);

    [Menu("Spike Profiler flush interval (ms)")]
    public RangeNode<int> SpikeProfilerFlushIntervalMs { get; set; } = new RangeNode<int>(1000, 250, 10000);

    [Menu("Spike Profiler log every sample")]
    public ToggleNode SpikeProfilerLogEverySample { get; set; } = new ToggleNode(false);

    [Menu("Spike Profiler directory")]
    public TextNode SpikeProfilerDirectory { get; set; } = new TextNode(Path.Combine(Path.GetTempPath(), "Follower-SpikeProfiler"));

    [Menu("Debug TradeDump Accept click to txt")]
    public ToggleNode DebugTradeDumpAcceptToTxt { get; set; } = new ToggleNode(false);

    [Menu("Debug AutoParty scanner to txt")]
    public ToggleNode DebugAutoPartyScannerToTxt { get; set; } = new ToggleNode(false);

    [Menu("Debug AutoParty reactions only to txt")]
    public ToggleNode DebugAutoPartyReactionsToTxt { get; set; } = new ToggleNode(false);

    [Menu("AutoParty debug directory")]
    public TextNode AutoPartyDebugDirectory { get; set; } = new TextNode(Path.Combine(Path.GetTempPath(), "FollowerDebug"));

    [Menu("AutoParty hover/window context dump to txt")]
    public ToggleNode DebugAutoPartyHoverContextToTxt { get; set; } = new ToggleNode(false);

    [Menu("AutoParty hover/window context dump hotkey")]
    public HotkeyNode AutoPartyHoverContextDumpHotkey { get; set; } = Keys.F10;

    [Menu("AutoParty hover context max depth")]
    public RangeNode<int> AutoPartyHoverContextMaxDepth { get; set; } = new RangeNode<int>(8, 3, 20);

    [Menu("AutoParty hover context child limit")]
    public RangeNode<int> AutoPartyHoverContextChildLimit { get; set; } = new RangeNode<int>(300, 20, 1000);

    [Menu("AutoParty debug max nodes per tick")]
    public RangeNode<int> AutoPartyDebugMaxNodesPerTick { get; set; } = new RangeNode<int>(2000, 100, 20000);

    [Menu("AutoParty reaction context child limit")]
    public RangeNode<int> AutoPartyReactionContextChildLimit { get; set; } = new RangeNode<int>(80, 10, 300);
}
