using System;
using SpinnakerNET;
using SpinnakerNET.GenApi;

namespace GolfCamBridge
{
    /// <summary>Best-effort GenICam node writes: missing or read-only nodes are logged and skipped.</summary>
    internal static class Nodes
    {
        public static bool SetEnum(INodeMap map, string node, params string[] candidates)
        {
            try
            {
                IEnum e = map.GetNode<IEnum>(node);
                if (e == null || !e.IsWritable) { Log.Skip(node, "not writable"); return false; }
                foreach (string c in candidates)
                {
                    IEnumEntry entry = e.GetEntryByName(c);
                    if (entry != null && entry.IsReadable)
                    {
                        e.Value = entry.Symbolic;
                        Log.Set(node, c);
                        return true;
                    }
                }
                Log.Skip(node, "none of [" + string.Join(", ", candidates) + "] available");
            }
            catch (SpinnakerException ex) { Log.Skip(node, ex.Message); }
            return false;
        }

        public static bool SetBool(INodeMap map, string node, bool value)
        {
            try
            {
                IBool b = map.GetNode<IBool>(node);
                if (b == null || !b.IsWritable) { Log.Skip(node, "not writable"); return false; }
                b.Value = value;
                Log.Set(node, value.ToString());
                return true;
            }
            catch (SpinnakerException ex) { Log.Skip(node, ex.Message); return false; }
        }

        /// <summary>Sets a float node, clamped to its [Min, Max].</summary>
        public static bool SetFloat(INodeMap map, string node, double value)
        {
            try
            {
                IFloat f = map.GetNode<IFloat>(node);
                if (f == null || !f.IsWritable) { Log.Skip(node, "not writable"); return false; }
                double v = Math.Max(f.Min, Math.Min(f.Max, value));
                f.Value = v;
                Log.Set(node, v.ToString("0.###") + (Math.Abs(v - value) > 1e-6 ? $" (clamped from {value})" : ""));
                return true;
            }
            catch (SpinnakerException ex) { Log.Skip(node, ex.Message); return false; }
        }

        /// <summary>Sets an integer node, clamped to [Min, Max]. If the camera rejects the value
        /// (increment mismatch), retries with the value rounded down to 8/16/32/64.</summary>
        public static bool SetInt(INodeMap map, string node, long value)
        {
            IInteger i;
            try
            {
                i = map.GetNode<IInteger>(node);
                if (i == null || !i.IsWritable) { Log.Skip(node, "not writable"); return false; }
            }
            catch (SpinnakerException ex) { Log.Skip(node, ex.Message); return false; }

            long v = Math.Max(i.Min, Math.Min(i.Max, value));
            string lastError = null;
            foreach (long align in new long[] { 1, 8, 16, 32, 64 })
            {
                long candidate = Math.Max(i.Min, v / align * align);
                try
                {
                    i.Value = candidate;
                    Log.Set(node, candidate.ToString());
                    return true;
                }
                catch (SpinnakerException ex) { lastError = ex.Message; }
            }
            Log.Skip(node, lastError ?? "rejected");
            return false;
        }

        public static bool SetIntMax(INodeMap map, string node)
        {
            try
            {
                IInteger i = map.GetNode<IInteger>(node);
                if (i == null || !i.IsWritable) { Log.Skip(node, "not writable"); return false; }
                return SetInt(map, node, i.Max);
            }
            catch (SpinnakerException ex) { Log.Skip(node, ex.Message); return false; }
        }

        public static long GetInt(INodeMap map, string node, long fallback)
        {
            try
            {
                IInteger i = map.GetNode<IInteger>(node);
                return i != null && i.IsReadable ? i.Value : fallback;
            }
            catch (SpinnakerException) { return fallback; }
        }

        public static long GetIntMax(INodeMap map, string node, long fallback)
        {
            try
            {
                IInteger i = map.GetNode<IInteger>(node);
                return i != null && i.IsReadable ? i.Max : fallback;
            }
            catch (SpinnakerException) { return fallback; }
        }

        public static double GetFloat(INodeMap map, string node, double fallback)
        {
            try
            {
                IFloat f = map.GetNode<IFloat>(node);
                return f != null && f.IsReadable ? f.Value : fallback;
            }
            catch (SpinnakerException) { return fallback; }
        }

        /// <summary>Value and [Min, Max] of a float node; NaN when unreadable.</summary>
        public static bool GetFloatInfo(INodeMap map, string node, out double value, out double min, out double max)
        {
            value = min = max = double.NaN;
            try
            {
                IFloat f = map.GetNode<IFloat>(node);
                if (f == null || !f.IsReadable) return false;
                value = f.Value;
                min = f.Min;
                max = f.Max;
                return true;
            }
            catch (SpinnakerException) { return false; }
        }

        public static string GetString(INodeMap map, string node)
        {
            try
            {
                IString s = map.GetNode<IString>(node);
                return s != null && s.IsReadable ? s.Value : "";
            }
            catch (SpinnakerException) { return ""; }
        }
    }
}
