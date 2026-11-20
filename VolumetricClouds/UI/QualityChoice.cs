namespace VolumetricClouds.UI
{
    /// <summary>
    /// The quality preset a NEW install starts on (2026-11-18, the author: Medium "since now we have the
    /// cumulus clouds which are heavy on by default", or "according to the user's in-game settings").
    /// Pure (tools\test-quality.ps1 compiles it); SettingsCatalog reads the computer for it.
    /// </summary>
    /// <remarks>
    /// Medium, lower (Low) where the game's own graphics options were lowered or the graphics card is
    /// small; never High on its own. The game ships its options at their top levels (shadows High,
    /// level of detail High, textures High), so most players never touched them and every computer
    /// reads as "high-end" by them: only a LOWERED option says something, and it says "lower". A
    /// player who wants High picks it on the options page. An existing settings file keeps its
    /// preset (every file lists it).
    /// The game's levels (OptionsGraphicsPanel, read from its IL 2026-11-18): shadows 0 off, 1 low,
    /// 2 medium, 3 high (default 3); level of detail 0 low .. 3 very high (default 2); textures 0 low,
    /// 1 medium, 2 high (default 2). -1 = not readable, then it says nothing.
    /// </remarks>
    public static class QualityChoice
    {
        /// <summary>A card with less graphics memory than this (MB) starts on Low: 4 GB cards report 4096, or a little under.</summary>
        public const int LowMemoryMB = 3800;

        public const int Low = 0;
        public const int Medium = 1;
        public const int High = 2;
        public const int Custom = 3;

        // Smoother clouds' drawing sizes as their row stores them (Sky.SmootherSize: Half 0, Third 2, Quarter 3).
        // Written out here so this file still compiles alone.
        private const int SizeHalf = 0, SizeThird = 2, SizeQuarter = 3;

        /// <summary>
        /// The size Smoother clouds draws the clouds at under a preset (1.6.1, the author after a test on an M1 Mac:
        /// "we need to link the quality preset to the Smoother clouds. High = Smoothest as it is now"): Low a quarter
        /// of the screen each way, Medium a third, High half (the size everyone had until then). Custom has none.
        /// </summary>
        public static int DrawingSize(int preset)
        {
            return preset == Low ? SizeQuarter : preset == Medium ? SizeThird : SizeHalf;
        }

        /// <summary>
        /// The Low preset draws none of the cloud shader's extras (Softer cumulus, Natural Classic clouds, Cloud
        /// variety, the glow above 100%): the plain cloud program, the look of 1.5.3's heaps (1.6.1). They are on in
        /// the mod defaults, so without this a new install on Low still drew the heaviest program there is.
        /// </summary>
        public static bool ExtrasOff(int preset)
        {
            return preset == Low;
        }

        /// <summary>
        /// A settings file read at startup, written before the presets set the drawing size: under Low, Medium or
        /// High the size becomes the preset's when it was never chosen (half, the only value there was before the
        /// row and its default since), and a size chosen by hand is kept and the preset reads Custom from now on.
        /// Under Custom, or with the preset's size already there, nothing changes. True when something did.
        /// </summary>
        public static bool Reconcile(int preset, int size, out int newPreset, out int newSize)
        {
            newPreset = preset;
            newSize = size;
            if (preset < Low || preset > High)
                return false;

            int wanted = DrawingSize(preset);
            if (size == wanted)
                return false;

            if (size == SizeHalf)
                newSize = wanted;
            else
                newPreset = Custom;
            return true;
        }

        /// <summary>
        /// The preset (0 Low, 1 Medium) for these game levels and this much graphics memory (MB, 0 or
        /// less: unknown); <paramref name="why"/> says what decided it, in English, for the log.
        /// </summary>
        public static int Pick(int shadows, int levelOfDetail, int textures, int videoMemoryMB, out string why)
        {
            if (videoMemoryMB > 0 && videoMemoryMB < LowMemoryMB)
            {
                why = "the graphics card has " + videoMemoryMB + " MB";
                return Low;
            }

            if (shadows >= 0 && shadows <= 1)
            {
                why = "the game's shadows are " + (shadows == 0 ? "off" : "low");
                return Low;
            }

            if (levelOfDetail == 0)
            {
                why = "the game's level of detail is low";
                return Low;
            }

            if (textures == 0)
            {
                why = "the game's textures are low";
                return Low;
            }

            why = "the default; nothing in the game's graphics options was lowered";
            return Medium;
        }
    }
}
