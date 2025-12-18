using Newtonsoft.Json;

public static class JsonLoader
{
    private static string GetPath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, fileName);
    }


    public static T LoadJson<T>(string fileName) where T : new()
    {
        string path = GetPath(fileName);

        if (!File.Exists(path))
        {
            T obj = new T();
            SaveJson(fileName, obj);
            return obj;
        }

        string content = File.ReadAllText(path);

        if (string.IsNullOrWhiteSpace(content))
        {
            T obj = new T();
            SaveJson(fileName, obj);
            return obj;
        }

        return JsonConvert.DeserializeObject<T>(content);
    }

    public static void SaveJson<T>(string fileName, T data)
    {
        string path = GetPath(fileName);

        string json = JsonConvert.SerializeObject(data, Formatting.Indented);
        File.WriteAllText(path, json);
    }
}