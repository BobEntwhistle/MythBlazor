using System;
using System.Threading.Tasks;
using MythTvApi.Guide.Models;
using MythBlazor.Services;

namespace MythBlazor.Services
{
    public class GuideDataService
    {
        private readonly ApiClientFactory _apiFactory;

        public GuideDataService(ApiClientFactory apiFactory)
        {
            _apiFactory = apiFactory ?? throw new ArgumentNullException(nameof(apiFactory));
        }

        public async Task<ProgramGuide?> GetProgramGuideAsync(DateTimeOffset start, DateTimeOffset end, bool details = true)
        {
            var client = _apiFactory.CreateGuideClient();
            var resp = await client.Guide.GetProgramGuide.GetAsync(cfg =>
            {
                cfg.QueryParameters = new MythTvApi.Guide.Guide.GetProgramGuide.GetProgramGuideRequestBuilder.GetProgramGuideRequestBuilderGetQueryParameters
                {
                    StartTime = start,
                    EndTime = end,
                    Details = details
                };
            });

            return resp?.ProgramGuide;
        }
    }
}