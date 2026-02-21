using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ynnu.OpenHaptic
{
    public class BiomechForceCurve : MonoBehaviour
    {
        [Header("Curve Source")]
        public TextAsset csv;

        [Header("Units")]
        public float xScale = 1f;

        public float yScale = 1f;

        private float[] xs;
        private float[] ys;

        private void Awake()
        {
            Load();
        }

        public void Load()
        {
            if (csv == null)
            {
                Debug.LogError("[BiomechForceCurve] csv is null.");
                xs = ys = Array.Empty<float>();
                return;
            }

            var lines = csv.text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var xList = new List<float>(lines.Length);
            var yList = new List<float>(lines.Length);

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#")) continue;
                if (line.ToLower().Contains("x") && line.ToLower().Contains("force")) continue; // skip header

                var parts = line.Split(',');
                if (parts.Length < 2) continue;

                if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                {
                    xList.Add(x);
                    yList.Add(y);
                }
            }

            xs = xList.ToArray();
            ys = yList.ToArray();

            if (xs.Length < 2)
                Debug.LogError("[BiomechForceCurve] Not enough points in curve.");
        }

        public float Evaluate(float xInput)
        {
            if (xs == null || ys == null || xs.Length < 2) return 0f;

            float x = xInput * xScale;

            // clamp
            if (x <= xs[0]) return ys[0] * yScale;
            int n = xs.Length;
            if (x >= xs[n - 1]) return ys[n - 1] * yScale;

            // binary search
            int lo = 0, hi = n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (xs[mid] <= x) lo = mid;
                else hi = mid;
            }

            float x0 = xs[lo], x1 = xs[hi];
            float y0 = ys[lo], y1 = ys[hi];
            float t = (x - x0) / (x1 - x0 + 1e-12f);
            return Mathf.Lerp(y0, y1, t) * yScale;
        }
    }
}
