using System.Text;
using System.Text.Json;
using Cewka.App.Radio;
using Cewka.Tools.StationList;

// Użycie: Cewka.RadioCatalog <katalog-ze-wsadem> <plik-wynikowy>
// Wsad (stacje-v2/*.json) leży poza repozytorium; do repozytorium trafia tylko plik wynikowy (Radio/stations.json).
if (args.Length != 2)
{
    Console.Error.WriteLine("Użycie: Cewka.RadioCatalog <katalog-ze-wsadem> <plik-wynikowy>");
    return 2;
}

var inputDirectory = args[0];
var outputPath = args[1];
if (!Directory.Exists(inputDirectory))
{
    Console.Error.WriteLine($"Brak katalogu ze wsadem: {inputDirectory}");
    return 2;
}

var files = Directory.GetFiles(inputDirectory, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
if (files.Count == 0)
{
    Console.Error.WriteLine($"Katalog nie zawiera plików .json: {inputDirectory}");
    return 2;
}

var result = CatalogBuilder.Build(files.Select(f => File.ReadAllText(f, Encoding.UTF8)));

var builder = new StringBuilder("{\"schema\":1,\"stations\":[\n");
for (var i = 0; i < result.Stations.Count; i++)
{
    builder.Append(JsonSerializer.Serialize(result.Stations[i], RadioJsonContext.Default.RadioStation));
    builder.Append(i + 1 < result.Stations.Count ? ",\n" : "\n");
}

builder.Append("]}\n");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
File.WriteAllText(outputPath, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

Console.WriteLine($"Pliki wsadu: {files.Count}; stacji we wsadzie: {result.Read}; w pliku: {result.Stations.Count}; odpadło: {result.Read - result.Stations.Count}.");
Print("Stacje, które odpadły", result.DroppedStations);
Print("Adresy odrzucone", result.DroppedAddresses);
Print("Logo pominięte", result.DroppedLogos);
foreach (var warning in result.Warnings)
    Console.WriteLine($"Uwaga: {warning}");

Console.WriteLine($"Zapisano: {Path.GetFullPath(outputPath)}");
return 0;

static void Print(string title, SortedDictionary<string, int> counts)
{
    Console.WriteLine($"{title}: {counts.Values.Sum()}");
    foreach (var (reason, count) in counts)
        Console.WriteLine($"  {count,5}  {reason}");
}
