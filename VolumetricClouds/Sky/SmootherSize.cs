using System;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// HOW LARGE SMOOTHER CLOUDS MARCHES THE CLOUDS (2026-11-17, row SmootherCloudsSize, stored by value): the
    /// screen divided by 2, 3 or 4 each way. Pure (no Unity), so tools\test-smoothersize.ps1 compiles this file.
    /// </summary>
    /// <remarks>
    /// Measured in the cloud bench (Auto at 68% cover, 4K): the cloud pass 18-19 ms at half the screen, 10-10.6 at a
    /// third, 6.6-7.3 at a quarter, with no more grain at any size; the finest wisps get softer. The size is taken
    /// from the size the camera renders at (its pixelWidth / pixelHeight): a mod that renders the game's camera into
    /// its own SMALLER target (a dynamic resolution under 100%) gets the march for that size. But never from more
    /// lines than the screen has (<see cref="Basis"/>, 2026-11-18): with the camera rendering at twice the screen
    /// each way (supersampling at 200%) the march was half of THAT, four times the pixels of any other player's, for
    /// clouds that are scaled back down to the screen: Auto at 81% cover, 4K screen, 67-80 ms a frame against 20-22.
    /// </remarks>
    public static class SmootherSize
    {
        /// <summary>Half the screen each way: the default, the size before the row.</summary>
        public const int Half = 0;

        /// <summary>By the screen's size: half while the camera renders up to <see cref="ByScreenLines"/> lines, a third above.</summary>
        public const int ByScreen = 1;

        /// <summary>A third of the screen each way.</summary>
        public const int Third = 2;

        /// <summary>A quarter of the screen each way.</summary>
        public const int Quarter = 3;

        /// <summary>"By the screen's size" marches at half size up to this many lines (1440p), at a third above.</summary>
        public const int ByScreenLines = 1440;

        /// <summary>
        /// The size the divisor is taken of: the render's own, or, when the camera renders more lines than the screen
        /// has (a mod supersampling the game), that render scaled down to the screen's lines (its shape kept). A
        /// screen height of 0 or less (unknown): the render's own.
        /// </summary>
        public static void Basis(int renderWidth, int renderHeight, int screenHeight, out int width, out int height)
        {
            width = renderWidth;
            height = renderHeight;
            if (screenHeight > 0 && renderHeight > screenHeight)
            {
                height = screenHeight;
                width = (int)Math.Max(1L, ((long)renderWidth * screenHeight + renderHeight / 2) / renderHeight);
            }
        }

        /// <summary>What the size is divided by, each way, for a choice and the height in pixels it is taken of (<see cref="Basis"/>).</summary>
        public static int Divisor(int choice, int renderHeight)
        {
            switch (choice)
            {
                case ByScreen:
                    return renderHeight <= ByScreenLines ? 2 : 3;
                case Third:
                    return 3;
                case Quarter:
                    return 4;
                default:
                    // Half, and any value a newer version may write: the size as before.
                    return 2;
            }
        }

        /// <summary>
        /// The march target's side for a side of the render: rounded up, never under 1. At a divisor of 2 this is
        /// (pixels + 1) / 2, the size Smoother clouds always had.
        /// </summary>
        public static int Size(int pixels, int divisor)
        {
            if (divisor < 1)
                divisor = 1;
            return Math.Max(1, (pixels + divisor - 1) / divisor);
        }

        /// <summary>The divisor in words, for the log ("half", "a third", "a quarter").</summary>
        public static string DivisorName(int divisor)
        {
            switch (divisor)
            {
                case 2:
                    return "half";
                case 3:
                    return "a third";
                case 4:
                    return "a quarter";
                default:
                    return "1/" + divisor;
            }
        }

        /// <summary>The choice in words, for the log.</summary>
        public static string ChoiceName(int choice)
        {
            switch (choice)
            {
                case ByScreen:
                    return "by screen size (half up to " + ByScreenLines + " lines, a third above)";
                case Third:
                    return "a third of the screen";
                case Quarter:
                    return "a quarter of the screen";
                case Half:
                    return "half the screen";
                default:
                    return "half the screen (value " + choice + " unknown here)";
            }
        }
    }
}
