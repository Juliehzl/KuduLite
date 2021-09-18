using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Kudu.Core.Helpers
{
    public class ArtifactUploadHelper
    {
        private string defaultDNSSuffix = null;
        private string appName;
        private string version;
        private NetworkCredential credential;

        public ArtifactUploadHelper(string appName, string username, string password, string defaultDNSSuffix, string version)
            : this(appName, new NetworkCredential(username, password), defaultDNSSuffix, version)
        {
        }

        public ArtifactUploadHelper(string appName, NetworkCredential credential, string defaultDNSSuffix, string version)
        {
            this.appName = appName;
            this.version = version;
            this.defaultDNSSuffix = defaultDNSSuffix;
            this.credential = credential;
        }

        public string BuildAppArtifactsURL()
        {
            return $"https://{appName}.scm.{defaultDNSSuffix}/api/vfs/site/artifacts/{version}/artifact.zip";
        }

        public async Task Upload(string filePath)
        {
            using (var handler = new HttpClientHandler { Credentials = credential })
            using (var httpClient = new HttpClient(handler))
            {
                using (var fileStream = new FileStream(filePath,
                    FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true))
                {
                    var uri = BuildAppArtifactsURL();
                    var content = new StreamContent(fileStream);
                    var requestMessage = new HttpRequestMessage();
                    requestMessage.Content = content;
                    requestMessage.RequestUri = new Uri(uri);
                    requestMessage.Method = HttpMethod.Put;
                    
                    //var zipUrlResponse = await httpClient.PutAsync(uri, content).ConfigureAwait(false);

                    try
                    {
                        var zipUrlResponse = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);
                        zipUrlResponse.EnsureSuccessStatusCode();
                    }
                    catch (HttpRequestException hre)
                    {
                        Console.WriteLine("Failed to get file from packageUri {0}", uri);
                        Console.WriteLine(hre);
                        throw;
                    }
                }
            }
        }
    }
}
