using System;
using System.Collections.Generic;
using System.Text;

namespace SharpestLlmStudio.Shared
{
    public class GenerationStats
    {
        private static readonly Lock AccumulatedTotalsLock = new();

        public DateTime? GenerationStarted { get; set; } = null;
        public DateTime? GenerationFinished { get; set; } = null;
        public double TimeTilFirstToken { get; set; } = 0.0;

        public int TotalTokensGenerated { get; set; }

        public TimeSpan? TotalGenerationTime => this.GenerationStarted.HasValue ? this.GenerationFinished.HasValue ? this.GenerationFinished.Value - this.GenerationStarted.Value : DateTime.UtcNow - this.GenerationStarted.Value : null;

        public double EffectiveGenerationSeconds => Math.Max(0.0, (this.TotalGenerationTime?.TotalSeconds ?? 0.0) - this.TimeTilFirstToken);
        public double AverageTimePerToken => this.TotalTokensGenerated > 0 ? this.EffectiveGenerationSeconds / this.TotalTokensGenerated : 0.0;
        public double TokensPerSecond => this.EffectiveGenerationSeconds > 0 ? this.TotalTokensGenerated / this.EffectiveGenerationSeconds : 0.0;

        public bool Running => this.GenerationStarted.HasValue && !this.GenerationFinished.HasValue;

        public double? UsedWattsApprox { get; set; }
        public double? WattsPerHourApprox => this.TotalGenerationTime?.TotalHours > 0 ? this.UsedWattsApprox / this.TotalGenerationTime?.TotalHours : null;

        public static double AccumulatedUsedWattsApprox { get; private set; }
        public static double AccumulatedCostApprox { get; private set; }

        public int ContextSize { get; set; }
        public int TotalContextTokens { get; set; }

        public static void ResetAccumulatedTotals()
        {
            lock (AccumulatedTotalsLock)
            {
                AccumulatedUsedWattsApprox = 0.0;
                AccumulatedCostApprox = 0.0;
            }
        }

        public static void AddCompletedGeneration(double? usedWattsApprox, TimeSpan? totalGenerationTime, double pricePerKiloWattHour)
        {
            double watts = Math.Max(0.0, usedWattsApprox ?? 0.0);
            if (watts <= 0.0)
            {
                return;
            }

            double hours = Math.Max(0.0, totalGenerationTime?.TotalHours ?? 0.0);
            double requestCost = hours > 0.0
                ? (watts * hours / 1000.0) * Math.Max(0.0, pricePerKiloWattHour * 100)
                : 0.0;

            lock (AccumulatedTotalsLock)
            {
                AccumulatedUsedWattsApprox += watts;
                AccumulatedCostApprox += requestCost;
            }
        }

    }
}
