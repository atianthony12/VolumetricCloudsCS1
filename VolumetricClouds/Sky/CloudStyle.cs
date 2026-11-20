using System;
using System.Globalization;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// The cloud styles (1.2; SOFT, 1.5.0: <see cref="CloudSoft"/>'s puffs with this style's light and
    /// layer; MIXED, 1.5.0: <see cref="CloudMix"/>, the same puffs and layer, mixed by a type pattern).
    /// CLASSIC is the mod's cloud layer as it has always been: the
    /// weather map thresholded into clouds, one thickness for all of them. CUMULUS adds HEAPS to that
    /// layer: EVE-Redux V5's cloud model (blackrack's KSP clouds, which the author asked to match "as
    /// close as" possible), every heap carved out of one big noise (<see cref="CumulusNoise3D"/>),
    /// each with its own height, a flat base and a billowing top -- and the Classic layer among and
    /// under them, its thickness following the map (the author's own idea, after his first look).
    /// Where both are, the denser wins. Classic's settings all shape the layer part.
    /// </summary>
    /// <remarks>
    /// Chosen on offline renders of this very formula (docs, "The EVE look" and after it; sheets
    /// eve-1-kerbal-look, eve-4-combined): the heaps alone have Classic's amount of cloud at every
    /// slider position (<see cref="EdgeTable"/>), the layer comes on top. What the heaps take from
    /// EVE V5, with EVE's numbers (NoiseWrapper.cs defaults and an Earth-like V5 config,
    /// PromisedWorlds' Gurdamma):
    /// <list type="bullet">
    /// <item>the noise: 8 octaves of spherical Worley, persistence 0.57, repeating every 2300 m;</item>
    /// <item>the shape: cg = saturate(c + coverageCurve(h) - 1), their cumulus coverage curve over
    /// <see cref="Span"/>; density = saturate((cg - (1 - noise) x 0.65) / (1 - 0.97));</item>
    /// <item>the light: single scattering 0.1 HG(0.95) + 0.2 HG(0.8), multiple scattering
    /// 3.0 HG(0.2) + 0.3 HG(-0.4) through a reduced extinction.</item>
    /// </list>
    /// Where it differs, from the renders: EVE paints its coverage (in-between values over tens of
    /// km); here it is our weather map, gently -- <see cref="EdgeCoverage"/> at the map's median,
    /// <see cref="CoverageSlope"/> more at its top tenth, a straight line in the map's normal
    /// scores, capped at <see cref="CoverageMax"/> -- so the noise makes every cloud and the map
    /// says where they gather (the map as a 0..1 coverage made cones: its Worley peaks are conical). Density 0.03 /m, 2.5 x EVE's, for crisp
    /// edges at a city camera's distances; EVE's density curve (thin bases) washed everything out
    /// and is left out; the multiple-scattering extinction is not public (0.5, by eye).
    ///
    /// Pure: no engine calls, so tools/test-cumulus.ps1 checks it offline, and the offline preview
    /// uses these numbers. The shader gets them from CloudShaderParams.ApplyStyle (the shape) and
    /// CloudVolume.ApplyDetailLight (the light).
    /// </remarks>
    public static class CloudStyle
    {
        // ---- the styles, stored by VALUE in VolumetricClouds.xml (append only) ----------------

        public const int Classic = 0;
        public const int Cumulus = 1;

        /// <summary>
        /// 1.5.0: the Soft style's puffs with the Classic layer coming in as the intensity rises, tipped by a
        /// type pattern, and (2026-11-06) the Cumulus style's heaps in a third of the sky (<see cref="CloudMix"/>).
        /// </summary>
        public const int Mixed = 2;

        /// <summary>
        /// 1.5.0: fair-weather clouds made of puffs, soft at the edges (<see cref="CloudSoft"/>), the
        /// Classic layer filling in at high intensity. The weather map gathers them, the Cumulus noise
        /// carves them (so, like Cumulus, drawn Classic the few seconds that noise takes to make). It
        /// takes the Cumulus style's light.
        /// </summary>
        public const int Soft = 3;

        /// <summary>
        /// The style follows the cover: Mixed's sky, mostly puffs in fair weather, the heaps spreading as the
        /// cover rises, Cumulus under a heavy sky (<see cref="CloudAuto"/>). Carved from the Cumulus noise and
        /// tipped by Mixed's type map, so drawn Classic the few seconds those take to make, and under Simpler
        /// clouds, which has neither.
        /// </summary>
        public const int Auto = 4;

        /// <summary>The setting's value (the default without one).</summary>
        public static int Current
        {
            get { return Settings.CloudStyle != null ? Settings.CloudStyle.value : Settings.Defaults.CloudStyle; }
        }

        public static bool IsCumulus
        {
            get { return Current == Cumulus; }
        }

        public static bool IsMixed
        {
            get { return Current == Mixed; }
        }

        /// <summary>The Soft style is chosen (drawn once its noise is there: <see cref="SoftDrawn"/>).</summary>
        public static bool IsSoft
        {
            get { return Current == Soft; }
        }

        /// <summary>The Auto style is chosen (drawn once its noise is there: <see cref="AutoDrawn"/>).</summary>
        public static bool IsAuto
        {
            get { return Current == Auto; }
        }

        /// <summary>
        /// The Auto style is drawn: chosen, the noise up and Mixed's type map baked, never under Simpler
        /// clouds. Then <see cref="TypeDrawn"/> says if the cover still asks for its mix (below it, the Cumulus
        /// style itself), and <see cref="HeapsDrawn"/> is true: what keys on those works as for the style alone.
        /// </summary>
        public static bool AutoDrawn
        {
            get { return IsAuto && MixedWanted && CloudMixMap.Ready; }
        }

        /// <summary>
        /// The sky is a mix of the three kinds, the type map saying which each stretch leans to: Mixed, or Auto
        /// below the cover it becomes Cumulus at. _CloudStyle 2.
        /// </summary>
        public static bool TypeDrawn
        {
            get { return MixedDrawn || (AutoDrawn && CloudAuto.PuffsAt(CloudWeather.Coverage)); }
        }

        /// <summary>The share of the sky that leans to the heaps while the type is drawn; 0 otherwise.</summary>
        public static float HeapSkyNow
        {
            get
            {
                if (!TypeDrawn)
                    return 0f;
                return AutoDrawn ? CloudAuto.HeapSky(CloudWeather.Coverage) : CloudMix.HeapSky;
            }
        }

        /// <summary>How strongly the type tips the sky: Mixed's fades at the top; Auto's is gone by being all heaps.</summary>
        public static float TypeStrengthNow
        {
            get { return AutoDrawn ? 1f : CloudMix.TypeStrength(CloudWeather.Coverage); }
        }

        /// <summary>
        /// The Soft style is drawn: chosen AND the Cumulus noise its puffs are carved from is there. Until
        /// then, Classic. Never under Simpler clouds (CloudShaders.Lite: its shaders have no puffs): Classic there.
        /// </summary>
        public static bool SoftDrawn
        {
            get { return IsSoft && Texture != null && !CloudShaders.Lite; }
        }

        /// <summary>
        /// The Soft style's puffs are drawn: Soft, Mixed or Auto below its Cumulus end. Everything they
        /// share keys on this -- SoftDensity's uniforms, the noise's repeat and mip level, the slab, the
        /// Classic layer beside the puffs.
        /// </summary>
        public static bool PuffsDrawn
        {
            get { return SoftDrawn || TypeDrawn; }
        }

        /// <summary>
        /// The Cumulus style's light is on: Cumulus, Mixed or Soft drawn. Its uniforms (CloudVolume's
        /// ApplyDetailLight), twice the steps and the noise's mip level from a pixel's footprint key on this.
        /// </summary>
        public static bool LobesLight
        {
            get { return HeapsDrawn || PuffsDrawn; }
        }

        /// <summary>A style carved from the Cumulus noise: Cumulus, Mixed, Soft (their puffs) or Auto.</summary>
        public static bool UsesNoise
        {
            get { return IsCumulus || IsMixed || IsSoft || IsAuto; }
        }

        /// <summary>
        /// The Cumulus noise, while a city is loaded (CloudVolume makes and uploads it the first
        /// time the style is wanted; null until then).
        /// </summary>
        public static UnityEngine.Texture3D Texture { get; set; }

        /// <summary>This city's Cumulus noise could not be made: the clouds are drawn Classic, and the log says why.</summary>
        public static bool Failed { get; set; }

        /// <summary>
        /// The Cumulus style's heaps are drawn: Cumulus, Mixed or Auto, AND the noise there (and for the last
        /// two the type map). Until then, Classic. The heaps' own uniforms, the layer's thickness following
        /// the map beside them.
        /// </summary>
        public static bool HeapsDrawn
        {
            get { return (IsCumulus && Texture != null) || MixedDrawn || AutoDrawn; }
        }

        /// <summary>
        /// How much the march's steps are the heaps': their hard edges need <see cref="StepFactor"/>. All of them
        /// under Cumulus and from a third of the sky up, less under Auto's few fair-weather stretches, none
        /// as Mixed's heaps go at the top. Its cost is why the puffs alone march the Quality's own steps.
        /// </summary>
        private static float HeapSteps
        {
            get
            {
                if (!PuffsDrawn)
                    return HeapsDrawn ? 1f : 0f;
                return Math.Min(1f, HeapSkyNow / CloudMix.HeapSky) * TypeStrengthNow;
            }
        }

        /// <summary>The march's steps, times the Quality setting's: <see cref="StepFactor"/> under the heaps (<see cref="HeapSteps"/>).</summary>
        public static float StepScale
        {
            get { return CloudAuto.Mix(1f, StepFactor, HeapSteps); }
        }

        /// <summary>
        /// The repeat, metres, the Cumulus noise's mip levels are worked out for: the heaps' (Cumulus size), the
        /// puffs', or by their shares of the sky under the type (they read one `lod`; Cumulus size only moves it
        /// a level at most). Each lookup keeps its own repeat.
        /// </summary>
        public static float NoiseLodTile
        {
            get
            {
                if (PuffsDrawn && HeapsDrawn)
                    return CloudAuto.Mix(SoftLook.NoiseTile, NoiseTileNow, HeapSkyNow);
                return HeapsDrawn ? NoiseTileNow : SoftLook.NoiseTile;
            }
        }

        /// <summary>
        /// The height the Cumulus light measures its sky light and its sun's looks over: the puffs' tallest, the
        /// heaps' curve's span, or by their shares of the sky under the type.
        /// </summary>
        public static float LightSpan
        {
            get
            {
                if (PuffsDrawn && HeapsDrawn)
                    return CloudAuto.Mix(SoftTallestNow, SpanNow, HeapSkyNow);
                return PuffsDrawn ? SoftTallestNow : SpanNow;
            }
        }

        /// <summary>
        /// Metres above the base the march must reach for the heaps under the type: as far as the tallest heap
        /// gets at the heaps' coverage now (<see cref="HeapTopAt"/>), under Auto rising to <see cref="TallestNow"/>
        /// as the puffs go, so the slab does not jump when the style becomes Cumulus. Never clips a heap.
        /// </summary>
        public static float TypedHeapTop
        {
            get
            {
                float cover = CloudWeather.Coverage;
                float a, b;
                Line(HeapEdge(HeapCoverage(cover)), out a, out b);
                // Cloud variety (2026-11-19): the towers stretched over that (itself with the row off).
                return VarietyTop(CloudAuto.Mix(HeapTopAt(a + b - LineTrimNow) * SizeFactor * HeightFactor, TallestNow, AutoDrawn ? CloudAuto.PuffFade(cover) : 0f));
            }
        }

        /// <summary>The Mixed style is drawn: chosen, the noise up and its type map baked (CloudMixMap).</summary>
        public static bool MixedDrawn
        {
            get { return IsMixed && MixedWanted && CloudMixMap.Ready; }
        }

        /// <summary>
        /// Mixed or Auto is chosen and the noise is up: the type map should be baked. Never under Simpler
        /// clouds (CloudShaders.Lite): no map, Classic drawn.
        /// </summary>
        public static bool MixedWanted
        {
            get { return (IsMixed || IsAuto) && Texture != null && !CloudMixMap.Failed && !CloudShaders.Lite; }
        }

        /// <summary>
        /// The cover the rain, lightning's cloud test and the rainbow's sunlit-rain test are cut at, so
        /// rain always falls from drawn cloud (invariant 2): under Mixed the SMALLEST cover its layer is
        /// drawn at anywhere; under Soft the larger of its puffs' and its layer's cover, never more than
        /// the cover in effect (CloudSoft.DrawnCoverage: the cover itself with the Classic layer row at
        /// 100%); under Auto CloudAuto.DrawnCoverage, the least any stretch of its sky draws. Classic and
        /// Cumulus: the cover in effect.
        /// </summary>
        public static float CloudTestCoverage(float coverage)
        {
            if (AutoDrawn)
                return CloudAuto.DrawnCoverage(SoftLook, coverage, LayerAmount);
            if (MixedDrawn)
                return MixedLayerInCumulusArea(coverage);
            return SoftDrawn ? CloudSoft.DrawnCoverage(SoftLook, coverage, LayerAmount) : coverage;
        }

        /// <summary>
        /// How much of the Classic layer Cumulus mixes in, 0..1 ("Classic layer", 1.3.0): the
        /// layer is cut at this share of the cover (CloudShaderParams.LayerCoverage), so less of it
        /// is fewer and smaller pieces of layer, never a paler one. 1 = as in 1.2. Under Mixed and Soft
        /// (1.5.0) the same share of their layer's cover, in every stretch of sky.
        /// </summary>
        public static float LayerAmount
        {
            get { return Math.Max(0f, Math.Min(1f, Settings.CumulusLayer != null ? Settings.CumulusLayer.value : Settings.Defaults.CumulusLayer)); }
        }

        /// <summary>Mixed's layer cover at this intensity (CloudMix.LayerCover), times <see cref="LayerAmount"/>.</summary>
        public static float MixedLayer(float coverage)
        {
            return CloudMix.LayerCover(coverage) * LayerAmount;
        }

        /// <summary>The same where the type leans to layer: the most anywhere (the empty-sky skip tests against it).</summary>
        public static float MixedLayerInLayerArea(float coverage)
        {
            return CloudMix.LayerCoverInLayerArea(coverage) * LayerAmount;
        }

        /// <summary>The same where the type leans to puffs: the least anywhere (the rain is cut at it).</summary>
        public static float MixedLayerInCumulusArea(float coverage)
        {
            return CloudMix.LayerCoverInCumulusArea(coverage) * LayerAmount;
        }

        // ---- the cumulus rows (1.5.0): the heaps' ------------------------------------------------

        /// <summary>Cumulus size, 0.5..2: the heaps' noise and height.</summary>
        public static float SizeFactor
        {
            get { return Clamp(Settings.CumulusSize != null ? Settings.CumulusSize.value : Settings.Defaults.CumulusSize, 0.5f, 2f); }
        }

        /// <summary>Cumulus height, 0.5..2: their height alone.</summary>
        public static float HeightFactor
        {
            get { return Clamp(Settings.CumulusHeight != null ? Settings.CumulusHeight.value : Settings.Defaults.CumulusHeight, 0.5f, 2f); }
        }

        /// <summary>Cumulus amount, 0..2: how many at a given intensity.</summary>
        public static float AmountFactor
        {
            get { return Clamp(Settings.CumulusAmount != null ? Settings.CumulusAmount.value : Settings.Defaults.CumulusAmount, 0f, 2f); }
        }

        /// <summary>The heaps' noise repeat, metres: <see cref="NoiseTile"/> times Cumulus size.</summary>
        public static float NoiseTileNow
        {
            get { return NoiseTile * SizeFactor; }
        }

        /// <summary>The heaps' coverage curve's span, metres: <see cref="Span"/> times Cumulus size and height.</summary>
        public static float SpanNow
        {
            get { return Span * SizeFactor * HeightFactor; }
        }

        /// <summary>The tallest heap now, metres above the base (with Rounded cumulus bases, its curve's: <see cref="ShapeNow"/>).</summary>
        public static float TallestNow
        {
            get { return ShapeNow.Tallest * SizeFactor * HeightFactor; }
        }

        /// <summary>
        /// The intensity the heaps are cut at: the cover in effect times Cumulus amount (0: none; 2: as
        /// many as twice the intensity would give, up to <see cref="HeapCap"/>'s). Under Mixed in the
        /// stretches that lean to them, going with the type at the top: its 100% is the deck it always was.
        /// </summary>
        public static float HeapCoverage(float coverage)
        {
            float heaps = Math.Min(1f, coverage * AmountFactor);
            return MixedDrawn ? heaps * CloudMix.TypeStrength(coverage) : heaps;
        }

        private static float Clamp(float v, float lo, float hi)
        {
            return Math.Max(lo, Math.Min(hi, v));
        }

        // ---- the puffs: Soft and Mixed (1.5.0) ---------------------------------------------------

        /// <summary>The puffs' numbers, Soft's and Mixed's: <see cref="CloudSoft.Default"/> (one place for options later).</summary>
        public static CloudSoft.Look SoftLook
        {
            get { return CloudSoft.Default; }
        }

        /// <summary>
        /// What the clouds' light is times, less 1, where the sky leans all the way to the heaps (the shader's
        /// _MixHeaps.w): their level is 1, the rest of the sky's <see cref="CloudLightScale"/>. Only under the type.
        /// </summary>
        public static float HeapLightBoost
        {
            get
            {
                if (!TypeDrawn)
                    return 0f;
                // "Brightness difference between clouds" at 100% (or where it does nothing): exactly as before it.
                if (EvenRow >= 1f)
                    return CloudMix.LightBoost(CloudLightScale, TypeStrengthNow);
                return CloudMix.LightBoost(CloudLightScale, TypeStrengthNow, HeapLevel);
            }
        }

        // ---- "Brightness difference between clouds" (CloudLightDifference, 2026-11-16) ---------------
        // Under the type (Mixed, Auto below Cumulus) the sky is lit at the puffs' level (LevelToday, about x0.5)
        // and lifted back to 1 where it leans to heaps (HeapLightBoost): a sunlit soft cloud in a stretch that
        // does not lean to heaps got half a heap's light (measured 2026-11-16 at his settings, two cities: a sunlit
        // heap 1.9x a sunlit soft cloud's light per opacity there, at Glow 100 and 150 alike; 1.66x / 1.82x over
        // all their opaque pixels, shade included). The row at 100% (its first default) is that sky exactly; at 0% the
        // two levels are EVEN: a sunlit soft cloud there as bright per opacity as a sunlit heap. Evened from both
        // sides, so the whole sky keeps about its brightness: the soft stretches up and the heaps' level down from
        // 1, by how much of the sky's cloud light the soft stretches carry at the cover (EvenSoftShare). The
        // numbers are fields so the bench can try them.

        /// <summary>
        /// A sunlit heap's light per opacity per unit of light level, as a share of a sunlit soft cloud's: the
        /// level at which the soft stretches are as bright as heaps lit at 1. Measured in the Unity 5.6 bench
        /// (his settings, his city and a second one, 24 views each): 0.965 at Glow 100, 0.945 at 150.
        /// </summary>
        public static float EvenHeapLight = 0.955f;

        /// <summary>
        /// Under Auto, how much of the sky's cloud light the soft stretches carry, by cover: all of it at
        /// <see cref="EvenShareFrom"/>, none from <see cref="EvenShareTo"/>, a smoothstep between: 33% at 43% cover,
        /// 4% at 58%, 0 at 68% (measured, his settings, light-weighted over 8 views: 16% and 43% at 43% in the two
        /// cities, 2% and 6% at 58%, under 1% at 68%; the two cities' 43% evened out between them). It decides how
        /// far the heaps' level comes down (EvenLevels).
        /// </summary>
        public static float EvenShareFrom = 0.085f, EvenShareTo = 0.64f;

        /// <summary>The same under Mixed, at every cover (measured: 4% in his city, 16-19% in the second, 43-68%).</summary>
        public static float EvenShareMixed = 0.1f;

        /// <summary>The row's effect fades out over this cover, so the sky meets Cumulus's light as before (none from EvenFadeTo).</summary>
        public static float EvenFadeFrom = 0.8f, EvenFadeTo = 0.88f;

        /// <summary>The row, 0..1 (1: the sky as before it).</summary>
        public static float LightDifference
        {
            get { return Clamp(Settings.CloudLightDifference != null ? Settings.CloudLightDifference.value : Settings.Defaults.CloudLightDifference, 0f, 1f); }
        }

        /// <summary>
        /// The row as it acts now: 1 (nothing) unless the type is drawn, faded to 1 over EvenFadeFrom..EvenFadeTo
        /// of cover, 1 exactly from there.
        /// </summary>
        private static float EvenRow
        {
            get
            {
                if (!TypeDrawn)
                    return 1f;
                float row = LightDifference;
                float cover = CloudWeather.Coverage;
                if (row >= 1f || cover >= EvenFadeTo)
                    return 1f;
                float t = Clamp((cover - EvenFadeFrom) / Math.Max(1e-3f, EvenFadeTo - EvenFadeFrom), 0f, 1f);
                return row + (1f - row) * t * t * (3f - 2f * t);
            }
        }

        /// <summary>The share of the sky's cloud light in the soft stretches at this cover, 0..1 (EvenShareFrom..To).</summary>
        private static float EvenSoftShare
        {
            get
            {
                if (!AutoDrawn)
                    return Clamp(EvenShareMixed, 0f, 1f);
                float t = Clamp((CloudWeather.Coverage - EvenShareFrom) / Math.Max(1e-3f, EvenShareTo - EvenShareFrom), 0f, 1f);
                return 1f - t * t * (3f - 2f * t);
            }
        }

        /// <summary>
        /// The two levels at the row's 0% for today's soft level <paramref name="today"/>: even (soft = heaps' level
        /// x EvenHeapLight), with the sky's mean light kept: share s of the light in the soft stretches (which go
        /// x soft / today), 1 - s on the heaps' (x heap), so s soft / today + (1 - s) heap = 1, i.e.
        /// heap = 1 / (1 + s (EvenHeapLight / today - 1)). The soft level never under today's nor over 1; where the
        /// soft clouds are already as bright (today at or over EvenHeapLight), nothing moves.
        /// </summary>
        private static void EvenLevels(float today, out float soft, out float heap)
        {
            float q = Clamp(EvenHeapLight, 1e-3f, 1f);
            if (today >= q)
            {
                soft = today;
                heap = 1f;
                return;
            }
            heap = 1f / (1f + EvenSoftShare * (q / Math.Max(1e-3f, today) - 1f));
            soft = Clamp(q * heap, today, 1f);
        }

        /// <summary>The heaps' level under the type, where the sky leans all the way to them: 1 at the row's 100%.</summary>
        public static float HeapLevel
        {
            get
            {
                float row = EvenRow;
                if (row >= 1f)
                    return 1f;
                float soft, heap;
                EvenLevels(LevelToday, out soft, out heap);
                return Math.Max(CloudLightScale, CloudAuto.Mix(heap, 1f, row));
            }
        }

        /// <summary>
        /// How much the Classic layer carries the sky's light now under the style drawn (Soft:
        /// CloudSoft.LightShare; Mixed: CloudMix.LightShare), times the "Classic layer" row; 0 under the
        /// others. The clouds' light level follows it (CloudSoft.LightScaleAt).
        /// </summary>
        public static float PuffLightShare(float coverage)
        {
            if (SoftDrawn)
                return CloudSoft.LightShare(SoftLook, coverage, LayerAmount);
            return MixedDrawn ? CloudMix.LightShare(coverage) * LayerAmount : 0f;
        }

        /// <summary>
        /// The clouds' own light level (their sun and sky light, CloudVolume.ApplyLighting): while the
        /// puffs are drawn, theirs where they carry the sky and the layer's where it does
        /// (CloudSoft.LightScaleAt), under Auto its own (CloudAuto.LightScale), 1 for every other style.
        /// The heaps' stretches under the type are lifted from it to 1 (<see cref="HeapLightBoost"/>). What
        /// reads those two lights back for something else (the distant clouds, SkyLayer) divides it out again.
        /// Under the type, "Brightness difference between clouds" below 100% moves it (and the heaps' level,
        /// <see cref="HeapLevel"/>) towards the even levels (EvenLevels).
        /// </summary>
        public static float CloudLightScale
        {
            get
            {
                float today = LevelToday;
                // "Brightness difference between clouds" (under the type only): from today's level at 100% to the
                // even one at 0% (EvenLevels).
                float row = EvenRow;
                if (row >= 1f)
                    return today;
                float soft, heap;
                EvenLevels(today, out soft, out heap);
                return CloudAuto.Mix(soft, today, row);
            }
        }

        /// <summary>The clouds' own light level as the styles set it, before "Brightness difference between clouds".</summary>
        private static float LevelToday
        {
            get
            {
                if (AutoDrawn)
                    return Math.Max(0f, CloudAuto.LightScale(SoftLook, CloudWeather.Coverage, LayerAmount));
                return PuffsDrawn ? Math.Max(0f, CloudSoft.LightScaleAt(SoftLook, PuffLightShare(CloudWeather.Coverage))) : 1f;
            }
        }

        /// <summary>The puffs' tallest cloud now, metres above the base: "Layer thickness" times its factor.</summary>
        public static float SoftTallestNow
        {
            get { return CloudSoft.Tallest(SoftLook, Settings.CloudThickness != null ? Settings.CloudThickness.value : Settings.Defaults.Thickness); }
        }

        /// <summary>
        /// The noise's mip chain as Color32 (the value in every channel; the shader reads alpha),
        /// for the city's noise seed. Worker thread: Color32 is a plain struct. Null on failure,
        /// with the reason in <paramref name="error"/>.
        /// </summary>
        public static UnityEngine.Color32[][] BuildLevels(int noiseSeed, out string error, out int milliseconds)
        {
            error = null;
            milliseconds = 0;
            try
            {
                DateTime start = DateTime.UtcNow;
                byte[][] levels = CumulusNoise3D.Generate(CumulusNoise3D.Size, CumulusNoise3D.SeedFor(noiseSeed), 0);
                var colours = new UnityEngine.Color32[levels.Length][];
                for (int l = 0; l < levels.Length; l++)
                {
                    byte[] src = levels[l];
                    var dst = new UnityEngine.Color32[src.Length];
                    for (int i = 0; i < src.Length; i++)
                    {
                        byte v = src[i];
                        dst[i] = new UnityEngine.Color32(v, v, v, v);
                    }
                    colours[l] = dst;
                }

                milliseconds = (int)(DateTime.UtcNow - start).TotalMilliseconds;
                return colours;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }

        // ---- the shape ------------------------------------------------------------------------

        /// <summary>Metres per repeat of the noise: EVE's baseNoiseTiling in their Earth-like config.</summary>
        public const float NoiseTile = 2300f;

        /// <summary>How deep the noise carves into the cloud's body (EVE's erosionDepth).</summary>
        public const float ErosionDepth = 0.65f;

        /// <summary>EVE's noiseEdgeHardness: the density is full 3% of the way in.</summary>
        public const float Hardness = 0.97f;

        /// <summary>Metres over which the coverage curve runs, base to top.</summary>
        public const float Span = 2000f;

        /// <summary>
        /// Coverage gained from the map's median to its top tenth, measured on the map's NORMAL
        /// SCORES (CloudDensityField.ScoreTexture: each texel's rank as a standard normal z), so
        /// the same share of every city's map gets each coverage. Measured from the raw values
        /// (the first build), one city's map sat lower than another's and its sky was twice as
        /// cloudy at the same slider.
        /// </summary>
        public const float CoverageSlope = 0.155f;

        /// <summary>The normal score a tenth of a map lies above.</summary>
        public const float TopTenthScore = 1.2815516f;

        /// <summary>
        /// The most coverage anywhere: EVE's "in-between" regime, where the noise shapes the cloud.
        /// It also sets the tallest cloud (<see cref="Tallest"/>).
        /// </summary>
        public const float CoverageMax = 0.45f;

        /// <summary>Extinction per metre at full density, at the default Density.</summary>
        public const float BaseExtinction = 0.03f;

        /// <summary>
        /// The raymarch takes this many times the Quality setting's steps under Cumulus (the shader's
        /// loop allows 192 = 96 x 2). Its slab is ~2.8x Classic's height (the tallest heap, 1.1 km),
        /// so with the same count every step skipped over the heaps' hard edges and the jitter showed
        /// as grain (the author, 2026-09-26: "small dots everywhere"). On the offline preview twice
        /// the steps with blue-noise jitter (<see cref="BlueNoise"/>) came close to four times.
        /// </summary>
        public const float StepFactor = 2f;

        // EVE's cumulus coverage curve (Gurdamma, type Cumulus): Unity float-curve keys, time =
        // height over Span, value = coverage, in/out tangents per unit of time.
        public static readonly float[] CurveTime = { 0.01728953f, 0.08695792f, 0.9732781f };
        public static readonly float[] CurveValue = { -0.004032524f, 0.976166f, -0.006797761f };
        public static readonly float[] CurveIn = { 1.633945f, 0.06669102f, -0.7966601f };
        public static readonly float[] CurveOut = { 1.633945f, 0.04846065f, -0.5550035f };

        /// <summary>
        /// The curve at height h (0 = base, 1 = Span up), as Unity evaluates a float curve:
        /// Hermite between keys, the end values outside them.
        /// </summary>
        public static float Curve(float h)
        {
            int n = CurveTime.Length;
            if (h <= CurveTime[0])
                return CurveValue[0];
            if (h >= CurveTime[n - 1])
                return CurveValue[n - 1];

            int i = 1;
            while (i < n - 1 && h > CurveTime[i])
                i++;

            float[] c = SegmentCubic(i - 1);
            float s = (h - CurveTime[i - 1]) / (CurveTime[i] - CurveTime[i - 1]);
            return ((c[0] * s + c[1]) * s + c[2]) * s + c[3];
        }

        /// <summary>
        /// Segment k (key k to key k + 1) as a cubic in s = 0..1 across it: a s^3 + b s^2 + c s + d.
        /// What the shader evaluates, so the two cannot disagree.
        /// </summary>
        public static float[] SegmentCubic(int k)
        {
            float dt = CurveTime[k + 1] - CurveTime[k];
            float v0 = CurveValue[k], v1 = CurveValue[k + 1];
            float m0 = CurveOut[k] * dt, m1 = CurveIn[k + 1] * dt;
            return new[]
            {
                2f * v0 + m0 - 2f * v1 + m1,
                -3f * v0 - 2f * m0 + 3f * v1 - m1,
                m0,
                v0,
            };
        }

        /// <summary>
        /// Metres above the base where the tallest cloud ends: above it c + curve - 1 is below 0
        /// even at <see cref="CoverageMax"/>, so the march can stop there.
        /// </summary>
        public static readonly float Tallest = ComputeTallest();

        private static float ComputeTallest()
        {
            return TopOf(CoverageMax);
        }

        /// <summary>Metres over <see cref="Span"/> where a heap of this coverage ends (at Cumulus size and height 100%).</summary>
        private static float TopOf(float coverage)
        {
            // The curve falls from its peak key to 0 at the last one; bisect where it crosses
            // 1 - coverage on the way down.
            float target = 1f - coverage;
            float lo = CurveTime[1], hi = CurveTime[CurveTime.Length - 1];
            for (int k = 0; k < 40; k++)
            {
                float mid = (lo + hi) * 0.5f;
                if (Curve(mid) > target)
                    lo = mid;
                else
                    hi = mid;
            }

            return hi * Span;
        }

        /// <summary><see cref="TopOf"/> per 1% of coverage, 0 .. CoverageMax: asked for several times a frame, and <see cref="Curve"/> allocates.</summary>
        private static readonly float[] Tops = ComputeTops();

        private static float[] ComputeTops()
        {
            int n = (int)Math.Round(CoverageMax * 100f) + 1;
            var tops = new float[n];
            for (int i = 0; i < n; i++)
                tops[i] = TopOf(Math.Min(i * 0.01f, CoverageMax));
            return tops;
        }

        /// <summary>
        /// Metres above the base where the tallest heap of this coverage can end (at Cumulus size and height
        /// 100%), rounded up to the next whole percent, so never under it: <see cref="Tallest"/> from
        /// <see cref="CoverageMax"/> up, the first key's height from none.
        /// </summary>
        public static float HeapTopAt(float coverage)
        {
            // The curve drawn now (Rounded cumulus bases): EVE's Tops with the row at 0.
            float[] tops = ShapeNow.Tops;
            int i = (int)Math.Ceiling(Math.Max(0f, coverage) * 100f);
            return tops[Math.Min(i, tops.Length - 1)];
        }

        // ---- how much cloud -------------------------------------------------------------------

        /// <summary>
        /// The heaps' coverage at the weather map's median, one per 5% of the Intensity slider
        /// (0%, 5% .. 100%): solved offline (docs/previews/render-detail.ps1 -Calibrate, several
        /// cities' seeds averaged) so that the heaps ALONE cover the share of the sky Classic does at
        /// Classic's default settings (the author's first pick, "so people don't get surprised").
        /// With the layer beside them the sky has somewhat more (his later pick: "maybe 5", the
        /// layer at the full slider): at 83%, 25% of the sky where Classic has 17%.
        /// </summary>
        public static readonly float[] EdgeTable =
        {
            // Solved 2026-09-26 on the normal scores, over 2 x 2 repeats of each map, for six
            // cities' seeds (field/noise 12345/67890, 23456/78901, 34567/89012, 45678/90123,
            // 56789/11223, 67891/22334) and averaged. Classic's own amount differs from city to
            // city (at 95%: 25% to 49% of the sky), so a single city's Cumulus sky is "about"
            // Classic's, not exactly.
            -0.3000f, -0.1274f, -0.0789f, -0.0532f, -0.0366f, -0.0244f, -0.0136f, -0.0030f, 0.0081f, 0.0191f,
            0.0301f, 0.0416f, 0.0542f, 0.0682f, 0.0820f, 0.0969f, 0.1139f, 0.1325f, 0.1555f, 0.1942f,
            0.3648f,
        };

        /// <summary>
        /// Above this Intensity the heaps stop multiplying and the Classic layer alone fills the sky
        /// in. At 100% the heaps alone covered the whole map, and their noise's 2.3 km repeat was
        /// all there was to see (the author: "the pattern starts to show"); a few heaps over the
        /// layer have no repeat to see.
        /// </summary>
        public const float HeapCap = 0.85f;

        /// <summary>The heaps' coverage at the map's median at this Intensity (capped at <see cref="HeapCap"/>).</summary>
        public static float HeapEdge(float coverage)
        {
            return EdgeCoverage(Math.Min(coverage, HeapCap));
        }

        /// <summary>
        /// THE LAYER under the heaps (the author: "Maybe we can combine the classic and cumulus
        /// clouds for a more natural result?"): Classic's own layer at the slider's Intensity, its
        /// thickness x (1 + this x the map's normal score), within <see cref="LayerThinnest"/> ..
        /// <see cref="LayerThickest"/>: thick where the map is high. A layer filled to a closed lid
        /// (tried: "the pattern repetition is super obvious") is one even carpet of the same puffs;
        /// its own gaps and this variety over kilometres are what keep 100% natural.
        /// </summary>
        public const float LayerThicknessVariety = 0.35f;
        public const float LayerThinnest = 0.25f, LayerThickest = 1.75f;

        /// <summary>
        /// The layer's thickness factor as a line in the score texture's value t, a + b x t (the
        /// shader then keeps it within LayerThinnest .. LayerThickest). Returns { a, b }.
        /// </summary>
        public static float[] LayerLine()
        {
            float range = CloudDensityField.ScoreRange;
            return new[] { 1f - LayerThicknessVariety * range, 2f * LayerThicknessVariety * range };
        }

        /// <summary>The layer's thickness factor at a score texel value (see <see cref="LayerLine"/>).</summary>
        public static float LayerFactor(float scoreTexel)
        {
            float[] line = LayerLine();
            return Math.Max(LayerThinnest, Math.Min(LayerThickest, line[0] + line[1] * scoreTexel));
        }

        /// <summary>The layer's thickness factor as a line in the score texture's value, cached: set every frame.</summary>
        public static readonly float[] LayerLineCached = LayerLine();

        /// <summary>The coverage at the map's median at this Intensity (0..1).</summary>
        public static float EdgeCoverage(float coverage)
        {
            float x = Math.Max(0f, Math.Min(1f, coverage)) * (EdgeTable.Length - 1);
            int i = Math.Min((int)x, EdgeTable.Length - 2);
            float f = x - i;
            return EdgeTable[i] + (EdgeTable[i + 1] - EdgeTable[i]) * f;
        }

        /// <summary>
        /// The shader's coverage as a straight line in the score texture's value t (0..1, the
        /// normal score z = (2t - 1) x ScoreRange): <paramref name="edge"/> at the map's median
        /// (z = 0), <see cref="CoverageSlope"/> more at its top tenth (z = 1.28), within
        /// 0..<see cref="CoverageMax"/> in the shader. Returns { a, b } for a + b x t.
        /// </summary>
        public static float[] Line(float edge)
        {
            float a, b;
            Line(edge, out a, out b);
            return new[] { a, b };
        }

        /// <summary>The same, without an allocation: the shader parameters ask for it every frame.</summary>
        public static void Line(float edge, out float a, out float b)
        {
            float range = CloudDensityField.ScoreRange;
            float perScore = CoverageSlope / TopTenthScore;
            a = edge - perScore * range;
            b = perScore * 2f * range;
        }

        /// <summary>Both segments' cubics, worked out once (the shader parameters are set every frame).</summary>
        public static readonly float[][] Cubics = { SegmentCubic(0), SegmentCubic(1) };

        /// <summary>The shader's coverage at a score texel value (see <see cref="Line"/>).</summary>
        public static float CoverageAt(float scoreTexel, float edge)
        {
            float[] line = Line(edge);
            return Math.Max(0f, Math.Min(CoverageMax, line[0] + line[1] * scoreTexel));
        }

        /// <summary>
        /// The density at one point, 0..1, from the coverage, the height over <see cref="Span"/> and
        /// the noise there: the shader's formula, for tests and the preview.
        /// </summary>
        public static float Density(float coverage, float h, float noise)
        {
            float cg = Math.Max(0f, Math.Min(1f, coverage + Curve(h) - 1f));
            if (cg <= 0f)
                return 0f;

            float d = (cg - (1f - noise) * ErosionDepth) / (1f - Hardness);
            return Math.Max(0f, Math.Min(1f, d));
        }

        // ---- rounded bases (2026-11-16, the row "Rounded cumulus bases", CumulusBases) -------------

        /// <summary>
        /// The heaps' undersides, formed like their tops. EVE's curve climbs from nothing to its peak over the
        /// lowest 7% of the span and falls over the rest: the noise could move a heap's underside by some 70 m
        /// while it moved its top by a kilometre, so every heap sat on one flat line, widest right at the
        /// bottom. With the row on, the climb is slower and rounded (fast low down, easing into the peak), and
        /// the widest level higher (<see cref="BaseWidest"/>); the peak's value and the fall's shape are EVE's.
        /// The noise then shapes the undersides through the coverage as it shapes the tops: nothing is cut or
        /// moved (the two tries of 2026-10-05 did: "it's like bending the cloud rather than naturally forming").
        /// 0 = EVE's curve exactly (the same numbers reach the shader). Static fields so the bench can sweep them.
        /// Chosen 2026-11-16 in the Unity 5.6 bench: widest at 15% of the span
        /// (20% made every heap visibly bigger and taller), the undersides formed over ~2.6x EVE's height
        /// (the coverage of an underside crossed over 132 m of the span where EVE's crossed it over 51 m).
        /// </summary>
        public static float BaseWidest = 0.15f;

        /// <summary>The climb's slope at its foot at 100%, in units of its rise per its length: 2 = a parabola (1 - (1 - s)^2), 3 at most (monotone).</summary>
        public static float BaseRise = 2f;

        /// <summary>How much of the widest level's move the fall's end follows: 1 = the tops ride up with it, their shape EVE's; 0 = they stay where they end and the fall is squeezed (chosen: the heaps keep their heights).</summary>
        public static float BaseTopShift = 0f;

        /// <summary>
        /// Coverage taken off the heaps' line at 100%: a thicker band near the peak makes each heap's footprint
        /// from below a little wider (the noise differs with height: +2 to +4 points of sky on one city's map),
        /// and the cover tables of Soft, Auto and the rain are solved against Cumulus's sky. This keeps that
        /// sky's amount (measured from straight above on two cities: within a point of the row at 0).
        /// </summary>
        public static float BaseTrim = 0.015f;

        /// <summary>The coverage taken off the heaps' line now (<see cref="BaseTrim"/> x the row as drawn).</summary>
        public static float BaseTrimNow
        {
            get { return BaseTrim * ShapeNow.Bases; }
        }

        /// <summary>The curve as the shader evaluates it: three keys, two cubics in s, the value past the last key; with its tops.</summary>
        public sealed class Shape
        {
            public float Bases;
            public float[] Time;
            public float[][] Cubics;
            public float End;
            /// <summary>Metres above the base where a heap of the most coverage ends (at Cumulus size and height 100%).</summary>
            public float Tallest;
            /// <summary>The same per 1% of coverage, 0 .. CoverageMax.</summary>
            public float[] Tops;

            /// <summary>The curve at h (0 = base, 1 = Span up), exactly as CumulusCurve in the shader.</summary>
            public float Eval(float h)
            {
                if (h <= Time[0])
                    return Cubics[0][3];
                if (h >= Time[2])
                    return End;
                bool first = h < Time[1];
                float[] k = first ? Cubics[0] : Cubics[1];
                float start = first ? Time[0] : Time[1];
                float end = first ? Time[1] : Time[2];
                float s = (h - start) / (end - start);
                return ((k[0] * s + k[1]) * s + k[2]) * s + k[3];
            }

            /// <summary>Metres over Span where a heap of this coverage ends: where the fall crosses 1 - coverage.</summary>
            public float TopOf(float coverage)
            {
                float target = 1f - coverage;
                float lo = Time[1], hi = Time[2];
                for (int k = 0; k < 40; k++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (Eval(mid) > target)
                        lo = mid;
                    else
                        hi = mid;
                }

                return hi * Span;
            }
        }

        /// <summary>EVE's curve, the row at 0: the very arrays and cubics the shader always got.</summary>
        public static readonly Shape EveShape = new Shape
        {
            Bases = 0f,
            Time = CurveTime,
            Cubics = Cubics,
            End = CurveValue[CurveValue.Length - 1],
            Tallest = Tallest,
            Tops = Tops,
        };

        /// <summary>A cubic in s from its ends' values and slopes (per unit of s), as <see cref="SegmentCubic"/>.</summary>
        private static float[] Hermite(float v0, float v1, float m0, float m1)
        {
            return new[] { 2f * v0 + m0 - 2f * v1 + m1, -3f * v0 - 2f * m0 + 3f * v1 - m1, m0, v0 };
        }

        /// <summary>The curve with the row at <paramref name="bases"/> (0..1): EVE's at 0 (the same object).</summary>
        public static Shape ShapeFor(float bases)
        {
            bases = Math.Max(0f, Math.Min(1f, bases));
            if (bases <= 0f)
                return EveShape;

            float t0 = CurveTime[0], t1Eve = CurveTime[1], t2Eve = CurveTime[2];
            float t1 = t1Eve + (Math.Max(BaseWidest, t1Eve) - t1Eve) * bases;
            float t2 = t2Eve + (t1 - t1Eve) * Math.Max(0f, Math.Min(1f, BaseTopShift));
            float v0 = CurveValue[0], v1 = CurveValue[1], v2 = CurveValue[2];

            // The climb: EVE's slopes (per unit of s) handed over to a rounded rise, steep at the foot, flat at the peak.
            float dt0 = t1Eve - t0;
            float m0Eve = CurveOut[0] * dt0, m1Eve = CurveIn[1] * dt0;
            float rise = Math.Max(0f, Math.Min(3f, BaseRise)) * (v1 - v0);
            float m0 = m0Eve + (rise - m0Eve) * bases;
            float m1 = m1Eve * (1f - bases);

            // The fall: EVE's shape in s, over its new stretch.
            float dt1 = t2Eve - t1Eve;
            float n0 = CurveOut[1] * dt1, n1 = CurveIn[2] * dt1;

            var shape = new Shape
            {
                Bases = bases,
                Time = new[] { t0, t1, t2 },
                Cubics = new[] { Hermite(v0, v1, m0, m1), Hermite(v1, v2, n0, n1) },
                End = v2,
            };
            int n = (int)Math.Round(CoverageMax * 100f) + 1;
            shape.Tops = new float[n];
            for (int i = 0; i < n; i++)
                shape.Tops[i] = shape.TopOf(Math.Min(i * 0.01f, CoverageMax));
            shape.Tallest = shape.TopOf(CoverageMax);
            return shape;
        }

        /// <summary>The "Rounded cumulus bases" row, 0..1 (its default without one).</summary>
        public static float BasesRow
        {
            get { return Clamp(Settings.CumulusBases != null ? Settings.CumulusBases.value : Settings.Defaults.CumulusBases, 0f, 1f); }
        }

        /// <summary>The row as drawn: none under Simpler clouds (its shaders keep EVE's base: the C# side sends EVE's curve).</summary>
        public static float BasesNow
        {
            get
            {
                float row = BasesRow;
                return row > 0f && !CloudShaders.Lite ? row : 0f;
            }
        }

        private static Shape _shape = EveShape;
        private static float _shapeWidest = float.NaN, _shapeRise = float.NaN, _shapeShift = float.NaN;

        /// <summary>The curve drawn now (<see cref="BasesNow"/>): what the shader gets, the tops and the slab.</summary>
        public static Shape ShapeNow
        {
            get
            {
                float bases = BasesNow;
                if (bases != _shape.Bases || (bases > 0f && (BaseWidest != _shapeWidest || BaseRise != _shapeRise || BaseTopShift != _shapeShift)))
                {
                    _shape = ShapeFor(bases);
                    _shapeWidest = BaseWidest;
                    _shapeRise = BaseRise;
                    _shapeShift = BaseTopShift;
                }

                return _shape;
            }
        }

        /// <summary>The density at one point with the row at <paramref name="bases"/>: <see cref="Density(float, float, float)"/> on that curve.</summary>
        public static float Density(float coverage, float h, float noise, float bases)
        {
            float cg = Math.Max(0f, Math.Min(1f, coverage + ShapeFor(bases).Eval(h) - 1f));
            if (cg <= 0f)
                return 0f;

            float d = (cg - (1f - noise) * ErosionDepth) / (1f - Hardness);
            return Math.Max(0f, Math.Min(1f, d));
        }

        /// <summary>For the log: the row and what it draws.</summary>
        public static string DescribeBases()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            Shape s = ShapeNow;
            if (s.Bases <= 0f)
                return BasesRow > 0f ? "off under Simpler clouds (EVE's flat bases)" : "off (EVE's flat bases)";
            return "on " + (s.Bases * 100f).ToString("F0", c) + "% -- widest " + (s.Time[1] * SpanNow).ToString("F0", c) +
                   " m above the base (EVE's " + (CurveTime[1] * SpanNow).ToString("F0", c) + " m), tallest heap " +
                   (s.Tallest * SizeFactor * HeightFactor).ToString("F0", c) + " m";
        }

        // ---- softer cumulus (2026-11-16, the row "Softer cumulus", CumulusSoftness) ------------------

        /// <summary>
        /// The heaps read as cotton, as the Soft style's puffs do (the author: the Cumulus clouds are "just blocks",
        /// Soft's "basically like cotton candy"). EVE's edge goes from nothing to full over 1/33 of the cloud's
        /// inside; with the row on, over about Soft's softness, starting further out so the half-density surface
        /// stays where it was (a soft skin outwards, thinner just inside, the core as dense as before); the noise
        /// lookup pushed about as Soft's is; and, close up, a finer read of the same noise frays the edge (strands
        /// low, billows higher) with a faint fringe. The coverage (cg) is never moved: nothing is drawn where it was
        /// not, so the empty-sky skip, the slab's top and the rain stay as they are. Drawn by the cloud shader's
        /// extras variant only (CloudShaders.PlusOn); 0 = the heaps exactly as they always were. Static fields so
        /// the bench can sweep them; at 100% each value below, at less the row's share of it (the edge's width
        /// moves from EVE's to <see cref="SoftHardness"/>'s).
        /// </summary>
        public static float SoftHardness = 7f;

        /// <summary>Where the half-density surface sits at 100%: 1 = where the hard edge's was (the skin grows outwards as it thins inwards), 0 = the soft edge starts at the hard one's foot.</summary>
        public static float SoftSurface = 1f;

        /// <summary>The cloud's inside (cg) over which the skin and the fringe come in: so a heap's top and its foot still thin to nothing where cg does.</summary>
        public static float SoftSkinFade = 0.05f;

        /// <summary>
        /// (2026-11-18) With the row on, a thin step of the march (the soft skin, the fringe) that comes right after a
        /// thin step that worked out its light takes that light, while the march's steps are no longer than this many
        /// metres (0: never): every other thin step of a run looks at the sun. In the bench 86% of the steps in cloud
        /// were thin ones, each with five looks at the sun. OFF (0): measured at 80 m it saved nothing (Auto 81%,
        /// 6.8-7.75 ms with it, 6.55-7.85 without: thin steps seldom come two in a row) for a picture 0.05-0.24/255
        /// off. The shader's branch is dead at 0; take it out with the next bundle build.
        /// </summary>
        public static float SoftThinHold = 0f;

        /// <summary>Metres the noise lookup is pushed about by the 64^3 noise at 100% (Soft's own: <see cref="CloudSoft.Warp"/>).</summary>
        public static float SoftWarp = 250f;

        /// <summary>How deep the fine read carves at 100%, in the cloud's inside (cg) units.</summary>
        public static float SoftFray = 0.1f;

        /// <summary>The fine read's repeat: the heaps' divided by this whole number (4: ~575-600 m features, as Soft's).</summary>
        public static float SoftFrayMultiple = 4f;

        /// <summary>
        /// What the fray gives back, in units of its depth: it carves (fine read x its depth) and then adds this back
        /// everywhere it reaches, so up close (where it is drawn) the sky keeps its amount as it does far off (where it
        /// fades out). 0 = it only carves. Chosen 2026-11-16 in the bench: the clouds from straight above with the fine read
        /// in (mip -3) within a point of the row at 0 at 43 / 58 / 70%, Cumulus and Auto (0 took 2.6-8.4 points off).
        /// </summary>
        public static float SoftFrayBias = 0.45f;

        /// <summary>Over this share of the tallest heap the fine read's strands (low) turn to billows (higher), as Soft's <see cref="CloudSoft.WispHeight"/>.</summary>
        public static float SoftFrayHeight = 0.3f;

        /// <summary>The fringe's density at 100% (a share of the heaps' full density), as Soft's <see cref="CloudSoft.Fringe"/>.</summary>
        public static float SoftFringe = 0.2f;

        /// <summary>
        /// How far outside the surface the fringe reaches, in the cloud's inside (cg) units. Chosen in the bench (2026-11-16):
        /// 0.17 (Soft's 0.4 in the heaps' units) filled the gaps between heaps with haze; 0.05 left no fringe at all.
        /// </summary>
        public static float SoftFringeReach = 0.13f;

        /// <summary>
        /// Coverage taken off the heaps' line at 100%, as <see cref="BaseTrim"/>: the soft skin outwards made each heap's
        /// footprint from above wider (+6 to +9 points of sky at 43-70% on two cities); this keeps the sky's amount from
        /// straight above within a point of the row at 0 (measured 2026-11-16, Cumulus and Auto, 43 / 58 / 70 / 98%).
        /// </summary>
        public static float SoftTrim = 0.035f;

        /// <summary>The "Softer cumulus" row, 0..1 (its default without one).</summary>
        public static float SoftnessRow
        {
            get { return Clamp(Settings.CumulusSoftness != null ? Settings.CumulusSoftness.value : Settings.Defaults.CumulusSoftness, 0f, 1f); }
        }

        /// <summary>
        /// The row as drawn: only while the cloud shader's extras variant is (CloudShaders.PlusOn: never under Simpler
        /// clouds, nor where that variant does not run); 0 otherwise, and the shader gets the heaps as they always were.
        /// </summary>
        public static float SoftnessNow
        {
            get
            {
                float row = SoftnessRow;
                return row > 0f && CloudShaders.PlusOn ? row : 0f;
            }
        }

        /// <summary>The edge's hardness at <paramref name="softness"/> (0..1): EVE's 1 / (1 - <see cref="Hardness"/>) at 0, its width moving to <see cref="SoftHardness"/>'s.</summary>
        public static float SoftHardnessAt(float softness)
        {
            float hard = 1f / (1f - Hardness);
            float soft = Math.Max(1f, SoftHardness);
            float width = 1f / hard + (1f / soft - 1f / hard) * Math.Max(0f, Math.Min(1f, softness));
            return 1f / width;
        }

        /// <summary>Where the soft edge starts, in the cloud's inside units (at or under 0): half its width under the hard edge's half-density point, times <see cref="SoftSurface"/>.</summary>
        public static float SoftStartAt(float softness)
        {
            float hard = 1f / (1f - Hardness);
            return Math.Min(0f, (0.5f / hard - 0.5f / SoftHardnessAt(softness)) * Math.Max(0f, Math.Min(1f, SoftSurface)));
        }

        // ---- cloud variety (2026-11-19, the row "Cloud variety", CloudVariety) -------------------------------------

        /// <summary>The "Cloud variety" row, 0..1 (its default without one).</summary>
        public static float VarietyRow
        {
            get { return Clamp(Settings.CloudVariety != null ? Settings.CloudVariety.value : Settings.Defaults.CloudVariety, 0f, 1f); }
        }

        /// <summary>
        /// The row as drawn: only while the heaps are drawn, the cloud shader's extras variant is (CloudShaders.PlusOn: never
        /// under Simpler clouds, nor where that variant does not run) and the mix map holding the pattern is up; 0 otherwise,
        /// and the shader gets the heaps as they always were.
        /// </summary>
        public static float VarietyNow
        {
            get
            {
                float row = VarietyRow;
                return row > 0f && HeapsDrawn && CloudShaders.PlusOn && CloudMixMap.Ready ? row : 0f;
            }
        }

        /// <summary>
        /// Plain Cumulus wants the mix map baked for the variety's pattern (Mixed and Auto have it anyway): the row on, the
        /// extras' variant drawn, the noise up.
        /// </summary>
        public static bool VarietyWantsMap
        {
            get { return IsCumulus && VarietyRow > 0f && CloudShaders.PlusOn && Texture != null && !CloudMixMap.Failed && !CloudShaders.Lite; }
        }

        /// <summary>
        /// Metres above the base the tallest heap the cover draws now reaches in a vigorous stretch before it is stretched:
        /// the heaps' line at the map's top score, plus the most the variety moves it (what the towers' stretch is bounded
        /// against: <see cref="CloudVariety.TowerStretchFor"/>).
        /// </summary>
        public static float VarietyHereTop(float row)
        {
            float a, b;
            Line(HeapEdge(HeapCoverage(CloudWeather.Coverage)), out a, out b);
            return HeapTopAt(a + b - LineTrimNow + CloudVariety.MostLineMove(row)) * SizeFactor * HeightFactor;
        }

        /// <summary>The towers' stretch now (the row's own, bounded by CloudVariety.TopBound over the tallest heap drawn now).</summary>
        public static float VarietyTowerStretch
        {
            get { return CloudVariety.TowerStretchFor(VarietyHereTop(VarietyNow)); }
        }

        /// <summary>
        /// Metres above the base the heaps reach with the variety: <paramref name="top"/> (what they reach without it) or the
        /// stretched tallest heap, whichever is higher. <paramref name="top"/> itself with the row off.
        /// </summary>
        public static float VarietyTop(float top)
        {
            float row = VarietyNow;
            if (row <= 0f)
                return top;
            float here = VarietyHereTop(row);
            float stretched = CloudVariety.TopAt(here, SpanNow, ShapeNow.Time[1], CloudVariety.MostStretch(row, CloudVariety.TowerStretchFor(here)));
            return Math.Max(top, stretched);
        }

        /// <summary>For the log.</summary>
        public static string DescribeVariety()
        {
            float row = VarietyNow;
            if (row <= 0f)
                return VarietyRow > 0f ? "off: the extras' variant, the heaps or the pattern's map is not drawn here" : "off (the heaps as before)";
            return CloudVariety.Describe(row, VarietyTowerStretch, VarietyTop(TallestNow));
        }

        /// <summary>The coverage taken off the heaps' line now: <see cref="BaseTrimNow"/> and <see cref="SoftTrim"/> x the row as drawn (both 0 off: the line as it was).</summary>
        public static float LineTrimNow
        {
            get
            {
                float soft = SoftnessNow;
                return soft > 0f ? BaseTrimNow + SoftTrim * soft : BaseTrimNow;
            }
        }

        /// <summary>
        /// The heaps' density at one point, 0..1, with the row at <paramref name="softness"/>: the shader's CumulusDensity
        /// from cg on (its C# twin; the push moves where `noise` and `fine` are read, <see cref="SoftPush"/>). `noise`: the
        /// Cumulus noise there; `fine`: its read at <see cref="SoftFrayMultiple"/> x the frequency; `wisps`: how much of
        /// the fray the distance leaves (0..1, 0 for the light's looks); `y`: metres above the base; `tallest`: the
        /// tallest heap now (m). At 0 exactly <see cref="Density(float, float, float)"/>'s formula from cg.
        /// </summary>
        public static float SofterDensity(float cg, float noise, float fine, float wisps, float y, float softness, float tallest)
        {
            if (cg <= 0f)
                return 0f;
            if (softness <= 0f)
                return Math.Max(0f, Math.Min(1f, (cg - (1f - noise) * ErosionDepth) / (1f - Hardness)));

            float s = Math.Min(1f, softness);
            float inside = cg - (1f - noise) * ErosionDepth;
            float f = 0f;
            if (wisps > 0f)
            {
                f = Clamp01((fine - CloudSoft.NoiseMiddle) * CloudSoft.NoiseContrast + 0.5f);
                inside -= SoftFray * s * wisps * (f + (1f - 2f * f) * Clamp01(y / Math.Max(SoftFrayHeight * tallest, 1f)) - SoftFrayBias);
            }

            float skin = Clamp01(cg / SoftSkinFade);
            float d = Clamp01((inside - SoftStartAt(s) * skin) * SoftHardnessAt(s));
            if (wisps > 0f && SoftFringe > 0f)
                d = Math.Max(d, SoftFringe * s * wisps * skin * f * f * Clamp01(1f + inside / SoftFringeReach));
            return d;
        }

        /// <summary>The push at a point (the shader's): metres along x, y, z from the 64^3 noise's R and G there (as read) at the row's <paramref name="softness"/>.</summary>
        public static void SoftPush(float r, float g, float softness, out float x, out float y, out float z)
        {
            float w = SoftWarp * Math.Max(0f, Math.Min(1f, softness));
            float a = r - CloudSoft.WarpMiddleR, b = g - CloudSoft.WarpMiddleG;
            x = a * w;
            y = (a - b) * 0.5f * w;
            z = b * w;
        }

        private static float Clamp01(float v)
        {
            return v < 0f ? 0f : v > 1f ? 1f : v;
        }

        /// <summary>For the log: the row and what it draws.</summary>
        public static string DescribeSoftness()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            float s = SoftnessNow;
            if (s <= 0f)
                return SoftnessRow > 0f ? "off: the cloud shader's extras variant is not drawn (Simpler clouds, or it does not run here)" : "off (EVE's hard edges)";
            return "on " + (s * 100f).ToString("F0", c) + "% -- edge soft over " + (1f / SoftHardnessAt(s)).ToString("F3", c) + " of the inside (EVE's " +
                   (1f - Hardness).ToString("F3", c) + "), starting " + (-SoftStartAt(s)).ToString("F3", c) + " out, pushed " + (SoftWarp * s).ToString("F0", c) +
                   " m, frayed " + (SoftFray * s).ToString("F3", c) + " deep by its x" + SoftFrayMultiple.ToString("F0", c) + " read, fringe " +
                   (SoftFringe * s).ToString("F2", c) + ", line trimmed " + (SoftTrim * s).ToString("F3", c);
        }

        // ---- natural Classic clouds (2026-11-17, the row "Natural Classic clouds", ClassicShape) ------------

        /// <summary>The "Natural Classic clouds" row, 0..1 (its default without one).</summary>
        public static float ClassicShapeRow
        {
            get { return Clamp(Settings.ClassicShape != null ? Settings.ClassicShape.value : Settings.Defaults.ClassicShape, 0f, 1f); }
        }

        /// <summary>
        /// The row as drawn (Sky.ClassicShape): only while the cloud shader's extras variant is (CloudShaders.PlusOn: never
        /// under Simpler clouds, nor where that variant does not run); 0 otherwise, and the shader gets the layer as it always was.
        /// </summary>
        public static float ClassicShapeNow
        {
            get
            {
                float row = ClassicShapeRow;
                return row > 0f && CloudShaders.PlusOn ? row : 0f;
            }
        }

        /// <summary>For the log: the row and what it draws.</summary>
        public static string DescribeClassicShape()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            float s = ClassicShapeNow;
            if (s <= 0f)
                return ClassicShapeRow > 0f ? "off: the cloud shader's extras variant is not drawn (Simpler clouds, or it does not run here)" : "off (the layer's slab as before)";
            return "on " + (s * 100f).ToString("F0", c) + "% -- the layer's clouds domed over " + ClassicShape.Span.ToString("0.##", c) +
                   "x the old edge in from their edge (gain " + ClassicShape.Gain.ToString("0.##", c) + "), widest " + (ClassicShape.Middle * 100f).ToString("F0", c) +
                   "% up, never over the old slab; the rim's edge up to " + (1f + ClassicShape.RimSoftness * s).ToString("0.##", c) + "x as soft, none over a closed deck (" +
                   (ClassicShape.DeckFrom * 100f).ToString("F0", c) + "-" + (ClassicShape.DeckTo * 100f).ToString("F0", c) + "% cover)";
        }

        // ---- the light ------------------------------------------------------------------------

        /// <summary>
        /// EVE's four phase lobes: eccentricity and strength. Two for single scattering (the
        /// silver lining), two for multiple scattering (the soft glow deep inside).
        /// </summary>
        public const float SingleG1 = 0.95f, SingleW1 = 0.1f, SingleG2 = 0.8f, SingleW2 = 0.2f;
        public const float MultipleG1 = 0.2f, MultipleW1 = 3.0f, MultipleG2 = -0.4f, MultipleW2 = 0.3f;

        /// <summary>The multiple-scattering light falls through this share of the extinction.</summary>
        public const float MultipleExtinction = 0.5f;

        /// <summary>The single-scattering lobes' cap: straight into the sun the rim cannot blow out.</summary>
        public const float SingleCap = 5f;

        /// <summary>How strongly the cloud above takes the sky light away (as cloud detail's).</summary>
        public const float AmbientOcclusion = 0.15f;

        public static float Single(float cosTheta)
        {
            return Math.Min(SingleCap, SingleW1 * HenyeyGreenstein(cosTheta, SingleG1) + SingleW2 * HenyeyGreenstein(cosTheta, SingleG2));
        }

        public static float Multiple(float cosTheta)
        {
            return MultipleW1 * HenyeyGreenstein(cosTheta, MultipleG1) + MultipleW2 * HenyeyGreenstein(cosTheta, MultipleG2);
        }

        /// <summary>
        /// The lobes' gain, so that a sunlit face seen side-on is as bright as Classic's old light
        /// makes it: the brightness settings keep their meaning in both styles.
        /// </summary>
        public static readonly float LightGain = ComputeLightGain();

        private static float ComputeLightGain()
        {
            float old = 0.65f + 0.35f * Math.Min(HenyeyGreenstein(0f, 0.55f), 5f);
            return old / (Single(0f) + Multiple(0f));
        }

        /// <summary>
        /// The sun's light at a point, as the shader works it out, from the optical depth towards
        /// the sun: both kinds of scattering, through the gain.
        /// </summary>
        public static float SunLight(float cosTheta, float tau)
        {
            return SunLight(cosTheta, tau, 0f, LightThrough.K);
        }

        /// <summary>The same with sunlight through the clouds (LightThrough) at an amount and k.</summary>
        public static float SunLight(float cosTheta, float tau, float soakAmount, float soakK)
        {
            float multiple = LightThrough.Apply((float)Math.Exp(-tau * MultipleExtinction), tau, soakAmount, soakK);
            return (Single(cosTheta) * (float)Math.Exp(-tau) + Multiple(cosTheta) * multiple) * LightGain;
        }

        public static float HenyeyGreenstein(float cosTheta, float g)
        {
            float g2 = g * g;
            return (1f - g2) / (float)Math.Pow(1f + g2 - 2f * g * cosTheta, 1.5);
        }

        // ---- for the log ----------------------------------------------------------------------

        public static string Name(int style)
        {
            return style == Cumulus ? "Cumulus" : style == Classic ? "Classic" : style == Mixed ? "Mixed" : style == Soft ? "Soft" :
                   style == Auto ? "Auto" : "unknown (" + style + ")";
        }

        public static string Describe()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            string layer = " (Classic layer " + (LayerAmount * 100f).ToString("F0", c) + "%, ";
            string bases = ", rounded bases " + (BasesRow * 100f).ToString("F0", c) + "%)";
            if (Current == Auto)
                return Name(Current) + layer + CloudAuto.DescribeAt(CloudWeather.Coverage) + ", cumulus size " + (SizeFactor * 100f).ToString("F0", c) +
                       "%, height " + (HeightFactor * 100f).ToString("F0", c) + "%, amount " + (AmountFactor * 100f).ToString("F0", c) + "%" + bases;
            if (Current == Cumulus)
                return Name(Current) + layer + "size " + (SizeFactor * 100f).ToString("F0", c) + "%, height " +
                       (HeightFactor * 100f).ToString("F0", c) + "%, amount " + (AmountFactor * 100f).ToString("F0", c) + "%" + bases;
            if (Current == Mixed)
                return Name(Current) + layer + "puffs up to " + SoftTallestNow.ToString("F0", c) + " m tall, cumulus size " + (SizeFactor * 100f).ToString("F0", c) +
                       "%, height " + (HeightFactor * 100f).ToString("F0", c) + "%, amount " + (AmountFactor * 100f).ToString("F0", c) + "%" + bases;
            if (Current == Soft)
                return Name(Current) + layer + "up to " + SoftTallestNow.ToString("F0", c) + " m tall)";
            return Name(Current);
        }

        /// <summary>The Cumulus style's shape, once, for the log.</summary>
        public static string DescribeShape()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "heaps carved from one " + CumulusNoise3D.Size + "^3 noise repeating every " + NoiseTileNow.ToString("F0", c) +
                   " m, up to " + TallestNow.ToString("F0", c) + " m tall, edges " + (Hardness * 100f).ToString("F0", c) +
                   "% hard, erosion " + ErosionDepth.ToString("F2", c) + ", no more of them above " +
                   (HeapCap * 100f).ToString("F0", c) + "%; with the Classic layer, its thickness x " +
                   LayerThinnest.ToString("F2", c) + ".." + LayerThickest.ToString("F2", c) + " following the map";
        }
    }
}
