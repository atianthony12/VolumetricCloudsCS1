using System;
using System.Globalization;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// LIGHT THROUGH CLOUDS (trial, 2026-11): the sun's light inside the clouds brought towards what a path tracer
    /// through these same clouds gives. On the "Light inside the clouds" slider from 100% to 200% (<see cref="Row"/>):
    /// at 100% and under it is off, the light from before, exactly (every use sits behind the shader's
    /// <c>_DeepLight.x &gt; 0</c> branch); at 200% all of it, the closest the model gets.
    /// </summary>
    /// <remarks>
    /// Measured 2026-11-11 (the Unity 5.6 bench, Auto, 70% cover): the light before stops in a thin shell, exp(-tau/2):
    /// the darkest tenth of a cloud got 13% of its mean sun where the reference has 53%, and towards the sun the
    /// clouds carried half the light. Three parts, on the Cumulus light only (Cumulus, Soft, Mixed, Auto), none
    /// reading a texture: a slow light, none at the lit face, all of it BuildUp optical depths in, then
    /// 1 / (1 + K tau), never under the light before; the scattered light dimmed towards a cloud's edge (bounced
    /// light needs cloud around it; the shader's body, from each density read), which shades a cloud like a lit ball; a middle lobe
    /// towards the sun for thin and middling cloud. The slow light and the lobe add nothing at a lit face seen
    /// side-on (tau 0); the edge dimming does take from a face's outer skin, so lit tops come out a little dimmer
    /// than before (as the reference has them): this row is not "never darker". Not under Simpler clouds (its
    /// shader has none of it), not under Classic (another light). The ground shadows are the shadow map's, untouched.
    /// The numbers are fields, not constants, so they can be tried without a build.
    /// </remarks>
    public static class DeepLight
    {
        /// <summary>The slow light's share at 100%.</summary>
        public static float Fill = 1.25f;
        /// <summary>Its fall-off: 1 / (1 + K tau).</summary>
        public static float K = 0.15f;
        /// <summary>The optical depth it builds up over.</summary>
        public static float BuildUp = 2f;
        /// <summary>The scattered light at a cloud's very edge at 100%, as a share of what it is well inside.</summary>
        public static float EdgeFloor = 0.35f;
        /// <summary>The middle lobe: its weight at 100%, its g, how fast it dies (x tau).</summary>
        public static float Mid = 1f;
        public static float MidG = 0.6f;
        public static float MidReach = 0.25f;
        /// <summary>The direct beam's cap at 100%, x CloudStyle.SingleCap.</summary>
        public static float BeamCap = 3f;
        /// <summary>What each kind's inside value is times to make "how deep in its cloud", 0..1.</summary>
        public static float BodyHeap = 5f;
        public static float BodyPuff = 2f;
        public static float BodyLayer = 2f;

        /// <summary>
        /// How much of it there is now, 0..1: the upper half of the "Light inside the clouds" slider
        /// ("Glow and softer shade", its clock and cloud button included; LightThrough.Slider). 100% and
        /// under -- every player's value as of 1.5.3's default -- is 0, the light from before; 200% is all of it.
        /// (2026-11, the author's call: a row of its own was one slider too many.)
        /// </summary>
        public static float Row
        {
            get { return ForSlider(LightThrough.Slider); }
        }

        /// <summary>The share of it at a value of the slider (0..2): none up to 100%, then up to all of it at 200%.</summary>
        public static float ForSlider(float slider)
        {
            return Math.Max(0f, Math.Min(1f, slider - 1f));
        }

        /// <summary>The row as drawn: 0 under Simpler clouds and while the Cumulus light is not the one drawn.</summary>
        public static float Setting
        {
            get { return CloudShaders.Lite || !CloudStyle.LobesLight ? 0f : Row; }
        }

        /// <summary>The slow light's share at a setting (the shader's _DeepLight.x).</summary>
        public static float FillFor(float setting)
        {
            return Fill * Math.Max(0f, setting);
        }

        /// <summary>The scattered light at a cloud's very edge (the shader's _DeepLight.w): 1 at 0%, EdgeFloor from 100%.</summary>
        public static float EdgeFor(float setting)
        {
            return 1f + (EdgeFloor - 1f) * Math.Max(0f, Math.Min(1f, setting));
        }

        /// <summary>The middle lobe's weight at a setting (the shader's _DeepLight2.x).</summary>
        public static float MidFor(float setting)
        {
            return Mid * Math.Max(0f, setting);
        }

        /// <summary>What the direct beam's cap is times: 1 at 0%, BeamCap from 100%.</summary>
        public static float CapFactor(float setting)
        {
            return 1f + (BeamCap - 1f) * Math.Max(0f, Math.Min(1f, setting));
        }

        /// <summary>The shader's slow light at optical depth tau.</summary>
        public static float Slow(float tau, float setting)
        {
            tau = Math.Max(0f, tau);
            return FillFor(setting) * (1f - (float)Math.Exp(-tau / BuildUp)) / (1f + K * tau);
        }

        /// <summary>
        /// The shader's scattered light at tau: never under <paramref name="before"/> (the light before there,
        /// its soak included).
        /// </summary>
        public static float Scattered(float tau, float before, float setting)
        {
            if (setting <= 0f)
                return before;

            return Math.Max(before, (float)Math.Exp(-Math.Max(0f, tau) * CloudStyle.MultipleExtinction) + Slow(tau, setting));
        }

        /// <summary>The middle lobe's value side-on, which the shader takes off (the shader's _DeepLight2.w).</summary>
        public static float MidSide
        {
            get { return CloudStyle.HenyeyGreenstein(0f, MidG); }
        }

        /// <summary>What the middle lobe adds at an angle to the sun, before its weight and the lobes' gain.</summary>
        public static float MidLobe(float cosTheta)
        {
            return Math.Max(CloudStyle.HenyeyGreenstein(cosTheta, MidG) - MidSide, 0f);
        }

        /// <summary>How much of the middle lobe there is at tau: none at the face, most about 1.5 in.</summary>
        public static float MidDepth(float tau)
        {
            tau = Math.Max(0f, tau);
            return (1f - (float)Math.Exp(-tau)) * (float)Math.Exp(-MidReach * tau);
        }

        public static string Describe(float setting)
        {
            if (setting <= 0f)
                return "off (the light from before)";

            CultureInfo c = CultureInfo.InvariantCulture;
            float before10 = (float)Math.Exp(-10f * CloudStyle.MultipleExtinction);
            return "on " + (setting * 100f).ToString("F0", c) + "% -- of the scattered sun, 2 optical depths in " +
                   (Scattered(2f, 0f, setting) * 100f).ToString("F0", c) + "%, 10 in " +
                   (Scattered(10f, 0f, setting) * 100f).ToString("F0", c) + "% (before " + (before10 * 100f).ToString("F1", c) +
                   "%), 40 in " + (Scattered(40f, 0f, setting) * 100f).ToString("F0", c) + "%; at a cloud's edge " +
                   (EdgeFor(setting) * 100f).ToString("F0", c) + "% of it; the direct beam's cap x" + CapFactor(setting).ToString("F1", c);
        }
    }
}
