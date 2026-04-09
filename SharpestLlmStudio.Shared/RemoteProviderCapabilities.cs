using System;
using System.Collections.Generic;

namespace SharpestLlmStudio.Shared
{
    public sealed record ProviderCapabilities
    (
        bool AllowTopK,
        bool AllowRepetitionPenalty,
        bool AllowTopP,
        bool AllowTemperature,
        bool AllowMaxTokens
    );

    public static class RemoteProviderCapabilities
    {
        public static ProviderCapabilities Get(RemoteLlmProvider provider, bool isLocal = false)
        {
            // If local llama.cpp, allow local-specific params
            if (isLocal)
            {
                return new ProviderCapabilities(
                    AllowTopK: true,
                    AllowRepetitionPenalty: true,
                    AllowTopP: true,
                    AllowTemperature: true,
                    AllowMaxTokens: true
                );
            }

            return provider switch
            {
                RemoteLlmProvider.Gemini => new ProviderCapabilities(
                    AllowTopK: false,
                    AllowRepetitionPenalty: false,
                    AllowTopP: true,
                    AllowTemperature: true,
                    AllowMaxTokens: true),

                RemoteLlmProvider.OpenAI => new ProviderCapabilities(
                    AllowTopK: false,
                    AllowRepetitionPenalty: false,
                    AllowTopP: true,
                    AllowTemperature: true,
                    AllowMaxTokens: true),

                RemoteLlmProvider.OpenRouter => new ProviderCapabilities(
                    AllowTopK: false,
                    AllowRepetitionPenalty: false,
                    AllowTopP: true,
                    AllowTemperature: true,
                    AllowMaxTokens: true),

                RemoteLlmProvider.XAI => new ProviderCapabilities(
                    AllowTopK: false,
                    AllowRepetitionPenalty: false,
                    AllowTopP: true,
                    AllowTemperature: true,
                    AllowMaxTokens: true),

                RemoteLlmProvider.CustomOpenAiCompatible => new ProviderCapabilities(
                    AllowTopK: false,
                    AllowRepetitionPenalty: false,
                    AllowTopP: true,
                    AllowTemperature: true,
                    AllowMaxTokens: true),

                _ => new ProviderCapabilities(
                    AllowTopK: false,
                    AllowRepetitionPenalty: false,
                    AllowTopP: true,
                    AllowTemperature: true,
                    AllowMaxTokens: true),
            };
        }
    }
}
