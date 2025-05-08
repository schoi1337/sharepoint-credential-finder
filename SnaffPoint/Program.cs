using SearchQueryTool.Helpers;
using SearchQueryTool.Model;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace SnaffPoint
{
    class Program
    {
        private static string PresetPath = "./presets";
        private static int MaxRows = 50;
        private static string SingleQueryText = null;
        private static SearchPresetList _SearchPresets;
        private static string BearerToken = null;
        private static string SPUrl = null;
        private static bool isFQL = false;
        private static string RefinementFilters = null;

        static void Main(string[] args)
        {
            if (args.Contains("--local"))
            {
                RunLocalScan(args);
                return;
            }

            foreach (var entry in args.Select((value, index) => new { index, value }))
            {
                switch (entry.value)
                {
                    case "-l":
                    case "--fql": isFQL = true; break;
                    case "-m": case "--max-rows":
                        if (!int.TryParse(args[entry.index + 1], out MaxRows)) { PrintHelp(); return; } break;
                    case "-p": case "--preset": PresetPath = args[entry.index + 1]; break;
                    case "-q": case "--query": SingleQueryText = args[entry.index + 1]; break;
                    case "-r": case "--refinement-filter": RefinementFilters = args[entry.index + 1]; break;
                    case "-t": case "--token": BearerToken = "Bearer " + args[entry.index + 1]; break;
                    case "-u": case "--url": SPUrl = args[entry.index + 1]; break;
                    case "-h": case "--help": PrintHelp(); return;
                }
            }

            if ((SPUrl == null || BearerToken == null) && !args.Contains("--local")) { PrintHelp(); return; }
            if (SingleQueryText != null) DoSingleQuery();
            else QueryAllPresets();
        }

        private static void RunLocalScan(string[] args)
        {
            var localIndex = Array.IndexOf(args, "--local");
            if (localIndex == -1 || localIndex + 1 >= args.Length)
            {
                Console.WriteLine("Usage: --local <folderPath>");
                return;
            }
            var folderPath = args[localIndex + 1];
            if (!Directory.Exists(folderPath))
            {
                Console.WriteLine($"[!] Folder not found: {folderPath}");
                return;
            }
            string[] previewKeywords = { "password", "token", "secret", "apikey" };
            var files = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".txt") || f.EndsWith(".log") || f.EndsWith(".md"));
            foreach (var file in files)
            {
                var content = File.ReadAllLines(file);
                var matches = content.Where(line => previewKeywords.Any(k => line.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)).Take(3);
                if (matches.Any())
                {
                    Console.WriteLine($"\n[+] Found in: {file}");
                    foreach (var line in matches) Console.WriteLine($"    Preview: {line.Trim()}");
                }
            }
        }

        private static void LoadSearchPresetsFromFolder(string presetFolderPath)
        {
            try { _SearchPresets = new SearchPresetList(presetFolderPath); }
            catch (Exception ex) { Console.WriteLine("Failed to read search presets. Error: " + ex.Message); }
        }

        private static SearchQueryResult StartSearchQueryRequest(SearchQueryRequest request)
        {
            try
            {
                HttpRequestResponsePair pair = HttpRequestRunner.RunWebRequest(request);
                if (pair?.Item2 != null && pair.Item2.StatusCode != HttpStatusCode.OK)
                    Console.WriteLine($"Request returned status: HTTP {(int)pair.Item2.StatusCode} {pair.Item2.StatusDescription}");
                var result = GetResultItem(pair);
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Request failed: " + ex.Message);
                return null;
            }
        }

        private static SearchQueryResult GetResultItem(HttpRequestResponsePair pair)
        {
            var request = pair.Item1;
            using (var response = pair.Item2)
            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                var content = reader.ReadToEnd();
                var reqHeaders = new NameValueCollection();
                foreach (var h in request.Headers.AllKeys) reqHeaders.Add(h, request.Headers[h]);
                var resHeaders = new NameValueCollection();
                foreach (var h in response.Headers.AllKeys) resHeaders.Add(h, response.Headers[h]);
                var result = new SearchQueryResult
                {
                    RequestUri = request.RequestUri,
                    RequestMethod = request.Method,
                    RequestContent = request.Method == "POST" ? pair.Item3 : "",
                    ContentType = response.ContentType,
                    ResponseContent = content,
                    RequestHeaders = reqHeaders,
                    ResponseHeaders = resHeaders,
                    StatusCode = response.StatusCode,
                    StatusDescription = response.StatusDescription,
                    HttpProtocolVersion = response.ProtocolVersion.ToString()
                };
                result.Process(); // ✅ 핵심: JSON 파싱하여 PrimaryQueryResult 세팅
                return result;
            }
        }

        static void QueryAllPresets()
        {
            LoadSearchPresetsFromFolder(PresetPath);
            var allResults = new Dictionary<string, List<(string, string)>>();
            if (_SearchPresets.Presets.Count == 0) { Console.WriteLine("No presets found in " + PresetPath); return; }

            foreach (var preset in _SearchPresets.Presets)
            {
                Console.WriteLine($"\n{preset.Name}\n{new string('=', preset.Name.Length)}\n");
                preset.Request.Token = BearerToken;
                preset.Request.SharePointSiteUrl = SPUrl;
                preset.Request.RowLimit = MaxRows;
                preset.Request.AcceptType = AcceptType.Json;
                preset.Request.AuthenticationType = AuthenticationType.SPOManagement;

                var result = StartSearchQueryRequest(preset.Request);
                DisplayResults(result);

                var entries = new List<(string, string)>();
                if (result?.PrimaryQueryResult?.RelevantResults != null)
                {
                    foreach (var item in result.PrimaryQueryResult.RelevantResults)
                        entries.Add((item.Title, item.Path));
                }
                allResults[preset.Name] = entries;
            }

            ExportResultsToCsv(allResults, "SharePointScanResults.csv");
        }

        private static void ExportResultsToCsv(Dictionary<string, List<(string Title, string Path)>> allResults, string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Preset Name,Title,Path");
            foreach (var preset in allResults)
            {
                foreach (var result in preset.Value)
                {
                    string safeTitle = result.Title.Replace("\"", "\"\"");
                    string safePath = result.Path.Replace("\"", "\"\"");
                    sb.AppendLine($"\"{preset.Key}\",\"{safeTitle}\",\"{safePath}\");
                }
            }
            File.WriteAllText(filePath, sb.ToString());
            Console.WriteLine($"CSV file saved to: {filePath}");
        }

        private static void DisplayResults(SearchQueryResult results)
        {
            if (results?.PrimaryQueryResult == null)
            {
                Console.WriteLine("Found no results... maybe the request failed?");
                Console.WriteLine("Raw response:");
                Console.WriteLine(results?.ResponseContent);
                return;
            }

            Console.WriteLine($"Found {results.PrimaryQueryResult.TotalRows} results");
            if (results.PrimaryQueryResult.TotalRows > MaxRows)
                Console.WriteLine($"Only showing {MaxRows} results.");

            foreach (var item in results.PrimaryQueryResult.RelevantResults)
            {
                Console.WriteLine("---");
                Console.WriteLine(item.Title);
                Console.WriteLine(item.Path);
                try
                {
                    var webClient = new WebClient();
                    webClient.Headers.Add("Authorization", BearerToken);
                    var content = webClient.DownloadString(item.Path);
                    string[] keywords = { "password", "token", "apikey", "secret" };
                    var lines = content.Split('\n').Where(l => keywords.Any(k => l.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)).Take(3);
                    foreach (var line in lines) Console.WriteLine($"    Preview: {line.Trim()}");
                }
                catch (Exception ex) { Console.WriteLine($"    [!] Preview fetch failed: {ex.Message}"); }
            }
        }

        private static void DoSingleQuery()
        {
            var request = new SearchQueryRequest
            {
                SharePointSiteUrl = SPUrl,
                AcceptType = AcceptType.Json,
                Token = BearerToken,
                AuthenticationType = AuthenticationType.SPOManagement,
                QueryText = SingleQueryText,
                HttpMethodType = HttpMethodType.Get,
                EnableFql = isFQL,
                RowLimit = MaxRows,
                RefinementFilters = RefinementFilters
            };
            var result = StartSearchQueryRequest(request);
            DisplayResults(result);
        }

        static void PrintHelp()
        {
            Console.WriteLine(
@"SnaffPoint - SharePoint + Local Keyword Scanner
Usage:
  SnaffPoint.exe -u <URL> -t <TOKEN> [options]
  SnaffPoint.exe --local <folderPath>
Options:
  -p, --preset            Path to XML preset folder
  -q, --query             KQL search query
  -r, --refinement-filter Add refinement filter
  -m, --max-rows          Max number of rows (default 50)
  -l, --fql               Use FQL instead of KQL
  -h, --help              Show this help message
  --local <path>          Run local keyword scan instead of SharePoint");
        }
    }
}
