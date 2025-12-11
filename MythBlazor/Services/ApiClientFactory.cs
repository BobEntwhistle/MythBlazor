using System;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Microsoft.Kiota.Abstractions.Serialization;
using MythTvApi.Dvr;
using MythTvApi.Guide;
using MythTvApi.Content;
using Microsoft.Kiota.Abstractions.Authentication;

namespace MythBlazor.Services
{
    /// <summary>
    /// Central factory to create configured ApiClient instances for generated MythTV clients.
    /// Keeps HttpClient / adapter creation logic in one place so components can be simplified.
    /// </summary>
    public class ApiClientFactory
    {
        private readonly IConfiguration _configuration;

        public ApiClientFactory(IConfiguration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        private HttpClient BuildHttpClient(string? baseApiUrl)
        {
            if (string.IsNullOrWhiteSpace(baseApiUrl))
            {
                return new HttpClient();
            }

            // Ensure trailing slash so relative IconURL resolution works consistently
            var baseWithSlash = baseApiUrl.EndsWith("/") ? baseApiUrl : baseApiUrl + "/";
            return new HttpClient { BaseAddress = new Uri(baseWithSlash) };
        }

        private HttpClientRequestAdapter BuildAdapter(IParseNodeFactory? parseFactory = null)
        {
            var baseApiUrl = _configuration.GetValue<string>("ApiInfo:ApiUrl") ?? string.Empty;
            var httpClient = BuildHttpClient(baseApiUrl);

            if (parseFactory != null)
            {
                return new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), parseFactory, httpClient: httpClient)
                {
                    BaseUrl = baseApiUrl
                };
            }

            return new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: httpClient)
            {
                BaseUrl = baseApiUrl
            };
        }

        public MythTvApi.Dvr.ApiClient CreateDvrClient()
        {
            var adapter = BuildAdapter();
            return new MythTvApi.Dvr.ApiClient(adapter);
        }

        public MythTvApi.Guide.ApiClient CreateGuideClient()
        {
            var adapter = BuildAdapter();
            return new MythTvApi.Guide.ApiClient(adapter);
        }

        public MythTvApi.Content.ApiClient CreateContentClient(IParseNodeFactory? parseFactory = null)
        {
            var adapter = BuildAdapter(parseFactory);
            return new MythTvApi.Content.ApiClient(adapter);
        }
    }
}