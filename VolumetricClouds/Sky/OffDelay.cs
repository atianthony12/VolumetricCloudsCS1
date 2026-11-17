namespace VolumetricClouds.Sky
{
    /// <summary>
    /// A switch that goes on at once and off only after it has not been asked for a while (2026-11-17): what keeps the
    /// cloud shader's extras variant from flapping while the light inside the clouds hovers about 100% (CloudShaders).
    /// Pure (no Unity), so tools\test-offdelay.ps1 compiles this file; the caller hands it the clock.
    /// </summary>
    public sealed class OffDelay
    {
        private double _unaskedSince = double.NaN;

        /// <summary>Whether the switch is on now.</summary>
        public bool On { get; private set; }

        /// <summary>
        /// One look at the switch: <paramref name="asked"/> turns it on at once; unasked, it goes off once it has been
        /// unasked for <paramref name="delay"/> seconds of <paramref name="now"/> in a row. Returns <see cref="On"/>.
        /// </summary>
        public bool Update(bool asked, double now, double delay)
        {
            if (asked)
            {
                On = true;
                _unaskedSince = double.NaN;
                return true;
            }

            if (!On)
                return false;

            if (double.IsNaN(_unaskedSince) || now < _unaskedSince)
                _unaskedSince = now;

            if (now - _unaskedSince >= delay)
            {
                On = false;
                _unaskedSince = double.NaN;
            }

            return On;
        }

        /// <summary>Seconds it has been unasked while still on (0 when asked or off).</summary>
        public double Unasked(double now)
        {
            return On && !double.IsNaN(_unaskedSince) ? now - _unaskedSince : 0.0;
        }

        /// <summary>Off, forgetting everything (a new city).</summary>
        public void Reset()
        {
            On = false;
            _unaskedSince = double.NaN;
        }
    }
}
