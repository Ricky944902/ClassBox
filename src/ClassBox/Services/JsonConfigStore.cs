using System.IO;
using System.Text.Json;
using ClassBox.Abstractions.Services;

namespace ClassBox.Services;

/// <summary>基于 System.Text.Json 的简单配置存储（零第三方依赖）。</summary>
public sealed class JsonConfigStore : IConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _directory;

    public JsonConfigStore(string directory)
    {
        _directory = directory;
        System.IO.Directory.CreateDirectory(_directory);
    }

    public string Directory => _directory;

    public T Load<T>(string key, T defaultValue) where T : class
        => TryLoadFile<T>(key, out var value) ? value! : defaultValue;

    public bool TryLoad<T>(string key, out T? value) where T : class, new()
    {
        if (TryLoadFile<T>(key, out var result))
        {
            value = result;
            return true;
        }

        value = null;
        return false;
    }

    public void Save<T>(string key, T value) where T : class
    {
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            var json = JsonSerializer.Serialize(value, Options);
            File.WriteAllText(GetPath(key), json);
        }
        catch
        {
            // 配置写入失败不应导致崩溃；调用方可通过日志服务记录
        }
    }

    public void Delete(string key)
    {
        try
        {
            var path = GetPath(key);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 忽略
        }
    }

    private bool TryLoadFile<T>(string key, out T? value) where T : class
    {
        value = null;
        try
        {
            var path = GetPath(key);
            if (!File.Exists(path)) return false;

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return false;

            value = JsonSerializer.Deserialize<T>(json, Options);
            return value is not null;
        }
        catch
        {
            return false;
        }
    }

    private string GetPath(string key)
    {
        var safe = string.Concat(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(_directory, safe + ".json");
    }
}
