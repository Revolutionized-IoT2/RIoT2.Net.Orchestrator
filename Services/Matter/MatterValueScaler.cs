using RIoT2.Core.Models.Matter;

namespace RIoT2.Net.Orchestrator.Services.Matter
{
    /// <summary>
    /// Converts between the units a RIoT device reports and the units a Matter cluster attribute uses.
    /// </summary>
    /// <remarks>
    /// Every value crosses the bridge as a <see cref="double"/> - booleans included, carried as 0 or 1 -
    /// so one scaling table serves every attribute. <see cref="ToMatter"/> is the report direction and
    /// <see cref="ToRiot"/> its inverse; the inverse rounds per scale so a command payload sent back to a
    /// device carries a sensible number rather than the full binary expansion of a division.
    /// </remarks>
    public static class MatterValueScaler
    {
        /// <summary>The Matter level/hue/saturation maximum: attributes are 0..254, not 0..255.</summary>
        private const double LevelMax = 254d;

        /// <summary>Scales a value reported by a RIoT device into the unit of a Matter attribute.</summary>
        public static double ToMatter(double value, MatterValueScale scale) => scale switch
        {
            MatterValueScale.InvertBoolean => value != 0 ? 0 : 1,
            MatterValueScale.Percent0To100ToLevel0To254 => value * LevelMax / 100d,
            MatterValueScale.Fraction0To1ToLevel0To254 => value * LevelMax,
            MatterValueScale.Degrees0To360ToHue0To254 => value * LevelMax / 360d,
            MatterValueScale.Percent0To100ToSaturation0To254 => value * LevelMax / 100d,
            MatterValueScale.KelvinToMireds => Reciprocal(value),
            MatterValueScale.CelsiusToHundredths => value * 100d,
            MatterValueScale.PercentToHundredths => value * 100d,
            MatterValueScale.LuxToLogScale => ToLogScale(value),
            _ => value
        };

        /// <summary>Scales a value taken from a Matter attribute back into the unit a RIoT device expects.</summary>
        public static double ToRiot(double value, MatterValueScale scale) => scale switch
        {
            // Self-inverse.
            MatterValueScale.InvertBoolean => value != 0 ? 0 : 1,
            MatterValueScale.Percent0To100ToLevel0To254 => Math.Round(value * 100d / LevelMax),
            MatterValueScale.Fraction0To1ToLevel0To254 => Math.Round(value / LevelMax, 3),
            MatterValueScale.Degrees0To360ToHue0To254 => Math.Round(value * 360d / LevelMax),
            MatterValueScale.Percent0To100ToSaturation0To254 => Math.Round(value * 100d / LevelMax),
            // Mireds and kelvin are reciprocals of one another, so the same conversion runs both ways.
            MatterValueScale.KelvinToMireds => Math.Round(Reciprocal(value)),
            MatterValueScale.CelsiusToHundredths => Math.Round(value / 100d, 2),
            MatterValueScale.PercentToHundredths => Math.Round(value / 100d, 2),
            MatterValueScale.LuxToLogScale => Math.Round(FromLogScale(value)),
            _ => value
        };

        // Mireds = 1000000 / kelvin. A zero guards against a device reporting "no colour temperature".
        private static double Reciprocal(double value) => value == 0 ? 0 : 1000000d / value;

        // Illuminance Measurement carries 10000 * log10(lux) + 1; 0 is the reserved "unknown" encoding.
        private static double ToLogScale(double lux) => lux <= 0 ? 0 : 10000d * Math.Log10(lux) + 1d;

        private static double FromLogScale(double measured) => measured <= 0 ? 0 : Math.Pow(10d, (measured - 1d) / 10000d);
    }
}
