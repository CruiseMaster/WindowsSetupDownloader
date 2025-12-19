using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace WindowsSetupDownloaderConsole
{
    public class ApiModelle
    {
        public class UupListIdRoot
        {
            [JsonPropertyName("response")] public UupListIdResponse Response { get; set; }
        }

        public class UupListIdResponse
        {
            [JsonPropertyName("apiVersion")] public string ApiVersion { get; set; }

            [JsonPropertyName("builds")] public List<UupBuildInfo> Builds { get; set; }
        }

        public class UupBuildInfo
        {
            [JsonPropertyName("title")] public string Title { get; set; }

            [JsonPropertyName("build")] public string Build { get; set; }

            [JsonPropertyName("arch")] public string Arch { get; set; }

            [JsonPropertyName("created")] public long? Created { get; set; }

            [JsonPropertyName("uuid")] public string Uuid { get; set; }
        }


        public class UupGetResponse
        {
            //[JsonPropertyName("files")] public Dictionary<string, UupFile> Files { get; set; }
            [JsonPropertyName("apiVersion")] public string ApiVersion { get; set; }

            [JsonPropertyName("files")] public List<UupFile> Files { get; set; }

            public class UupFile
            {
                public string Name { get; set; }
                public string Url { get; set; }
                public string Sha1 { get; set; }
            }
        }

        public class UupSearchResponse
        {
            [JsonPropertyName("builds")] public Dictionary<string, UupBuild> Builds { get; set; }

            public class UupBuild
            {
                public string Title { get; set; }
                public long BuildNumber { get; set; }
                public string Arch { get; set; }
                public string Channel { get; set; }
            }
        }
    }
}