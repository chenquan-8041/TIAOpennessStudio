using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TIAOpennessStudio.AICore;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// One model entry in the provider catalog returned by the backend.
    /// </summary>
    public class ProviderModel
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public bool Vision { get; set; }
    }

    /// <summary>
    /// One provider entry in the catalog (mirrors backend core/providers.py).
    /// </summary>
    public class ProviderCatalogEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("api_type")]
        public string ApiType { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("base_url")]
        public string BaseUrl { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("base_url_editable")]
        public bool BaseUrlEditable { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("requires_base_url")]
        public bool RequiresBaseUrl { get; set; }

        public bool Available { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("unavailable_reason")]
        public string UnavailableReason { get; set; }

        public List<ProviderModel> Models { get; set; } = new List<ProviderModel>();
    }

    /// <summary>
    /// Fetches the cloud provider catalog from the Python backend's GET /providers
    /// endpoint. The backend is the single source of truth for which platforms
    /// exist and which the backend can actually serve (adapter dependency present).
    ///
    /// If the backend is unreachable, callers fall back to the local built-in
    /// table (AIConfig.GetAvailableModels) so the UI still works offline.
    /// </summary>
    public class ProviderCatalogService
    {
        private static readonly HttpClient Http = new HttpClient();
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly string _backendBaseUrl;

        public ProviderCatalogService(string backendBaseUrl)
        {
            _backendBaseUrl = string.IsNullOrWhiteSpace(backendBaseUrl)
                ? ApiEndpoints.DefaultBackendUrl
                : backendBaseUrl;
        }

        /// <summary>
        /// Fetch the provider catalog from the backend. Returns null on any failure
        /// (unreachable backend, bad payload) so the caller can fall back locally.
        /// Never throws.
        /// </summary>
        public async Task<List<ProviderCatalogEntry>> FetchAsync(int timeoutMs = 4000)
        {
            var url = _backendBaseUrl.TrimEnd('/') + ApiEndpoints.Providers;
            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                using var resp = await Http.GetAsync(url, cts.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    AppLogger.Log($"[ProviderCatalog] backend returned {(int)resp.StatusCode}, falling back to local table");
                    return null;
                }

                var json = await resp.Content.ReadAsStringAsync();
                var doc = JsonSerializer.Deserialize<ProvidersResponse>(json, JsonOpts);
                if (doc?.Providers == null || doc.Providers.Count == 0)
                {
                    AppLogger.Log("[ProviderCatalog] empty catalog, falling back to local table");
                    return null;
                }

                AppLogger.Log($"[ProviderCatalog] fetched {doc.Providers.Count} providers from backend");
                return doc.Providers;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[ProviderCatalog] fetch failed ({ex.Message}), falling back to local table");
                return null;
            }
        }

        /// <summary>
        /// Get the provider catalog, preferring the backend and falling back to the
        /// local built-in table (AIConfig.GetAvailableModels) when the backend is
        /// unavailable. The local fallback marks every provider as available since
        /// we cannot know the backend's real adapter state offline.
        /// </summary>
        public async Task<List<ProviderCatalogEntry>> GetCatalogAsync(int timeoutMs = 4000)
        {
            var fromBackend = await FetchAsync(timeoutMs);
            if (fromBackend != null)
                return fromBackend;

            return BuildLocalFallback();
        }

        /// <summary>
        /// Build a catalog from the local built-in table. Used when the backend is
        /// unreachable. Providers are marked available (optimistically) since the
        /// backend cannot be consulted; the connection test will surface any real
        /// adapter/dependency problem at use time.
        /// </summary>
        public static List<ProviderCatalogEntry> BuildLocalFallback()
        {
            var result = new List<ProviderCatalogEntry>();
            var table = AIConfig.GetAvailableModels();
            foreach (var kv in table)
            {
                var entry = new ProviderCatalogEntry
                {
                    Id = kv.Key,
                    Name = kv.Key,
                    ApiType = "openai_compat",
                    BaseUrl = AIConfig.GetDefaultEndpointForProvider(kv.Key) ?? "",
                    BaseUrlEditable = true,
                    RequiresBaseUrl = kv.Key == "Custom",
                    Available = true,
                    Models = kv.Value.Select(m => new ProviderModel
                    {
                        Id = m.Id,
                        Name = m.Name,
                        Description = m.Description,
                        Vision = m.SupportsVision,
                    }).ToList(),
                };
                result.Add(entry);
            }
            return result;
        }

        private class ProvidersResponse
        {
            public List<ProviderCatalogEntry> Providers { get; set; }
            public string Error { get; set; }
        }
    }
}
