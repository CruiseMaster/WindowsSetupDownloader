using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json;
using static WindowsSetupDownloaderConsole.ApiModelle;

namespace WindowsSetupDownloaderConsole
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
                //Console.WriteLine("GET-Call: Path: " + path);
                //Console.WriteLine(json);
                return JsonConvert.DeserializeObject<T>(json);
            }
            catch (Exception e)
            {
                Console.WriteLine("Fehler beim Mappen von JSon bei Get " + typeof(T) + " von " + path);
                Console.WriteLine(e);
                Console.WriteLine(e.Message);
            }

            return default;
        }

        public Task<UupListIdRoot> ListProducts()
            => Get<UupListIdRoot>("/listid.php");

        public Task<UupSearchResponse> Search(string term)
            => Get<UupSearchResponse>($"/search.php?search={Uri.EscapeDataString(term)}");

        public Task<UupGetResponse> GetFiles(string updateId)
            => Get<UupGetResponse>($"/get.php?id={updateId}");

        public Task<UupListIdRoot> GetNewestRetail()
            => Get<UupListIdRoot>($"/fetchupd.php?arch=amd64&ring=retail");
    }
}