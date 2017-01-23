using VRageMath;
using System;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using VRage.Utils;

public interface ITimeline
{
    Vector3D Evaluate(int frame, bool direction);
}

public class Timeline : ITimeline
{
    // frame to keyframe
    public System.Collections.Generic.SortedList<int, TrackingInfo> keyframes;

    public Timeline()
    {
        keyframes = new System.Collections.Generic.SortedList<int, TrackingInfo>();
    }

    public bool Empty
    {
        get
        {
            return keyframes.Count() == 0;
        }
    }

    public Vector3D Evaluate(int frame, bool direction)
    {
        bool from_found = false, to_found = false;
        TrackingInfo from = new TrackingInfo(), to = new TrackingInfo();
        int from_frame = 0, to_frame = 0;

        bool control_lo_found = false, control_hi_found = false;
        TrackingInfo control_lo = new TrackingInfo(), control_hi = new TrackingInfo();
        int control_lo_frame = 0, control_hi_frame = 0;

        foreach (var pair in keyframes)
        {
            int keyframe = pair.Key;
            TrackingInfo keyinfo = pair.Value;
            // MyLog.Default.WriteLine(String.Format("check {0} <> {1}", frame, keyframe));

            if (keyframe <= frame)
            {
                // pass down to lo control slot
                control_lo_frame = from_frame;
                control_lo = from;
                control_lo_found = from_found;

                from_frame = keyframe;
                from = keyinfo;
                from_found = true;
            }
            if (keyframe >= frame)
            {
                // fill hi control slot
                if (to_found)
                {
                    control_hi_frame = keyframe;
                    control_hi = keyinfo;
                    control_hi_found = true;
                    break;
                }
                to_frame = keyframe;
                to = keyinfo;
                to_found = true;
            }
        }
        if (!from_found && !to_found) throw new Exception("timeline empty - cannot evaluate.");
        if (!from_found)
        { // frame before timeline
            return to.Evaluate(direction: direction);
        }
        if (!to_found)
        { // frame after timeline
            return from.Evaluate(direction: direction);
        }

        Vector3D from_value = from.Evaluate(direction: direction);
        Vector3D to_value = to.Evaluate(direction: direction);

        if (from_frame == to_frame)
        { // right on the frame - just use the frame value
            // MyLog.Default.WriteLine(String.Format("keyframe = <{0},{1},{2}>", from_value.X, from_value.Y, from_value.Z));
            return from_value;
        }

        // MyLog.Default.WriteLine(String.Format("interpol from = <{0},{1},{2}>", from_value.X, from_value.Y, from_value.Z));
        // MyLog.Default.WriteLine(String.Format("interpol to = <{0},{1},{2}>", to_value.X, to_value.Y, to_value.Z));

        float factor = (frame - from_frame) * 1.0f / (to_frame - from_frame);
        // MyLog.Default.WriteLine(String.Format("frame = {0} between {1} and {2} - {3}", frame, from_frame, to_frame, factor));
        // linear and spline transitions are the ones that offer "natural continuity"
        // all others behave like a constant transition, ie. a linear transition from from_value/to to_value
        if (!control_lo_found || control_lo.Transition != TransitionMode.Spline && control_lo.Transition != TransitionMode.Linear)
        {
            if (!control_lo_found) control_lo_frame = from_frame - 60;
            // constant
            control_lo.Mode = TrackMode.Unlocked;
            control_lo.Transition = TransitionMode.Linear;
            control_lo.Value3D = from_value;
        }
        if (!control_hi_found || control_hi.Transition != TransitionMode.Spline && control_hi.Transition != TransitionMode.Linear)
        {
            if (!control_hi_found) control_hi_frame = to_frame + 60;
            control_hi.Mode = TrackMode.Unlocked;
            control_hi.Transition = TransitionMode.Linear;
            control_hi.Value3D = to_value;
        }

        switch (to.Transition)
        {
            case TransitionMode.Constant:
                return TransitionFunctions.constant_transition(from_value, to_value, factor);
            case TransitionMode.Linear:
                return TransitionFunctions.linear_transition(from_value, to_value, factor);
            case TransitionMode.Cosine:
                return TransitionFunctions.cosine_transition(from_value, to_value, factor);
            case TransitionMode.Spline:
                return TransitionFunctions.catmull_rom_transition(
                    control_lo.Evaluate(direction: direction), from_value, to_value, control_hi.Evaluate(direction: direction),
                    control_lo_frame, from_frame, to_frame, control_hi_frame,
                    control_lo.Transition == TransitionMode.Linear, control_hi.Transition == TransitionMode.Linear,
                    frame
                );

            default:
                throw new Exception("undefined transition mode");
        }
    }

    public void AddKeyframe(int frame, TrackingInfo info)
    {
        keyframes.Remove(frame);
        keyframes.Add(frame, info);
    }

    public bool IsKeyframe(int frame)
    {
        return keyframes.ContainsKey(frame);
    }

    // if no smaller keyframe can be found, returns frame
    public int KeyframeNextSmallerThan(int frame)
    {
        int prev_frame = frame;

        foreach (var pair in keyframes)
        {
            if (pair.Key >= frame) break;
            prev_frame = pair.Key;
        }
        return prev_frame;
    }

    // if no larger keyframe can be found, returns frame
    public int KeyframeNextLargerThan(int frame)
    {
        foreach (var pair in keyframes)
        {
            if (pair.Key > frame) return pair.Key;
        }
        return frame;
    }

    public void DeleteKeyframe(int frame)
    {
        keyframes.Remove(frame);
    }

    // last frame and up
    public int LastFrame
    {
        get
        {
            if (keyframes.Count() == 0)
            {
                return Int32.MinValue;
            }
            return keyframes.Last().Key;
        }
    }

    public string Serialize()
    {
        // [5:Locked:170:<7, 8, 9>:Linear] [15:...]
        StringBuilder builder = new StringBuilder();

        foreach (var pair in keyframes)
        {
            int keyframe = pair.Key;
            TrackingInfo keyinfo = pair.Value;
            builder.Append("[");
            builder.Append(keyframe);
            builder.Append(":");
            switch (keyinfo.Mode)
            {
                case TrackMode.Locked: builder.Append("Locked"); break;
                case TrackMode.Unlocked: builder.Append("Unlocked"); break;
            }
            builder.Append(":");
            if (keyinfo.Mode == TrackMode.Locked)
            {
                builder.Append(keyinfo.EntityId);
            }
            builder.Append(":");
            builder.AppendFormat("<{0},{1},{2}>", keyinfo.Value3D.X, keyinfo.Value3D.Y, keyinfo.Value3D.Z);
            builder.Append(":");
            switch (keyinfo.Transition)
            {
                case TransitionMode.Constant: builder.Append("Constant"); break;
                case TransitionMode.Linear: builder.Append("Linear"); break;
                case TransitionMode.Cosine: builder.Append("Cosine"); break;
                case TransitionMode.Spline: builder.Append("Spline"); break;
                default: throw new Exception("Unknown transition mode!");
            }
            builder.Append("] ");
        }
        return builder.ToString();
    }

    public static Timeline Deserialize(RDParser parser)
    {
        Timeline timeline = new Timeline();

        while (true)
        {
            long frame = 0;
            TrackingInfo info = new TrackingInfo();

            if (!parser.Accept("[")) break;
            if (!parser.GetLong(ref frame)) parser.Fail("frame expected");
            parser.Expect(":");
            if (parser.Accept("Locked")) info.Mode = TrackMode.Locked;
            else if (parser.Accept("Unlocked")) info.Mode = TrackMode.Unlocked;
            else parser.Fail("unknown track mode");
            parser.Expect(":");
            if (info.Mode == TrackMode.Locked)
            {
                if (!parser.GetLong(ref info.EntityId))
                {
                    parser.Fail("entity id expected");
                }
            }
            parser.Expect(":");
            parser.Expect("<");
            if (!parser.GetDouble(ref info.Value3D.X))
                parser.Fail("vector x expected");
            parser.Expect(",");
            if (!parser.GetDouble(ref info.Value3D.Y))
                parser.Fail("vector y expected");
            parser.Expect(",");
            if (!parser.GetDouble(ref info.Value3D.Z))
                parser.Fail("vector z expected");
            parser.Expect(">");
            parser.Expect(":");
            if (parser.Accept("Constant")) info.Transition = TransitionMode.Constant;
            else if (parser.Accept("Linear")) info.Transition = TransitionMode.Linear;
            else if (parser.Accept("Cosine")) info.Transition = TransitionMode.Cosine;
            else if (parser.Accept("Spline")) info.Transition = TransitionMode.Spline;
            else parser.Fail("unknown transition mode");
            parser.Expect("]");

            timeline.keyframes.Add((int)frame, info);
        }
        return timeline;
    }
}

public class RelativeTimeline : ITimeline
{
    ITimeline basis, target;
    public RelativeTimeline(ITimeline basis, ITimeline target)
    {
        this.basis = basis;
        this.target = target;
    }

    public Vector3D Evaluate(int frame, bool direction)
    {
        if (direction)
        {
            return target.Evaluate(frame, true);
        }

        Vector3D basis_vec = basis.Evaluate(frame, false);
        Vector3D target_vec = target.Evaluate(frame, false);
        return basis_vec + Vector3D.Normalize(target_vec - basis_vec);
    }
}