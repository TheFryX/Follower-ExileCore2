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
}