using System;
using UnityEngine;

namespace Agents
{
    /// <summary>
    /// Available easing function types for smooth interpolation.
    /// </summary>
    public enum EasingType
    {
        /// <summary>Constant speed, no acceleration.</summary>
        Linear,
        
        /// <summary>Starts slow, accelerates. Good for departures.</summary>
        EaseInQuad,
        
        /// <summary>Starts fast, decelerates. Good for arrivals.</summary>
        EaseOutQuad,
        
        /// <summary>Slow start and end, fast middle. Most natural for movement.</summary>
        EaseInOutQuad,
        
        /// <summary>Stronger ease-in curve.</summary>
        EaseInCubic,
        
        /// <summary>Stronger ease-out curve.</summary>
        EaseOutCubic,
        
        /// <summary>Stronger ease-in-out curve.</summary>
        EaseInOutCubic,
        
        /// <summary>Very smooth S-curve. Ken Perlin's smoothstep.</summary>
        SmoothStep,
        
        /// <summary>Even smoother S-curve. Ken Perlin's smootherstep.</summary>
        SmootherStep,
        
        /// <summary>Sine-based smooth curve.</summary>
        EaseInOutSine,
        
        /// <summary>Exponential ease-in.</summary>
        EaseInExpo,
        
        /// <summary>Exponential ease-out.</summary>
        EaseOutExpo,
        
        /// <summary>Slight overshoot at the end for bouncy feel.</summary>
        EaseOutBack,
        
        /// <summary>Slight overshoot at start and end.</summary>
        EaseInOutBack
    }

    /// <summary>
    /// Static utility class providing easing functions for smooth interpolation.
    /// All functions take a normalized time t (0-1) and return a normalized value (0-1).
    /// </summary>
    public static class EasingFunctions
    {
        /// <summary>
        /// Evaluates the easing function at time t.
        /// </summary>
        /// <param name="type">The easing type to use.</param>
        /// <param name="t">Normalized time (0-1).</param>
        /// <returns>Eased value (0-1), may exceed bounds for overshoot easings.</returns>
        public static float Evaluate(EasingType type, float t)
        {
            t = Mathf.Clamp01(t);
            
            return type switch
            {
                EasingType.Linear => t,
                EasingType.EaseInQuad => EaseInQuad(t),
                EasingType.EaseOutQuad => EaseOutQuad(t),
                EasingType.EaseInOutQuad => EaseInOutQuad(t),
                EasingType.EaseInCubic => EaseInCubic(t),
                EasingType.EaseOutCubic => EaseOutCubic(t),
                EasingType.EaseInOutCubic => EaseInOutCubic(t),
                EasingType.SmoothStep => SmoothStep(t),
                EasingType.SmootherStep => SmootherStep(t),
                EasingType.EaseInOutSine => EaseInOutSine(t),
                EasingType.EaseInExpo => EaseInExpo(t),
                EasingType.EaseOutExpo => EaseOutExpo(t),
                EasingType.EaseOutBack => EaseOutBack(t),
                EasingType.EaseInOutBack => EaseInOutBack(t),
                _ => t
            };
        }
        
        // Quadratic
        public static float EaseInQuad(float t) => t * t;
        public static float EaseOutQuad(float t) => 1f - (1f - t) * (1f - t);
        public static float EaseInOutQuad(float t) => t < 0.5f ? 2f * t * t : 1f - Mathf.Pow(-2f * t + 2f, 2f) / 2f;
        
        // Cubic
        public static float EaseInCubic(float t) => t * t * t;
        public static float EaseOutCubic(float t) => 1f - Mathf.Pow(1f - t, 3f);
        public static float EaseInOutCubic(float t) => t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;
        
        // Smoothstep variants (Ken Perlin)
        public static float SmoothStep(float t) => t * t * (3f - 2f * t);
        public static float SmootherStep(float t) => t * t * t * (t * (6f * t - 15f) + 10f);
        
        // Sine
        public static float EaseInOutSine(float t) => -(Mathf.Cos(Mathf.PI * t) - 1f) / 2f;
        
        // Exponential
        public static float EaseInExpo(float t) => t == 0f ? 0f : Mathf.Pow(2f, 10f * t - 10f);
        public static float EaseOutExpo(float t) => t == 1f ? 1f : 1f - Mathf.Pow(2f, -10f * t);
        
        // Back (overshoot)
        private const float BackC1 = 1.70158f;
        private const float BackC2 = BackC1 * 1.525f;
        private const float BackC3 = BackC1 + 1f;
        
        public static float EaseOutBack(float t) => 1f + BackC3 * Mathf.Pow(t - 1f, 3f) + BackC1 * Mathf.Pow(t - 1f, 2f);
        public static float EaseInOutBack(float t) => t < 0.5f
            ? (Mathf.Pow(2f * t, 2f) * ((BackC2 + 1f) * 2f * t - BackC2)) / 2f
            : (Mathf.Pow(2f * t - 2f, 2f) * ((BackC2 + 1f) * (t * 2f - 2f) + BackC2) + 2f) / 2f;
        
        /// <summary>
        /// Interpolates between two values using the specified easing.
        /// </summary>
        public static float Lerp(float a, float b, float t, EasingType easing)
        {
            return Mathf.LerpUnclamped(a, b, Evaluate(easing, t));
        }
        
        /// <summary>
        /// Interpolates between two vectors using the specified easing.
        /// </summary>
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t, EasingType easing)
        {
            return Vector3.LerpUnclamped(a, b, Evaluate(easing, t));
        }
        
        /// <summary>
        /// Interpolates between two quaternions using the specified easing.
        /// </summary>
        public static Quaternion Slerp(Quaternion a, Quaternion b, float t, EasingType easing)
        {
            return Quaternion.SlerpUnclamped(a, b, Evaluate(easing, t));
        }
    }
}
