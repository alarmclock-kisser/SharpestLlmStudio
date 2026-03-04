using System;
using System.Collections.Generic;
using System.Text;

namespace SharpestLlmStudio.Shared
{
    public class GenerationStats
    {
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

        public int ContextSize { get; set; }
        public int TotalContextTokens { get; set; }

    }
}
