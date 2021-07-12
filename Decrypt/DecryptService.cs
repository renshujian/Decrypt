using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Decrypt
{
    public class DecryptService : IDisposable
    {
        public HttpClient HttpClient { get; set; } = new();
        public string Endpoint { get; set; }

        public DecryptService(string endpoint)
        {
            Endpoint = endpoint;
        }

        public void Dispose() => HttpClient.Dispose();

        public async Task<string> Decrypt(string input)
        {
            using var response = await HttpClient.PostAsync(Endpoint, Content(input));
            response.EnsureSuccessStatusCode();
            var envelope = await response.Content.ReadAsStringAsync();
            return XElement.Parse(envelope).Descendants("{http://wserver}return").First().Value;
        }

        static StringContent Content(string input)
        {
            var body = $@"<soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
  <soap:Body>
    <decryptRequest xmlns=""http://wserver"">
        <decrypt>{input}</decrypt>
    </decryptRequest>
  </soap:Body>
</soap:Envelope>";
            return new StringContent(body);
        }
    }
}
