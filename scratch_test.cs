using System;
using System.Net.Http;
using System.Threading.Tasks;

class Program
{
    static async Task Main()
    {
        string base64 = Uri.EscapeDataString(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(@"C:\Users\david\Pictures\Hytale Screenshots\Hytale2026-01-13_17-05-08.png")));
        string url = $"http://localhost:8889/overlay/local?path={base64}&v=123";
        Console.WriteLine($"Fetching: {url}");
        try {
            using var client = new HttpClient();
            var resp = await client.GetAsync(url);
            Console.WriteLine($"Status: {resp.StatusCode}");
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            Console.WriteLine($"Length: {bytes.Length}");
        } catch (Exception ex) {
            Console.WriteLine(ex);
        }
    }
}
