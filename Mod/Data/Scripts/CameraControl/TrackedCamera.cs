using System;
using System.Text;
using System.Collections.Generic;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using Sandbox.Common;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Common.Components;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Utils;
using VRageMath;

public class TrackingShot
{
    public Timeline position_timeline;
    public Timeline lookat_timeline;
    public Timeline upvec_timeline; // updated in parallel with lookat_timeline
                                    // TODO fov
    public ITimeline lookat_by_position_timeline;
    public TrackingShot(Timeline position_timeline, Timeline lookat_timeline, Timeline upvec_timeline)
    {
        this.position_timeline = position_timeline;
        this.lookat_timeline = lookat_timeline;
        this.upvec_timeline = upvec_timeline;
        this.lookat_by_position_timeline = new RelativeTimeline(this.position_timeline, this.lookat_timeline);
    }
    public TrackingShot() : this(new Timeline(), new Timeline(), new Timeline())
    {
    }

    public bool IsKeyframe(int frame)
    {
        return position_timeline.IsKeyframe(frame)
            || lookat_timeline.IsKeyframe(frame);
    }

    public void DeleteKeyframe(int frame)
    {
        position_timeline.DeleteKeyframe(frame);
        lookat_timeline.DeleteKeyframe(frame);
        upvec_timeline.DeleteKeyframe(frame);
    }

    public int KeyframeNextSmallerThan(int frame)
    {
        int pk = position_timeline.KeyframeNextSmallerThan(frame);
        int lk = lookat_timeline.KeyframeNextSmallerThan(frame);
        int uk = upvec_timeline.KeyframeNextSmallerThan(frame);

        if (pk == frame && lk == frame && uk == frame) return frame;
        if (pk == frame && lk == frame) return uk;
        if (pk == frame && uk == frame) return lk;
        if (lk == frame && uk == frame) return pk;
        if (pk == frame) return Math.Max(lk, uk);
        if (lk == frame) return Math.Max(pk, uk);
        if (uk == frame) return Math.Max(pk, lk);
        return Math.Max(pk, Math.Max(lk, uk));
    }

    public int KeyframeNextLargerThan(int frame)
    {
        int pk = position_timeline.KeyframeNextLargerThan(frame);
        int lk = lookat_timeline.KeyframeNextLargerThan(frame);
        int uk = upvec_timeline.KeyframeNextLargerThan(frame);

        if (pk == frame && lk == frame && uk == frame) return frame;
        if (pk == frame && lk == frame) return uk;
        if (pk == frame && uk == frame) return lk;
        if (lk == frame && uk == frame) return pk;
        if (pk == frame) return Math.Min(lk, uk);
        if (lk == frame) return Math.Min(pk, uk);
        if (uk == frame) return Math.Min(pk, lk);
        return Math.Min(pk, Math.Min(lk, uk));
    }

    public bool IsLastFrame(int frame)
    {
        return frame == Math.Max(position_timeline.LastFrame, Math.Max(lookat_timeline.LastFrame, upvec_timeline.LastFrame));
    }
}

[MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
public class FrameCaller : MySessionComponentBase
{
    static List<MyGameLogicComponent> blocks = new List<MyGameLogicComponent>();
    static bool iterating = false;
    static List<MyGameLogicComponent> to_remove = new List<MyGameLogicComponent>();

    public override void UpdateBeforeSimulation()
    {
        iterating = true;
        foreach (var block in blocks)
        {
            block.UpdateBeforeSimulation();
        }
        foreach (var block in to_remove) blocks.Remove(block);
        to_remove.Clear();
        iterating = false;
    }

    public static void AddComponent(MyGameLogicComponent comp)
    {
        blocks.Add(comp);
    }

    public static void RmComponent(MyGameLogicComponent comp)
    {
        if (iterating)
        {
            to_remove.Add(comp);
        }
        else {
            blocks.Remove(comp);
        }
    }
}

// the kind of camera that is focused on a position in a grid
public class TrackingCamera : MyGameLogicComponent
{
    Sandbox.Common.ObjectBuilders.MyObjectBuilder_EntityBase objectBuilder = null;
    bool block_initialized = false;

    public MatrixD OriginalLocalMatrix = MatrixD.Identity;
    public int frame = 0;
    public TransitionMode keyframe_mode = TransitionMode.Spline;
    public bool view_locked = false; // whether we should apply our view
    TrackingShot playing_shot = null; // when playing, this will be non-null

    public void InitLate()
    {
        block_initialized = true;
        frame = SettingsStore.Get(Entity, "frame", 0);
        OriginalLocalMatrix = Entity.LocalMatrix;

        if (!CameraUI.initialized) CameraUI.InitLate();
    }

    public IMyEntity view_locked_to // entity whose frame lookat is locked to
    {
        get
        {
            IMyEntity res = null;
            var id = SettingsStore.Get(Entity, "view_locked_to", 0L);

            if (id != 0) MyAPIGateway.Entities.TryGetEntityById(id, out res);
            return res;
        }
        set
        {
            if (value == null) SettingsStore.Set(Entity, "view_locked_to", 0);
            else SettingsStore.Set(Entity, "view_locked_to", value.EntityId);
        }
    }
    public IMyEntity pos_locked_to // entity whose frame pos is locked to
    {
        get
        {
            IMyEntity res = null;
            var id = SettingsStore.Get(Entity, "pos_locked_to", 0L);

            if (id != 0) MyAPIGateway.Entities.TryGetEntityById(id, out res);
            return res;
        }
        set
        {
            if (value == null) SettingsStore.Set(Entity, "pos_locked_to", 0);
            else SettingsStore.Set(Entity, "pos_locked_to", value.EntityId);
        }
    }

    public override void Init(Sandbox.Common.ObjectBuilders.MyObjectBuilder_EntityBase objectBuilder)
    {
        Entity.NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME;
        this.objectBuilder = objectBuilder;
        FrameCaller.AddComponent(this);
    }

    public void Save(TrackingShot shot)
    {
        var builder = new StringBuilder();
        builder.Append("Position: ");
        builder.Append(shot.position_timeline.Serialize());
        builder.Append(", LookAt: ");
        builder.Append(shot.lookat_timeline.Serialize());
        builder.Append(", UpVec: ");
        builder.Append(shot.upvec_timeline.Serialize());
        SettingsStore.Set(Entity, "shot", builder.ToString());
    }

    public TrackingShot Load()
    {
        string shotstr = SettingsStore.Get<string>(Entity, "shot", null);

        if (shotstr == null)
        {
            return new TrackingShot();
        }

        var parser = new RDParser(shotstr);

        parser.Expect("Position");
        parser.Expect(":");
        var position_timeline = Timeline.Deserialize(parser);

        parser.Expect(",");
        parser.Expect("LookAt");
        parser.Expect(":");
        var lookat_timeline = Timeline.Deserialize(parser);

        parser.Expect(",");
        parser.Expect("UpVec");
        parser.Expect(":");
        var upvec_timeline = Timeline.Deserialize(parser);

        parser.End();

        return new TrackingShot(position_timeline, lookat_timeline, upvec_timeline);
    }

    public override MyObjectBuilder_EntityBase GetObjectBuilder(bool copy = false)
    {
        return objectBuilder;
    }

    public void UpdateView(TrackingShot shot = null)
    {
        if (shot == null)
        {
            shot = Load();
        }

        MatrixD proper_worldmat = OriginalLocalMatrix * Entity.Parent.WorldMatrix;
        Vector3D position = Vector3D.Zero;
        Vector3D lookat = Vector3D.Zero;
        Vector3D upvec = Vector3D.Zero;

        if (!shot.position_timeline.Empty)
        {
            position = shot.position_timeline.Evaluate(frame, false);
        }
        else
        {
            position = proper_worldmat.Translation;
        }

        if (!shot.lookat_timeline.Empty)
        {
            if (!shot.position_timeline.Empty)
            {
                lookat = shot.lookat_by_position_timeline.Evaluate(frame, false);
            }
            else {
                lookat = shot.lookat_timeline.Evaluate(frame, false);
            }
        }
        else
        {
            lookat = proper_worldmat.Translation + proper_worldmat.Forward * SettingsStore.Get(Entity, "focus_distance", 1.0f);
        }

        if (!shot.upvec_timeline.Empty)
        {
            upvec = shot.upvec_timeline.Evaluate(frame, true);
        }
        else
        {
            upvec = proper_worldmat.Up;
        }

        // WorldMatrix: Block space -> World space
        // CreateWorld: Target space -> World space
        // needed: Block space -> Target space
        // CreateWorld * WorldMatrix^-1: Target space -> Block space

        // local mat: Block space -> Grid space
        // wanted local mat: Target space -> Grid space

        var mat = MatrixD.CreateWorld(position, lookat - position, upvec) * MatrixD.Invert(proper_worldmat);
        // MyLog.Default.WriteLine(String.Format("lmat = {0}", mat));

        Entity.SetLocalMatrix(mat * OriginalLocalMatrix);
    }

    public override void UpdateBeforeSimulation()
    {
        if (Entity == null)
        {
            FrameCaller.RmComponent(this);
            return;
        }
        if (!block_initialized) InitLate();

        if (view_locked)
        {
            UpdateView(playing_shot /* may be null! */);
        }
        else
        {
            Entity.SetLocalMatrix(OriginalLocalMatrix);
        }

        if (playing_shot == null) return;

        frame++;

        if (playing_shot.IsLastFrame(frame))
        {
            Pause();
            return;
        }
    }

    public void Pause()
    {
        playing_shot = null;
        view_locked = true; // Stop -> Pause: lock
        // sync frame now that it's no longer changing 60 times per second
        SettingsStore.Set(Entity, "frame", frame);
    }

    public void Stop()
    {
        Pause();
        view_locked = false;
    }

    public void Play()
    {
        view_locked = true; // force view to camera
        if (playing_shot == null)
        {
            playing_shot = Load();
        }
    }

    public PlaybackMode PlaybackState
    {
        get
        {
            if (!view_locked) return PlaybackMode.Stopped;
            if (playing_shot != null) return PlaybackMode.Playing;
            return PlaybackMode.Paused;
        }
    }

    public void SetPosFrame(TrackingShot shot = null)
    {
        var worldmat = OriginalLocalMatrix * Entity.Parent.WorldMatrix;
        var pos = worldmat.Translation;
        var info = new TrackingInfo();
        info.Transition = keyframe_mode;

        if (pos_locked_to != null)
        {
            info.Mode = TrackMode.Locked;
            info.Entity = pos_locked_to;
            info.Value3D = Vector3D.Transform(pos, MatrixD.Invert(pos_locked_to.WorldMatrix));
        }
        else
        {
            info.Mode = TrackMode.Unlocked;
            info.Value3D = pos;
        }

        MyLog.Default.WriteLine(String.Format("set pos frame <{0},{1},{2}>", info.Value3D.X, info.Value3D.Y, info.Value3D.Z));
        if (shot == null)
        {
            shot = Load();
            shot.position_timeline.AddKeyframe(frame, info);
            Save(shot);
        }
        else {
            shot.position_timeline.AddKeyframe(frame, info);
        }
    }

    public void SetLookatFrame(TrackingShot shot = null)
    {
        var worldmat = OriginalLocalMatrix * Entity.Parent.WorldMatrix;
        var target = worldmat.Translation + worldmat.Forward * SettingsStore.Get(Entity, "focus_distance", 1.0f);
        var info = new TrackingInfo();
        info.Transition = keyframe_mode;

        if (view_locked_to != null)
        {
            info.Mode = TrackMode.Locked;
            info.Entity = view_locked_to;
            info.Value3D = Vector3D.Transform(target, MatrixD.Invert(view_locked_to.WorldMatrix));
        }
        else
        {
            info.Mode = TrackMode.Unlocked;
            info.Value3D = target;
        }

        if (shot == null)
        {
            shot = Load();
            shot.lookat_timeline.AddKeyframe(frame, info);
            Save(shot);
        }
        else
        {
            shot.lookat_timeline.AddKeyframe(frame, info);
        }
    }

    public void SetUpFrame(TrackingShot shot = null)
    {
        var worldmat = OriginalLocalMatrix * Entity.Parent.WorldMatrix;
        var upvec = worldmat.Up; // TODO
        var info = new TrackingInfo();
        info.Transition = keyframe_mode;

        if (pos_locked_to != null)
        {
            info.Mode = TrackMode.Locked;
            info.Entity = pos_locked_to;
            info.Value3D = Vector3D.TransformNormal(upvec, pos_locked_to.WorldMatrixNormalizedInv);
        }
        else
        {
            info.Mode = TrackMode.Unlocked;
            info.Value3D = upvec;
        }

        if (shot == null)
        {
            shot = Load();
            shot.upvec_timeline.AddKeyframe(frame, info);
            Save(shot);
        }
        else {
            shot.upvec_timeline.AddKeyframe(frame, info);
        }
    }

    public void SetFrame()
    {
        var shot = Load(); // TODO
        SetPosFrame(shot);
        SetLookatFrame(shot);
        SetUpFrame(shot);
        Save(shot);
    }

    public void SetViewFrame()
    {
        var shot = Load();
        SetLookatFrame(shot);
        Save(shot);
    }

    public void DeleteKeyframe()
    {
        var shot = Load();
        shot.DeleteKeyframe(frame);
        Save(shot);
    }
}

[MyEntityComponentDescriptor(typeof(MyObjectBuilder_CameraBlock), "TrackingCameraBlock_Small")]
public class TrackingCameraSmall : TrackingCamera
{
}

[MyEntityComponentDescriptor(typeof(MyObjectBuilder_CameraBlock), "TrackingCameraBlock_Large")]
public class TrackingCameraLarge : TrackingCamera
{
}

static class CameraUI
{
    public static bool initialized = false;
    public static IMyTerminalAction nextFrameAction, prevFrameAction;
    public static IMyTerminalAction nextFrame10Action, prevFrame10Action;
    public static IMyTerminalAction nextKeyFrameAction, prevKeyFrameAction;
    public static IMyTerminalAction playAction, pauseAction, playPauseAction, stopAction;
    public static IMyTerminalAction setFrameAction, setPosFrameAction, setViewFrameAction, delKeyframeAction, toggleKeyframeModeAction;
    public static IMyTerminalAction lockViewAction, lockPosAction, setFocusAction;
    public static IMyTerminalControlSlider rangeSlider;

    public static bool BlockIsMyCamera(IMyTerminalBlock block)
    {
        return block.BlockDefinition.SubtypeId == "TrackingCameraBlock_Small"
            || block.BlockDefinition.SubtypeId == "TrackingCameraBlock_Large";
    }

    public static void GetCameraActions(IMyTerminalBlock block, List<IMyTerminalAction> actions)
    {
        if (!BlockIsMyCamera(block))
        {
            actions.Remove(nextFrameAction);
            actions.Remove(prevFrameAction);
            actions.Remove(nextFrame10Action);
            actions.Remove(prevFrame10Action);
            actions.Remove(nextKeyFrameAction);
            actions.Remove(prevKeyFrameAction);
            actions.Remove(playAction);
            actions.Remove(pauseAction);
            actions.Remove(stopAction);
            actions.Remove(setFrameAction);
            actions.Remove(setPosFrameAction);
            actions.Remove(setViewFrameAction);
            actions.Remove(delKeyframeAction);
            actions.Remove(toggleKeyframeModeAction);
            actions.Remove(lockViewAction);
            actions.Remove(lockPosAction);
        }
    }

    // SettingsStore.frame is only a backup, to avoid flooding the network when we're playing
    public static void ActionNextFrame(IMyTerminalBlock block)
    {
        var cam = block.GameLogic.GetAs<TrackingCamera>();
        cam.frame = cam.frame + 1;
        cam.UpdateView();
        SettingsStore.Set(block, "frame", cam.frame);
    }

    public static void ActionPrevFrame(IMyTerminalBlock block)
    {
        var cam = block.GameLogic.GetAs<TrackingCamera>();
        cam.frame = cam.frame - 1;
        cam.UpdateView();
        SettingsStore.Set(block, "frame", cam.frame);
    }

    public static void ActionNextFrame10(IMyTerminalBlock block)
    {
        var cam = block.GameLogic.GetAs<TrackingCamera>();
        cam.frame = cam.frame + 10;
        cam.UpdateView();
        SettingsStore.Set(block, "frame", cam.frame);
    }

    public static void ActionPrevFrame10(IMyTerminalBlock block)
    {
        var cam = block.GameLogic.GetAs<TrackingCamera>();
        cam.frame = cam.frame - 10;
        cam.UpdateView();
        SettingsStore.Set(block, "frame", cam.frame);
    }

    public static void ActionNextKeyFrame(IMyTerminalBlock block)
    {
        var cam = block.GameLogic.GetAs<TrackingCamera>();
        var shot = cam.Load(); // TODO track "active shot" even when not playing
        cam.frame = shot.KeyframeNextLargerThan(cam.frame);
        cam.UpdateView();
        SettingsStore.Set(block, "frame", cam.frame);
    }

    public static void ActionPrevKeyFrame(IMyTerminalBlock block)
    {
        var cam = block.GameLogic.GetAs<TrackingCamera>();
        var shot = cam.Load(); // TODO track "active shot" even when not playing
        cam.frame = shot.KeyframeNextSmallerThan(cam.frame);
        cam.UpdateView();
        SettingsStore.Set(block, "frame", cam.frame);
    }

    public static void ActionToggleKeyframeMode(IMyTerminalBlock block)
    {
        var cam = block.GameLogic.GetAs<TrackingCamera>();

        switch (cam.keyframe_mode)
        {
            case TransitionMode.Constant: cam.keyframe_mode = TransitionMode.Linear; break;
            case TransitionMode.Linear: cam.keyframe_mode = TransitionMode.Cosine; break;
            case TransitionMode.Cosine: cam.keyframe_mode = TransitionMode.Spline; break;
            case TransitionMode.Spline: cam.keyframe_mode = TransitionMode.Constant; break;
            default: throw new Exception("unknown transition mode");
        }
    }

    public static IMyEntity GetLockTarget()
    {
        var gamecam = MyAPIGateway.Session.Camera;
        // TODO better?
        var line = gamecam.WorldLineFromScreen(gamecam.ViewportSize / 2.0f);
        IHitInfo hit;
        bool was_hit = MyAPIGateway.Physics.CastRay(line.From, line.To, out hit);

        if (!was_hit)
        {
            MyAPIGateway.Utilities.ShowNotification("Error: cannot lock, not aiming at anything!", 2000, MyFontEnum.Red);
            return null;
        }

        return hit.HitEntity;
    }

    public static void ActionLockViewToTarget(IMyTerminalBlock block)
    {
        var cam = block.GameLogic.GetAs<TrackingCamera>();

        if (cam.view_locked_to != null)
        {
            // unlock view
            cam.view_locked_to = null;
            return;
        }

        cam.view_locked_to = GetLockTarget();
    }

    public static void ActionLockPosToTarget(IMyTerminalBlock block)
    {
        var cam = block.GameLogic.GetAs<TrackingCamera>();

        if (cam.pos_locked_to != null)
        {
            // unlock pos
            cam.pos_locked_to = null;
            return;
        }

        cam.pos_locked_to = GetLockTarget();
    }

    public static void KeyframeModeWriter(IMyTerminalBlock block, StringBuilder builder)
    {
        var cam = block.GameLogic.GetAs<TrackingCamera>();
        string info = "";

        switch (cam.keyframe_mode)
        {
            case TransitionMode.Constant: info = "Cons"; break;
            case TransitionMode.Linear: info = "Linr"; break;
            case TransitionMode.Cosine: info = "Cosn"; break;
            case TransitionMode.Spline: info = "Spln"; break;
            default: throw new Exception("unknown transition mode");
        }

        builder.Clear();
        builder.AppendFormat("[{0}]", info);
    }

    public static Action<IMyTerminalBlock, StringBuilder> FrameNumWriterGen(string info)
    {
        return (block, builder) =>
        {
            var cam = block.GameLogic.GetAs<TrackingCamera>();
            int frame = cam.frame;
            var shot = cam.Load(); // TODO version of Load() that doesn't reload if there's no change?
            var is_pos_keyframe = shot.position_timeline.IsKeyframe(frame);
            var is_view_keyframe = shot.lookat_timeline.IsKeyframe(frame);
            builder.Clear();
            builder.AppendFormat("{0}\n",
                is_pos_keyframe ? (is_view_keyframe ? "PV" : "P_") : (is_view_keyframe ? "_V" : ""));
            builder.AppendFormat("{0}{1}", frame, info);
        };
    }

    public static Action<IMyTerminalBlock, StringBuilder> ConditionToggleWriterGen(Func<TrackingCamera, bool> test, string info)
    {
        return (block, builder) =>
        {
            var cam = block.GameLogic.GetAs<TrackingCamera>();

            builder.Clear();
            if (test(cam)) builder.Append("[###]");
            else builder.Append("[___]");
            builder.Append("\n");
            builder.Append(info);
        };
    }

    public static void ActionPlay(IMyTerminalBlock block)
    {
        var cam = block.GameLogic.GetAs<TrackingCamera>();
        cam.Play();
    }

    public static void ActionPlayPause(IMyTerminalBlock block)
    {
        var cam = block.GameLogic.GetAs<TrackingCamera>();

        if (cam.PlaybackState == PlaybackMode.Playing)
        {
            cam.Pause();
        }
        else
        {
            cam.Play();
        }
    }

    public static void ActionPause(IMyTerminalBlock block)
    {
        var cam = block.GameLogic.GetAs<TrackingCamera>();
        cam.Pause();
    }

    public static void ActionStop(IMyTerminalBlock block)
    {
        var cam = block.GameLogic.GetAs<TrackingCamera>();
        cam.Stop();
    }

    public static void ActionSetFrame(IMyTerminalBlock block)
    {
        var cam = block.GameLogic.GetAs<TrackingCamera>();
        cam.SetFrame();
    }

    public static void ActionSetPosFrame(IMyTerminalBlock block)
    {
        var cam = block.GameLogic.GetAs<TrackingCamera>();
        var shot = cam.Load();
        cam.SetPosFrame(shot);
        cam.SetUpFrame(shot);
        cam.Save(shot);
    }

    public static void ActionSetViewFrame(IMyTerminalBlock block)
    {
        var cam = block.GameLogic.GetAs<TrackingCamera>();
        cam.SetViewFrame();
    }

    public static void ActionDelKeyframe(IMyTerminalBlock block)
    {
        var cam = block.GameLogic.GetAs<TrackingCamera>();
        cam.DeleteKeyframe();
    }

    // set focus setting to view lock entity
    public static void ActionSetFocusToViewLock(IMyTerminalBlock block)
    {
        var cam = block.GameLogic.GetAs<TrackingCamera>();
        var view_locked_to = cam.view_locked_to;

        if (view_locked_to == null)
        {
            MyAPIGateway.Utilities.ShowNotification("Error: cannot set focus to view lock; view not locked!", 2000, MyFontEnum.Red);
            return;
        }

        var proper_worldmat = cam.OriginalLocalMatrix * cam.Entity.Parent.WorldMatrix;
        var dist = (proper_worldmat.Translation - view_locked_to.WorldMatrix.Translation).Length();

        SettingsStore.Set(block, "focus_distance", (float)dist);
    }

    public static float LogRound(float f)
    {
        var logbase = Math.Pow(10, Math.Floor(Math.Log10(f)));
        var frac = f / logbase;
        frac = Math.Floor(frac * 10) / 10;
        return (float)(logbase * frac);
    }

    public static void InitLate()
    {
        initialized = true;

        nextFrameAction = MyAPIGateway.TerminalControls.CreateAction<IMyCameraBlock>("Camera_NextFrame");
        nextFrameAction.Name = new StringBuilder("Next Frame");
        nextFrameAction.Action = ActionNextFrame;
        nextFrameAction.Writer = FrameNumWriterGen("+1");
        MyAPIGateway.TerminalControls.AddAction<IMyCameraBlock>(nextFrameAction);

        prevFrameAction = MyAPIGateway.TerminalControls.CreateAction<IMyCameraBlock>("Camera_PrevFrame");
        prevFrameAction.Name = new StringBuilder("Prev Frame");
        prevFrameAction.Action = ActionPrevFrame;
        prevFrameAction.Writer = FrameNumWriterGen("-1");
        MyAPIGateway.TerminalControls.AddAction<IMyCameraBlock>(prevFrameAction);

        nextFrame10Action = MyAPIGateway.TerminalControls.CreateAction<IMyCameraBlock>("Camera_NextFrame10");
        nextFrame10Action.Name = new StringBuilder("Next Frame +10");
        nextFrame10Action.Action = ActionNextFrame10;
        nextFrame10Action.Writer = FrameNumWriterGen("+10");
        MyAPIGateway.TerminalControls.AddAction<IMyCameraBlock>(nextFrame10Action);

        prevFrame10Action = MyAPIGateway.TerminalControls.CreateAction<IMyCameraBlock>("Camera_PrevFrame10");
        prevFrame10Action.Name = new StringBuilder("Prev Frame -10");
        prevFrame10Action.Action = ActionPrevFrame10;
        prevFrame10Action.Writer = FrameNumWriterGen("-10");
        MyAPIGateway.TerminalControls.AddAction<IMyCameraBlock>(prevFrame10Action);

        nextKeyFrameAction = MyAPIGateway.TerminalControls.CreateAction<IMyCameraBlock>("Camera_NextKeyFrame");
        nextKeyFrameAction.Name = new StringBuilder("Next Keyframe");
        nextKeyFrameAction.Action = ActionNextKeyFrame;
        nextKeyFrameAction.Writer = FrameNumWriterGen("+K");
        MyAPIGateway.TerminalControls.AddAction<IMyCameraBlock>(nextKeyFrameAction);

        prevKeyFrameAction = MyAPIGateway.TerminalControls.CreateAction<IMyCameraBlock>("Camera_PrevKeyFrame");
        prevKeyFrameAction.Name = new StringBuilder("Prev Keyframe");
        prevKeyFrameAction.Action = ActionPrevKeyFrame;
        prevKeyFrameAction.Writer = FrameNumWriterGen("-K");
        MyAPIGateway.TerminalControls.AddAction<IMyCameraBlock>(prevKeyFrameAction);

        playAction = MyAPIGateway.TerminalControls.CreateAction<IMyCameraBlock>("Camera_Play");
        playAction.Name = new StringBuilder("Play");
        playAction.Action = ActionPlay;
        playAction.Writer = ConditionToggleWriterGen((cam) => cam.PlaybackState == PlaybackMode.Playing, "Play");
        MyAPIGateway.TerminalControls.AddAction<IMyCameraBlock>(playAction);

        pauseAction = MyAPIGateway.TerminalControls.CreateAction<IMyCameraBlock>("Camera_Pause");
        pauseAction.Name = new StringBuilder("Pause");
        pauseAction.Action = ActionPause;
        pauseAction.Writer = ConditionToggleWriterGen((cam) => cam.PlaybackState == PlaybackMode.Paused, "Paus");
        MyAPIGateway.TerminalControls.AddAction<IMyCameraBlock>(pauseAction);

        playPauseAction = MyAPIGateway.TerminalControls.CreateAction<IMyCameraBlock>("Camera_PlayPause");
        playPauseAction.Name = new StringBuilder("Play/Pause");
        playPauseAction.Action = ActionPlayPause;
        playPauseAction.Writer = ConditionToggleWriterGen((cam) => cam.PlaybackState == PlaybackMode.Playing, "Play");
        MyAPIGateway.TerminalControls.AddAction<IMyCameraBlock>(playPauseAction);

        stopAction = MyAPIGateway.TerminalControls.CreateAction<IMyCameraBlock>("Camera_Stop");
        stopAction.Name = new StringBuilder("Stop");
        stopAction.Action = ActionStop;
        stopAction.Writer = ConditionToggleWriterGen((cam) => cam.PlaybackState == PlaybackMode.Stopped, "Stop");
        MyAPIGateway.TerminalControls.AddAction<IMyCameraBlock>(stopAction);

        setFrameAction = MyAPIGateway.TerminalControls.CreateAction<IMyCameraBlock>("Camera_SetKey");
        setFrameAction.Name = new StringBuilder("Set Frame");
        setFrameAction.Action = ActionSetFrame;
        setFrameAction.Writer = (b, sb) => { sb.Clear(); sb.Append("^Frm"); };
        MyAPIGateway.TerminalControls.AddAction<IMyCameraBlock>(setFrameAction);

        setPosFrameAction = MyAPIGateway.TerminalControls.CreateAction<IMyCameraBlock>("Camera_SetPosKey");
        setPosFrameAction.Name = new StringBuilder("Set Pos");
        setPosFrameAction.Action = ActionSetPosFrame;
        setPosFrameAction.Writer = (b, sb) => { sb.Clear(); sb.Append("^Pos"); };
        MyAPIGateway.TerminalControls.AddAction<IMyCameraBlock>(setPosFrameAction);

        setViewFrameAction = MyAPIGateway.TerminalControls.CreateAction<IMyCameraBlock>("Camera_SetViewKey");
        setViewFrameAction.Name = new StringBuilder("Set View");
        setViewFrameAction.Action = ActionSetViewFrame;
        setViewFrameAction.Writer = (b, sb) => { sb.Clear(); sb.Append("^View"); };
        MyAPIGateway.TerminalControls.AddAction<IMyCameraBlock>(setViewFrameAction);

        delKeyframeAction = MyAPIGateway.TerminalControls.CreateAction<IMyCameraBlock>("Camera_DelKeyframe");
        delKeyframeAction.Name = new StringBuilder("Remove Keyframe");
        delKeyframeAction.Action = ActionDelKeyframe;
        delKeyframeAction.Writer = (b, sb) => { sb.Clear(); sb.Append("RmFrm"); };
        MyAPIGateway.TerminalControls.AddAction<IMyCameraBlock>(delKeyframeAction);

        toggleKeyframeModeAction = MyAPIGateway.TerminalControls.CreateAction<IMyCameraBlock>("Camera_ToggleTransitionMode");
        toggleKeyframeModeAction.Name = new StringBuilder("Change Transition");
        toggleKeyframeModeAction.Action = ActionToggleKeyframeMode;
        toggleKeyframeModeAction.Writer = KeyframeModeWriter;
        MyAPIGateway.TerminalControls.AddAction<IMyCameraBlock>(toggleKeyframeModeAction);

        lockViewAction = MyAPIGateway.TerminalControls.CreateAction<IMyCameraBlock>("Camera_LockViewToTarget");
        lockViewAction.Name = new StringBuilder("Lock View to Target");
        lockViewAction.Action = ActionLockViewToTarget;
        lockViewAction.Writer = ConditionToggleWriterGen((cam) => cam.view_locked_to != null, "VTgt");
        MyAPIGateway.TerminalControls.AddAction<IMyCameraBlock>(lockViewAction);

        lockPosAction = MyAPIGateway.TerminalControls.CreateAction<IMyCameraBlock>("Camera_LockPosToTarget");
        lockPosAction.Name = new StringBuilder("Lock Pos to Target");
        lockPosAction.Action = ActionLockPosToTarget;
        lockPosAction.Writer = ConditionToggleWriterGen((cam) => cam.pos_locked_to != null, "PTgt");
        MyAPIGateway.TerminalControls.AddAction<IMyCameraBlock>(lockPosAction);

        setFocusAction = MyAPIGateway.TerminalControls.CreateAction<IMyCameraBlock>("Camera_SetFocusToViewLock");
        setFocusAction.Name = new StringBuilder("Set Focus to View Lock");
        setFocusAction.Action = ActionSetFocusToViewLock;
        setFocusAction.Writer = (b, sb) => { sb.Clear(); sb.Append("Focus"); };
        MyAPIGateway.TerminalControls.AddAction<IMyCameraBlock>(setFocusAction);

        rangeSlider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyCameraBlock>("Camera_FocusDistance");
        rangeSlider.Title = MyStringId.GetOrCompute("Focus Distance");
        rangeSlider.Tooltip = MyStringId.GetOrCompute("Focus distance. Important when camera is locked to a distant object.");
        rangeSlider.SetLogLimits(1.0f, 100000.0f);
        rangeSlider.SupportsMultipleBlocks = true;
        rangeSlider.Getter = b => (float)SettingsStore.Get(b, "focus_distance", 1.0f);
        rangeSlider.Setter = (b, v) => SettingsStore.Set(b, "focus_distance", (float)LogRound(v));
        rangeSlider.Writer = (b, result) => result.Append(SettingsStore.Get(b, "focus_distance", 1.0f));
        rangeSlider.Visible = BlockIsMyCamera;
        MyAPIGateway.TerminalControls.AddControl<IMyCameraBlock>(rangeSlider);

        MyAPIGateway.TerminalControls.CustomActionGetter += GetCameraActions;
    }
}