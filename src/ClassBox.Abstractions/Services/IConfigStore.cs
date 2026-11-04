namespace ClassBox.Abstractions.Services;

/// <summary>
/// 轻量 JSON 配置存储。每个插件拥有独立目录，互不干扰。
/// </summary>
public interface IConfigStore
{
    /// <summary>读取配置；不存在或反序列化失败时返回 defaultValue。</summary>
    T Load<T>(string key, T defaultValue) where T : class;

    bool TryLoad<T>(string key, out T? value) where T : class, new();

    void Save<T>(string key, T value) where T : class;

    void Delete(string key);

    /// <summary>配置文件所在目录。</summary>
    string Directory { get; }
}
