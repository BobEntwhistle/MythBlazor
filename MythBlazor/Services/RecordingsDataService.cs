using MythTvApi.Dvr.Dvr.GetRecordedList;
using Program = MythTvApi.Dvr.Models.Program;

namespace MythBlazor.Services
{
    public class RecordingsDataService
    {
        private readonly ApiClientFactory _apiFactory;

        public RecordingsDataService(ApiClientFactory apiFactory)
        {
            _apiFactory = apiFactory ?? throw new ArgumentNullException(nameof(apiFactory));
        }

        public async Task<List<MythTvApi.Dvr.Models.Program>> GetRecordedPageAsync(int pageIndex, int pageSize)
        {
            var client = _apiFactory.CreateDvrClient();
            var startIndex = pageIndex * pageSize;

            var resp = await client.Dvr.GetRecordedList.GetAsync(cfg =>
            {
                cfg.QueryParameters = new GetRecordedListRequestBuilder.GetRecordedListRequestBuilderGetQueryParameters
                {
                    Count = pageSize,
                    StartIndex = startIndex,
                    Details = true,
                    IncArtWork = true,
                    IncChannel = true,
                    Descending = true
                };
            });

            var programList = resp?.ProgramList;
            return programList?.Programs ?? new List<MythTvApi.Dvr.Models.Program>();
        }
    }
}