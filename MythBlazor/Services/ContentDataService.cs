using System;
using System.Threading.Tasks;
using MythBlazor.Factories;
using MythBlazor.Services;

namespace MythBlazor.Services
{
    public class ContentDataService
    {
        private readonly ApiClientFactory _apiFactory;

        public ContentDataService(ApiClientFactory apiFactory)
        {
            _apiFactory = apiFactory ?? throw new ArgumentNullException(nameof(apiFactory));
        }

        /// <summary>
        /// Returns jpeg bytes (null if unavailable).
        /// </summary>
        public async Task<byte[]?> GetPreviewImageBytesAsync(int? recordedId = null, int? chanId = null, DateTimeOffset? startTime = null, int width = 320, int height = 180, int? secsIn = null)
        {
            // Use JpegFactory so Kiota returns raw bytes in AdditionalData["content"]
            var contentClient = _apiFactory.CreateContentClient(new JpegFactory());

            var resp = await contentClient.Content.GetPreviewImage.GetAsync(cfg =>
            {
                cfg.QueryParameters = new MythTvApi.Content.Content.GetPreviewImage.GetPreviewImageRequestBuilder.GetPreviewImageRequestBuilderGetQueryParameters
                {
                    RecordedId = recordedId,
                    ChanId = chanId,
                    StartTime = startTime,
                    Width = width,
                    Height = height,
                    SecsIn = secsIn,
                    Format = "jpg"
                };
            });

            if (resp?.AdditionalData != null && resp.AdditionalData.TryGetValue("content", out var contentObj) && contentObj != null)
            {
                if (contentObj is byte[] bytes && bytes.Length > 0) return bytes;
                if (contentObj is string s && !string.IsNullOrWhiteSpace(s))
                {
                    // treat string as base64 if possible
                    try
                    {
                        return Convert.FromBase64String(s);
                    }
                    catch
                    {
                        // not base64 -> no bytes
                    }
                }
            }

            // older generated field may contain base64 string
            var legacy = resp?.GetPreviewImageResponse;
            if (!string.IsNullOrEmpty(legacy))
            {
                try
                {
                    return Convert.FromBase64String(legacy);
                }
                catch { }
            }

            return null;
        }
    }
}