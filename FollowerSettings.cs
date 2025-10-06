using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using System.Windows.Forms;

namespace Follower;

public class FollowerSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);
    public ToggleNode IsFollowEnabled { get; set; } = new ToggleNode(false);
public HotkeyNode ToggleFollower { get; set; } = Keys.PageUp;
public RangeNode<int> PathfindingNodeDistance { get; set; } = new RangeNode<int>(200, 10, 1000);
public RangeNode<int> BotInputFrequency { get; set; } = new RangeNode<int>(50, 10, 250);
public RangeNode<int> ClearPathDistance { get; set; } = new RangeNode<int>(500, 100, 5000);
public RangeNode<int> RandomClickOffset { get; set; } = new RangeNode<int>(10, 1, 100);
public TextNode LeaderName { get; set; } = new TextNode("");
public HotkeyNode MovementKey { get; set; } = Keys.T;
public ToggleNode IsCloseFollowEnabled { get; set; } = new ToggleNode(false);

[Menu("Auto Party")]
public ToggleNode AutoAcceptParty { get; set; } = new ToggleNode(true);
[Menu("Accept invites from (comma-separated)")]
public TextNode AcceptFrom { get; set; } = new TextNode("");
public RangeNode<int> AutoPartyPollMs { get; set; } = new RangeNode<int>(500, 200, 2000);


// --- Party Teleport settings ---
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


// --- Party Teleport: extra controls ---

[Menu("Teleport confirm timeout (ms)")]
public RangeNode<int> TpConfirmTimeoutMs { get; set; } = new RangeNode<int>(2500, 500, 8000);

[Menu("Teleport confirm retries")]
public RangeNode<int> TpConfirmRetries { get; set; } = new RangeNode<int>(2, 0, 5);
}
