using RectangleF = ExileCore2.Shared.RectangleF;
using FollowerInternals;
using ExileCore2.Shared.Nodes;
using ExileCore2.Shared.Interfaces;
using System.Numerics;
﻿using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Drawing;
using System.Windows.Forms;
using System.Threading;


namespace Follower;
public class Follower : BaseSettingsPlugin<FollowerSettings>
{
    private const int SPRINT_HOLD_TO_START_MS = 1900;
    private const int SPRINT_RELEASE_STABLE_MS = 250;
    private AutoParty _autoParty;
    private PartyTeleport _partyTeleport;
private Random random = new Random();
    private Camera Camera => GameController.IngameState.Camera;
    private Dictionary<uint, Entity> _areaTransitions = new Dictionary<uint, Entity>();

    private Vector3 _lastTargetPosition;
    private Vector3 _lastPlayerPosition;
    private Entity _followTarget;

    
    private bool IsInHideout()
    {
        var area = GameController.Area.CurrentArea;
        return area.IsHideout || area.Name.Contains("Hideout", StringComparison.OrdinalIgnoreCase);
    }


    private bool _hasUsedWP = true;


    private List<TaskNode> _tasks = new List<TaskNode>();
    private DateTime _nextBotAction = DateTime.Now;

    private DateTime _nextSprintAt = DateTime.MinValue;

    private enum SprintFsm { Idle, Holding }

    private float _distEwma = 0f;
    private DateTime _releaseGateStart = DateTime.MinValue;
    
    private SprintFsm _sprintFsmState = SprintFsm.Idle;
    private DateTime _sprintHoldUntil = DateTime.MinValue;
    private DateTime _nextSprintAllowed = DateTime.MinValue;
    private bool _sprintKeyDown = false;
    
    private bool _sprintHeld = false;
    private DateTime _lastSprintToggle = DateTime.MinValue;

    

    private int _numRows, _numCols;
    private byte[,] _tiles;

    public override bool Initialise()
    {
        Name = "Follower";
        Input.RegisterKey(Settings.MovementKey.Value);

        Input.RegisterKey(Settings.ToggleFollower.Value);
        Settings.ToggleFollower.OnValueChanged += () => { Input.RegisterKey(Settings.ToggleFollower.Value); };

        _autoParty = new AutoParty(this);
        _partyTeleport = new PartyTeleport(this);
        return base.Initialise();
    }


    /// <summary>
    /// Clears all pathfinding values. Used on area transitions primarily.
    /// </summary>
    private void ResetPathing()
    {
        _tasks = new List<TaskNode>();
        _followTarget = null;
        _lastTargetPosition = Vector3.Zero;
        _lastPlayerPosition = Vector3.Zero;
        _areaTransitions = new Dictionary<uint, Entity>();
        _hasUsedWP = true;
    }

    public override void AreaChange(AreaInstance area)
    {
        ResetPathing();

        //Load initial transitions!

        foreach (var transition in GameController.EntityListWrapper.Entities.Where(I => I.Type == ExileCore2.Shared.Enums.EntityType.AreaTransition ||
         I.Type == ExileCore2.Shared.Enums.EntityType.Portal ||
         I.Type == ExileCore2.Shared.Enums.EntityType.TownPortal).ToList())
        {
            if (!_areaTransitions.ContainsKey(transition.Id))
                _areaTransitions.Add(transition.Id, transition);
        }


        var terrain = GameController.IngameState.Data.Terrain;
        var terrainBytes = GameController.Memory.ReadBytes(terrain.LayerMelee.First, terrain.LayerMelee.Size);
        _numCols = (int)(terrain.NumCols - 1) * 23;
        _numRows = (int)(terrain.NumRows - 1) * 23;
        if ((_numCols & 1) > 0)
            _numCols++;

        _tiles = new byte[_numCols, _numRows];
        int dataIndex = 0;
        for (int y = 0; y < _numRows; y++)
        {
            for (int x = 0; x < _numCols; x += 2)
            {
                var b = terrainBytes[dataIndex + (x >> 1)];
                _tiles[x, y] = (byte)((b & 0xf) > 0 ? 1 : 255);
                _tiles[x + 1, y] = (byte)((b >> 4) > 0 ? 1 : 255);
            }
            dataIndex += terrain.BytesPerRow;
        }

        terrainBytes = GameController.Memory.ReadBytes(terrain.LayerRanged.First, terrain.LayerRanged.Size);
        _numCols = (int)(terrain.NumCols - 1) * 23;
        _numRows = (int)(terrain.NumRows - 1) * 23;
        if ((_numCols & 1) > 0)
            _numCols++;
        dataIndex = 0;
        for (int y = 0; y < _numRows; y++)
        {
            for (int x = 0; x < _numCols; x += 2)
            {
                var b = terrainBytes[dataIndex + (x >> 1)];

                var current = _tiles[x, y];
                if (current == 255)
                    _tiles[x, y] = (byte)((b & 0xf) > 3 ? 2 : 255);
                current = _tiles[x + 1, y];
                if (current == 255)
                    _tiles[x + 1, y] = (byte)((b >> 4) > 3 ? 2 : 255);
            }
            dataIndex += terrain.BytesPerRow;
        }


        GeneratePNG();
    }

    public void GeneratePNG()
    {
        using (var img = new Bitmap(_numCols, _numRows))
        {
            for (int x = 0; x < _numCols; x++)
                for (int y = 0; y < _numRows; y++)
                {
                    try
                    {
                        var color = System.Drawing.Color.Black;
                        switch (_tiles[x, y])
                        {
                            case 1:
                                color = System.Drawing.Color.White;
                                break;
                            case 2:
                                color = System.Drawing.Color.Gray;
                                break;
                            case 255:
                                color = System.Drawing.Color.Black;
                                break;
                        }
                        img.SetPixel(x, y, color);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                }
            img.Save("output.png");
        }
    }


    private void MouseoverItem(Entity item)
    {
        var uiLoot = GameController.IngameState.IngameUi.ItemsOnGroundLabels.FirstOrDefault(I => I.IsVisible && I.ItemOnGround.Id == item.Id);
        if (uiLoot != null)
        {
            var clickPos = uiLoot.Label.GetClientRect().Center;
            Mouse.SetCursorPos(new Vector2(
                clickPos.X + random.Next(-15, 15),
                clickPos.Y + random.Next(-10, 10)));
            Thread.Sleep(30 + random.Next(Settings.BotInputFrequency));
        }
    }

    public override void Render()
    {
//Dont run logic if we're dead!
if (!GameController.Player.IsAlive)
    return;

// Hotkey toggle (pause the whole plugin logic)
if (Settings.ToggleFollower.PressedOnce())
{
    Settings.IsFollowEnabled.SetValueNoEvent(!Settings.IsFollowEnabled.Value);
    _tasks = new List<TaskNode>();
}

if (!Settings.IsFollowEnabled.Value)
    return;

// Optional safety: when inventory is open, do not move/click/teleport.
if (IsInventoryOpen())
    return;

_autoParty?.Tick();
_partyTeleport?.Tick();

//Cache the current follow target (if present)
        _followTarget = GetFollowingTarget();
        if (_followTarget != null)
        {
            var distanceFromFollower = Vector3.Distance(GameController.Player.Pos, _followTarget.Pos);
            //We are NOT within clear path distance range of leader. Logic can continue
            if (distanceFromFollower >= Settings.ClearPathDistance.Value)
            {
                //Leader moved VERY far in one frame. Check for transition to use to follow them.
                var distanceMoved = Vector3.Distance(_lastTargetPosition, _followTarget.Pos);
                if (_lastTargetPosition != Vector3.Zero && distanceMoved > Settings.ClearPathDistance.Value)
                {
                    var transition = _areaTransitions.Values.OrderBy(I => Vector3.Distance(_lastTargetPosition, I.Pos)).FirstOrDefault();
                    var dist = Vector3.Distance(_lastTargetPosition, transition.Pos);
                    if (Vector3.Distance(_lastTargetPosition, transition.Pos) < Settings.ClearPathDistance.Value)
                        _tasks.Add(new TaskNode(transition.Pos, 200, TaskNode.TaskNodeType.Transition));
                }
                //We have no path, set us to go to leader pos.
                else if (_tasks.Count == 0)
                    _tasks.Add(new TaskNode(_followTarget.Pos, Settings.PathfindingNodeDistance));
                //We have a path. Check if the last task is far enough away from current one to add a new task node.
                else
                {
                    var distanceFromLastTask = Vector3.Distance(_tasks.Last().WorldPosition, _followTarget.Pos);
                    if (distanceFromLastTask >= Settings.PathfindingNodeDistance)
                        _tasks.Add(new TaskNode(_followTarget.Pos, Settings.PathfindingNodeDistance));
                }
            }
            else
            {
                //Clear all tasks except for looting/claim portal (as those only get done when we're within range of leader. 
                if (_tasks.Count > 0)
                {
                    for (var i = _tasks.Count - 1; i >= 0; i--)
                        if (_tasks[i].Type == TaskNode.TaskNodeType.Movement || _tasks[i].Type == TaskNode.TaskNodeType.Transition)
                            _tasks.RemoveAt(i);
                }
                else if (Settings.IsCloseFollowEnabled.Value)
                {
                    //Close follow logic. We have no current tasks. Check if we should move towards leader
                    if (distanceFromFollower >= Settings.PathfindingNodeDistance.Value)
                        _tasks.Add(new TaskNode(_followTarget.Pos, Settings.PathfindingNodeDistance));
                }

                //Check if we should add quest loot logic. We're close to leader already
                var questLoot = GetLootableQuestItem();
                if (questLoot != null &&
                    Vector3.Distance(GameController.Player.Pos, questLoot.Pos) < Settings.ClearPathDistance.Value &&
                    _tasks.FirstOrDefault(I => I.Type == TaskNode.TaskNodeType.Loot) == null)
                    _tasks.Add(new TaskNode(questLoot.Pos, Settings.ClearPathDistance, TaskNode.TaskNodeType.Loot));

                else if (!_hasUsedWP)
                {
                    //Check if there's a waypoint nearby
                    var waypoint = GameController.EntityListWrapper.Entities.SingleOrDefault(I => I.Type == ExileCore2.Shared.Enums.EntityType.Waypoint &&
                        Vector3.Distance(GameController.Player.Pos, I.Pos) < Settings.ClearPathDistance);

                    if (waypoint != null)
                    {
                        _hasUsedWP = true;
                        _tasks.Add(new TaskNode(waypoint.Pos, Settings.ClearPathDistance, TaskNode.TaskNodeType.ClaimWaypoint));
                    }

                }

            }
            _lastTargetPosition = _followTarget.Pos;
        }
        //Leader is null but we have tracked them this map.
        //Try using transition to follow them to their map
        else if (_tasks.Count == 0 &&
            _lastTargetPosition != Vector3.Zero &&
            IsInHideout())
        {

            var transOptions = _areaTransitions.Values.
                Where(I => Vector3.Distance(_lastTargetPosition, I.Pos) < Settings.ClearPathDistance).
                OrderBy(I => Vector3.Distance(_lastTargetPosition, I.Pos)).ToArray();
            if (transOptions.Length > 0)
                _tasks.Add(new TaskNode(transOptions[random.Next(transOptions.Length)].Pos, Settings.PathfindingNodeDistance.Value, TaskNode.TaskNodeType.Transition));
        }


        //We have our tasks, now we need to perform in game logic with them.
        if (DateTime.Now > _nextBotAction && _tasks.Count > 0)
        {
            var currentTask = _tasks.First();
            var taskDistance = Vector3.Distance(GameController.Player.Pos, currentTask.WorldPosition);
            var playerDistanceMoved = Vector3.Distance(GameController.Player.Pos, _lastPlayerPosition);

            //We are using a same map transition and have moved significnatly since last tick. Mark the transition task as done.
            if (currentTask.Type == TaskNode.TaskNodeType.Transition &&
                playerDistanceMoved >= Settings.ClearPathDistance.Value)
            {
                _tasks.RemoveAt(0);
                if (_tasks.Count > 0)
                    currentTask = _tasks.First();
                else
                {
                    _lastPlayerPosition = GameController.Player.Pos;
                    return;
}
            }

            switch (currentTask.Type)
            {
                case TaskNode.TaskNodeType.Movement:
                    _nextBotAction = DateTime.Now.AddMilliseconds(Settings.BotInputFrequency + random.Next(Settings.BotInputFrequency));

                                                            UpdateDodgeSprintFSM();
UpdateDodgeSprintFSM();
if (false && CheckDashTerrain(currentTask.WorldPosition))
                        return;
if (!Mouse.IsGuardLocked) Mouse.SetCursorPosHuman2(WorldToValidScreenPosition(currentTask.WorldPosition));
                    Thread.Sleep(random.Next(25) + 30);
                    Input.KeyDown(Settings.MovementKey);
                    Thread.Sleep(random.Next(25) + 30);
                    Input.KeyUp(Settings.MovementKey);

                    //Within bounding range. Task is complete
                    //Note: Was getting stuck on close objects... testing hacky fix.
                    if (taskDistance <= Settings.PathfindingNodeDistance.Value * 1.5)
                        _tasks.RemoveAt(0);
                    break;
                case TaskNode.TaskNodeType.Loot:
                    {
                        _nextBotAction = DateTime.Now.AddMilliseconds(Settings.BotInputFrequency + random.Next(Settings.BotInputFrequency));
                        currentTask.AttemptCount++;
                        var questLoot = GetLootableQuestItem();
                        if (questLoot == null
                            || currentTask.AttemptCount > 2
                            || Vector3.Distance(GameController.Player.Pos, questLoot.Pos) >= Settings.ClearPathDistance.Value)
                            _tasks.RemoveAt(0);

                        Input.KeyUp(Settings.MovementKey);
                        Thread.Sleep(Settings.BotInputFrequency);
                        //Pause for long enough for movement to hopefully be finished.
                        var targetInfo = questLoot.GetComponent<Targetable>();
                        if (!targetInfo.isTargeted)
                            MouseoverItem(questLoot);
                        if (targetInfo.isTargeted)
                        {
                            Thread.Sleep(25);
                            Mouse.LeftMouseDown();
                            Thread.Sleep(25 + random.Next(Settings.BotInputFrequency));
                            Mouse.LeftMouseUp();
                            _nextBotAction = DateTime.Now.AddSeconds(1);
                        }

                        break;
                    }
                case TaskNode.TaskNodeType.Transition:
                    {
                        _nextBotAction = DateTime.Now.AddMilliseconds(Settings.BotInputFrequency * 2 + random.Next(Settings.BotInputFrequency));
                        var screenPos = WorldToValidScreenPosition(currentTask.WorldPosition);
                        if (taskDistance <= Settings.ClearPathDistance.Value)
                        {
                            //Click the transition
                            Input.KeyUp(Settings.MovementKey);
                            if (!Mouse.IsGuardLocked) Mouse.SetCursorPosAndLeftClickHuman(screenPos, 100);
                            _nextBotAction = DateTime.Now.AddSeconds(1);
                        }
                        else
                        {
                            //Walk towards the transition
                            if (!Mouse.IsGuardLocked) Mouse.SetCursorPosHuman2(screenPos);
                            Thread.Sleep(random.Next(25) + 30);
                            Input.KeyDown(Settings.MovementKey);
                            Thread.Sleep(random.Next(25) + 30);
                            Input.KeyUp(Settings.MovementKey);
                        }
                        currentTask.AttemptCount++;
                        if (currentTask.AttemptCount > 3)
                            _tasks.RemoveAt(0);
                        break;
                    }

                case TaskNode.TaskNodeType.ClaimWaypoint:
                    {
                        if (Vector3.Distance(GameController.Player.Pos, currentTask.WorldPosition) > 150)
                        {
                            var screenPos = WorldToValidScreenPosition(currentTask.WorldPosition);
                            Input.KeyUp(Settings.MovementKey);
                            Thread.Sleep(Settings.BotInputFrequency);
                            if (!Mouse.IsGuardLocked) Mouse.SetCursorPosAndLeftClickHuman(screenPos, 100);
                            _nextBotAction = DateTime.Now.AddSeconds(1);
                        }
                        currentTask.AttemptCount++;
                        if (currentTask.AttemptCount > 3)
                            _tasks.RemoveAt(0);
                        break;
                    }
            }
        }
        _lastPlayerPosition = GameController.Player.Pos;
        return;

        DrawPath();
    }

    
    
    // NUDO TODO: Replace this with real terrain lookup from ExileCore2 (e.g., GameController.IngameState.Data.Terrain)
    private static byte GetTile(int x, int y)
    {
        // 255 = blocked, 0 = walkable; default safe is walkable.
        return 0;
    }
    
    
    // Attempts to trigger a Sprint if leader is too far while close-follow is enabled.
    
    
    private void UpdateDodgeSprintFSM()
    {
        try
        {
            if (!(Settings.IsSprintEnabled?.Value ?? false))
            {
                if (_sprintKeyDown) { Input.KeyUp(Settings.DodgeSprintKey); _sprintKeyDown = false; }
                _sprintFsmState = SprintFsm.Idle;
                return;
            }

            var leader = GetFollowingTarget();
            if (leader == null)
            {
                if (_sprintKeyDown) { Input.KeyUp(Settings.DodgeSprintKey); _sprintKeyDown = false; }
                _sprintFsmState = SprintFsm.Idle;
                return;
            }

            var now = DateTime.Now;
            var dist = Vector3.Distance(GameController.Player.Pos, leader.Pos);
            // distance smoothing to reduce oscillations
            if (_distEwma <= 0f) _distEwma = dist;
            _distEwma = (float)(0.8 * _distEwma + 0.2 * dist);

            var startFar = Settings.SprintDistanceThreshold.Value;
            var releaseNear = Math.Max(10, (int)(startFar * 0.7));
            bool followGate = (Settings.IsCloseFollowEnabled?.Value ?? false) || (true /* always when far */);

            switch (_sprintFsmState)
            {
                case SprintFsm.Idle:
                    if (followGate && _distEwma >= startFar && now >= _nextSprintAllowed)
                    {
                        Input.KeyDown(Settings.DodgeSprintKey);
                        _sprintKeyDown = true;
                        _sprintHoldUntil = now.AddMilliseconds(SPRINT_HOLD_TO_START_MS);
                        _releaseGateStart = DateTime.MinValue;
                        _sprintFsmState = SprintFsm.Holding;
                    }
                    break;

                case SprintFsm.Holding:
                    // must hold at least until _sprintHoldUntil
                    if (now < _sprintHoldUntil) break;

                    // after min-hold, continue holding until distance is stably below release threshold
                    if (_distEwma <= releaseNear)
                    {
                        if (_releaseGateStart == DateTime.MinValue)
                            _releaseGateStart = now;
                        var stableMs = (now - _releaseGateStart).TotalMilliseconds;
                        if (stableMs >= SPRINT_RELEASE_STABLE_MS)
                        {
                            Input.KeyUp(Settings.DodgeSprintKey);
                            _sprintKeyDown = false;
                            _sprintFsmState = SprintFsm.Idle;
                            _nextSprintAllowed = now.AddMilliseconds(Settings.SprintRetriggerCooldownMs.Value);
                        }
                    }
                    else
                    {
                        _releaseGateStart = DateTime.MinValue; // not yet stable
                    }
                    break;
            }
        }
        catch
        {
            // never throw from render/update
        }
    }
    
    private bool CheckDashTerrain(Vector3 targetWorld)
    {
        // Purpose: detect if dashing (using dash key) helps to traverse short wall segments by moving cursor to dash destination and firing dash key.
        try
        {
            var playerGrid = GameController.Player.GridPos;
            var targetPosition = FollowerInternals.MathEx.WorldToGrid(targetWorld);
            var distance = Vector2.Distance(playerGrid, targetPosition);
            var dir = targetPosition - playerGrid;
            if (dir == Vector2.Zero) return false;
            dir = Vector2.Normalize(dir);

            var distanceBeforeWall = 0;
            var distanceInWall = 0;
            var shouldDash = false;

            for (var i = 0; i < 500; i++)
            {
                var v2Point = playerGrid + i * dir;
                var pt = new System.Drawing.Point((int)Math.Round(v2Point.X), (int)Math.Round(v2Point.Y));
                // Read tile info via internal helper if available
                byte tile = 0;
                try
                {
                    tile = GetTile(pt.X, pt.Y);
                }
                catch
                {
                    // If terrain reader unavailable, abort dash
                    return false;
                }

                // Interpret tile: 255 == invalid/blocked, other values walkable
                if (tile == 255)
                {
                    // inside wall
                    distanceInWall++;
                    if (distanceInWall > 20)
                    {
                        shouldDash = false;
                        break;
                    }
                }
                else
                {
                    // walkable
                    if (distanceInWall > 0)
                    {
                        // we have emerged from wall after some in-wall length -> candidate for dash
                        shouldDash = true;
                        break;
                    }
                    distanceBeforeWall++;
                    if (distanceBeforeWall > 10)
                    {
                        break;
                    }
                }
            }

            if (distanceBeforeWall > 10 || distanceInWall < 5)
                shouldDash = false;

            if (shouldDash)
            {
                _nextBotAction = DateTime.Now.AddMilliseconds(500 + new Random().Next(Settings.BotInputFrequency));
                // Move cursor to target world position and perform dash key press
                var worldPos = FollowerInternals.MathEx.GridToWorld(targetPosition, targetWorld.Z);
                Mouse.SetCursorPos(WorldToValidScreenPosition(worldPos));
                Thread.Sleep(25);
                Input.KeyDown(Settings.DodgeSprintKey);
                Thread.Sleep(25);
                Input.KeyUp(Settings.DodgeSprintKey);
                return true;
            }
        }
        catch { }
        return false;
    }
    

    private Entity GetFollowingTarget()
    {
        var leaderName = Settings.LeaderName.Value.ToLower();
        try
        {
            return GameController.Entities
                .Where(x => x.Type == ExileCore2.Shared.Enums.EntityType.Player)
                .FirstOrDefault(x => x.GetComponent<Player>().PlayerName.ToLower() == leaderName);
        }
        // Sometimes we can get "Collection was modified; enumeration operation may not execute" exception
        catch
        {
            return null;
}
    }

    private Entity GetLootableQuestItem()
    {
        try
        {
            return GameController.EntityListWrapper.Entities
                .Where(e => e.Type == ExileCore2.Shared.Enums.EntityType.WorldItem)
                .Where(e => e.IsTargetable)
                .Where(e => e.GetComponent<WorldItem>() != null)
                .FirstOrDefault(e =>
                {
                    Entity itemEntity = e.GetComponent<WorldItem>().ItemEntity;
                    return GameController.Files.BaseItemTypes.Translate(itemEntity.Path).ClassName ==
                            "QuestItem";
                });
        }
        catch
        {
            return null;
}
    }
    public override void EntityAdded(Entity entity)
    {
        if (!string.IsNullOrEmpty(entity.RenderName))
            switch (entity.Type)
            {
                //TODO: Handle doors and similar obstructions to movement/pathfinding

                //TODO: Handle waypoint (initial claim as well as using to teleport somewhere)

                //Handle clickable teleporters
                case ExileCore2.Shared.Enums.EntityType.AreaTransition:
                case ExileCore2.Shared.Enums.EntityType.Portal:
                case ExileCore2.Shared.Enums.EntityType.TownPortal:
                    if (!_areaTransitions.ContainsKey(entity.Id))
                        _areaTransitions.Add(entity.Id, entity);
                    break;
            }
        base.EntityAdded(entity);
    }

    public override void EntityRemoved(Entity entity)
    {
        switch (entity.Type)
        {
            //TODO: Handle doors and similar obstructions to movement/pathfinding

            //TODO: Handle waypoint (initial claim as well as using to teleport somewhere)

            //Handle clickable teleporters
            case ExileCore2.Shared.Enums.EntityType.AreaTransition:
            case ExileCore2.Shared.Enums.EntityType.Portal:
            case ExileCore2.Shared.Enums.EntityType.TownPortal:
                if (_areaTransitions.ContainsKey(entity.Id))
                    _areaTransitions.Remove(entity.Id);
                break;
        }
        base.EntityRemoved(entity);
    }


    


    private Vector2 WorldToValidScreenPosition(Vector3 worldPos)
    {
        var windowRect = GameController.Window.GetWindowRectangle();
        var screenPos = Camera.WorldToScreen(worldPos);
        var result = screenPos + windowRect.Location;

        var edgeBounds = 50;
        if (!windowRect.Intersects(new RectangleF(result.X, result.Y, edgeBounds, edgeBounds)))
        {
            //Adjust for offscreen entity. Need to clamp the screen position using the game window info. 
            if (result.X < windowRect.TopLeft.X) result.X = windowRect.TopLeft.X + edgeBounds;
            if (result.Y < windowRect.TopLeft.Y) result.Y = windowRect.TopLeft.Y + edgeBounds;
            if (result.X > windowRect.BottomRight.X) result.X = windowRect.BottomRight.X - edgeBounds;
            if (result.Y > windowRect.BottomRight.Y) result.Y = windowRect.BottomRight.Y - edgeBounds;
        }
        return result;
    }

    private void DrawPath()
    {

        if (_tasks != null && _tasks.Count > 1)
            for (var i = 1; i < _tasks.Count; i++)
            {
                var start = WorldToValidScreenPosition(_tasks[i - 1].WorldPosition);
                var end = WorldToValidScreenPosition(_tasks[i].WorldPosition);
                Graphics.DrawLine(start, end, 2, Color.Pink);
            }
        var dist = _tasks.Count > 0 ? Vector3.Distance(GameController.Player.Pos, _tasks.First().WorldPosition) : 0;
        var targetDist = _lastTargetPosition == null ? "NA" : Vector3.Distance(GameController.Player.Pos, _lastTargetPosition).ToString();
        Graphics.DrawText($"Follow Enabled: {Settings.IsFollowEnabled.Value}", new Vector2(500, 120));
        Graphics.DrawText($"Task Count: {_tasks.Count} Next WP Distance: {dist} Target Distance: {targetDist}", new Vector2(500, 140));
        var counter = 0;
        foreach (var transition in _areaTransitions)
        {
            counter++;
            Graphics.DrawText($"{transition.Key} at {transition.Value.Pos.X} {transition.Value.Pos.Y}", new Vector2(100, 120 + counter * 20));
        }
    
    }



private bool IsInventoryOpen()
{
    if (!Settings.PauseWhenInventoryOpen.Value) return false;

    try
    {
        var ingame = GameController.IngameState;
        var ui = ingame?.IngameUi;
        if (ui == null) return false;

        // Prefer the actual "open right panel" inventory, because some root inventory widgets
        // can remain visible even when the panel is closed.
        object? panel =
            TryGetNestedProperty(ui, "OpenRightPanel", "InventoryPanel") ??
            TryGetProperty(ui, "InventoryPanel") ??
            TryGetProperty(ui, "Inventory") ??
            TryGetProperty(ui, "InventoryWindow");

        if (panel == null) return false;

        // If this is an Element-like object, rely on visibility flags + geometry.
        // In PoE2 UI many panels remain IsActive=true even when hidden, so NEVER use IsActive alone.
        var isVisibleLocal = GetBool(panel, "IsVisibleLocal");
        var isVisible = GetBool(panel, "IsVisible");
        var width = GetInt(panel, "Width");
        var height = GetInt(panel, "Height");

        // Some UI elements can report negative coordinates depending on anchoring,
        // so position is not a reliable signal for "open".

        // Inventory open heuristic:
        // - Must be visible (locally or globally)
        // - Must have a reasonable size (closed panels often report 0 or tiny sizes)
        if ((isVisibleLocal || isVisible) && width >= 200 && height >= 200)
            return true;

        // Fallback flags for unusual UI models (still requires visibility).
        return (isVisibleLocal || isVisible) && (GetBool(panel, "IsOpened") || GetBool(panel, "IsOpen"));
    }
    catch
    {
        return false;
    }

    static object? TryGetProperty(object obj, string name)
    {
        var t = obj.GetType();
        var p = t.GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        return p?.GetValue(obj);
    }

    static object? TryGetNestedProperty(object obj, string parentName, string childName)
    {
        var parent = TryGetProperty(obj, parentName);
        return parent != null ? TryGetProperty(parent, childName) : null;
    }

    static bool GetBool(object obj, string prop)
    {
        try
        {
            var t = obj.GetType();
            var p = t.GetProperty(prop, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (p == null) return false;
            return p.GetValue(obj) is bool b && b;
        }
        catch { return false; }
    }

    static int GetInt(object obj, string prop)
    {
        try
        {
            var t = obj.GetType();
            var p = t.GetProperty(prop, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (p == null) return 0;
            var v = p.GetValue(obj);
            return v switch
            {
                int i => i,
                long l => unchecked((int)l),
                float f => (int)f,
                double d => (int)d,
                _ => 0
            };
        }
        catch { return 0; }
    }
}

}