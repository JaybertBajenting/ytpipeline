using System.Text;
using Newtonsoft.Json;

namespace YtPipeline.Core.Services;

/// <summary>
/// Centralised JSON read/write. Always writes <b>BOM-less UTF-8</b> — a BOM breaks some
/// downstream tools and is never required for UTF-8.
/// </summary>
public static class Json
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static readonly JsonSerializerSettings Settings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
    };

    public static string Serialize(object value) =>
        JsonConvert.SerializeObject(value, Settings);

    public static T Deserialize<T>(string json) =>
        JsonConvert.DeserializeObject<T>(json, Settings)
        ?? throw new JsonException($"Deserialized null for {typeof(T).Name}.");

    public static void WriteFile(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Serialize(value), Utf8NoBom);
    }

    public static T ReadFile<T>(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Expected JSON file not found: {path}");
        return Deserialize<T>(File.ReadAllText(path));
    }
}
