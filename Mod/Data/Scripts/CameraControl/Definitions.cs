using VRageMath;
using System;
using System.Text;
using VRage.Utils;

public enum TransitionMode
{
    Constant,
    Linear,
    Cosine,
    Spline // catmull-rom
}

public enum TrackMode
{
    Locked,
    Unlocked
}

public enum PlaybackMode
{
    Stopped,
    Paused,
    Playing
}

/*
enum ShotKind
{
    // defined by a target coordinate - LookAt
    TrackingShot,
    // defined by a target vector - Direction
    PanningShot
}
*/

/*
enum CameraState
{
    Paused,
    Running
}
*/

public struct TrackingInfo
{
    public TrackMode Mode;
    public long EntityId;
    public Vector3D Value3D; // the value being tracked
    public TransitionMode Transition;

    public IMyEntity Entity
    {
        get
        {
            if (Mode != TrackMode.Locked) return null;

            IMyEntity ent;

            if (!MyAPIGateway.Entities.TryGetEntityById(EntityId, out ent))
            {
                throw new Exception("Entity to track cannot be found: " + EntityId);
            }
            return ent;
        }
        set
        {
            Mode = TrackMode.Locked;
            EntityId = value.EntityId;
        }
    }

    public Vector3D Evaluate(bool direction)
    {
        // global value
        if (Mode == TrackMode.Unlocked) return Value3D;
        // local value; translate
        var ent = Entity;

        if (direction)
        {
            return Vector3D.TransformNormal(Value3D, ent.WorldMatrix);
        }
        else
        {
            return Vector3D.Transform(Value3D, ent.WorldMatrix);
        }
    }
}

static class TransitionFunctions
{
    public static Vector3D constant_transition(Vector3D x, Vector3D y, double factor)
    {
        return x;
    }

    public static Vector3D linear_transition(Vector3D x, Vector3D y, double factor)
    {
        return x * (1 - factor) + y * (factor);
    }

    public static Vector3D cosine_transition(Vector3D x, Vector3D y, double factor)
    {
        return linear_transition(x, y, 0.5 - 0.5 * Math.Cos(factor * Math.PI));
    }

    public static Vector3D catmull_rom_transition(
        Vector3D P0, Vector3D P1, Vector3D P2, Vector3D P3,
        float f0, float f1, float f2, float f3,
        bool left_linear, bool right_linear, float f
    )
    {
        // MyLog.Default.WriteLine(String.Format("For spline by:"));
        // MyLog.Default.WriteLine(String.Format("P0: {0}, {1}", f0, P0));
        // MyLog.Default.WriteLine(String.Format("P1: {0}, {1}", f1, P1));
        // MyLog.Default.WriteLine(String.Format("P2: {0}, {1}", f2, P2));
        // MyLog.Default.WriteLine(String.Format("P3: {0}, {1}", f3, P3));
        // MyLog.Default.WriteLine(String.Format("F: {0} (leftlin {1}, rightlin {2})", f, left_linear, right_linear));
        // the catmull-rom spline through P1 is parallel to P0-P2
        if (left_linear)
        {
            // adjust P0 to P0' so that P0'-P2 is multiple of P0-P1
            f0 = f1 - (f2 - f1); // hack

            float fac = (f2 - f0) / (f1 - f0);
            P0 = P2 - (P1 - P0) * fac;
            // MyLog.Default.WriteLine(String.Format("P0': {0}, {1} for {2}", f0, P0, fac));
        }
        if (right_linear)
        {
            // same in reverse
            f3 = f2 - (f1 - f2); // hack

            float fac = (f3 - f1) / (f3 - f2);
            P3 = P1 - (P2 - P3) * fac;
            // f3 = f1 - (f2 - f3) * fac;
            // MyLog.Default.WriteLine(String.Format("P3': {0}, {1}", f3, P3));
        }
        // see https://en.wikipedia.org/wiki/Centripetal_Catmull%E2%80%93Rom_spline
        // except use vector length instead of component length to avoid orientation dependence
        var t1_ = (P1 - P0).LengthSquared() + (f1 - f0) * (f1 - f0);
        var t2_ = (P2 - P1).LengthSquared() + (f2 - f1) * (f2 - f1);
        var t3_ = (P3 - P2).LengthSquared() + (f3 - f2) * (f3 - f2);
        const double tension = 0.6; // slightly rounded
        var t0 = 0.0;
        var t1 = t0 + Math.Pow(t1_, tension);
        var t2 = t1 + Math.Pow(t2_, tension);
        var t3 = t2 + Math.Pow(t3_, tension);

        // MyLog.Default.WriteLine(String.Format("T0: {0}", t0));
        // MyLog.Default.WriteLine(String.Format("T1: {0}", t1));
        // MyLog.Default.WriteLine(String.Format("T2: {0}", t2));
        // MyLog.Default.WriteLine(String.Format("T3: {0}", t3));

        var t = t1 + (t2 - t1) * ((f - f1) / (f2 - f1));
        // MyLog.Default.WriteLine(String.Format("at t {0} for f {1}", t, f));

        var A1 = (t1 - t) * P0 / (t1 - t0) + (t - t0) * P1 / (t1 - t0);
        var A2 = (t2 - t) * P1 / (t2 - t1) + (t - t1) * P2 / (t2 - t1);
        var A3 = (t3 - t) * P2 / (t3 - t2) + (t - t2) * P3 / (t3 - t2);
        // MyLog.Default.WriteLine(String.Format("A1: {0}", A1));
        // MyLog.Default.WriteLine(String.Format("A2: {0}", A2));
        // MyLog.Default.WriteLine(String.Format("A3: {0}", A3));

        var B1 = (t2 - t) * A1 / (t2 - t0) + (t - t0) * A2 / (t2 - t0);
        var B2 = (t3 - t) * A2 / (t3 - t1) + (t - t1) * A3 / (t3 - t1);
        // MyLog.Default.WriteLine(String.Format("B1: {0}", B1));
        // MyLog.Default.WriteLine(String.Format("B2: {0}", B2));

        var C = (t2 - t) * B1 / (t2 - t1) + (t - t1) * B2 / (t2 - t1);
        // MyLog.Default.WriteLine(String.Format("C: {0}", C));

        return C;
    }
}