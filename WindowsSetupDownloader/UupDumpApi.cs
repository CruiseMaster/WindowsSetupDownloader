using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace WindowsSetupDownloader
{
    public class UupDumpApi
    {
        private readonly HttpClient _client;

        public UupDumpApi()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true
            };

            _client = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.uupdump.net")
            };
        }

        private async Task<T> Get<T>(string path)
        {
            try
            {
                var json = await _client.GetStringAsync(path);
                return JsonSerializer.Deserialize<T>(json);
            }
            catch (Exception)
            {
            }

            return default;
        }

        public Task<UupListIdRoot> ListProducts()
            => Get<UupListIdRoot>("/listid.php");

        public Task<UupSearchResponse> Search(string term)
            => Get<UupSearchResponse>($"/search.php?search={Uri.EscapeDataString(term)}");

        public Task<UupGetResponse> GetFiles(string updateId)
            => Get<UupGetResponse>($"/get.php?id={updateId}");
    }
}