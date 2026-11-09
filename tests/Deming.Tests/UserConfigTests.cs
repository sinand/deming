using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Deming.Tests;

/// <summary>The user config file. Every test points DEMING_CONFIG at a temporary path: the real user directory is never touched.</summary>
public sealed class UserConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deming-tests", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_dir, "sub", "config.json");

    private string? Env(string name) => name == UserConfig.PathVariable ? ConfigPath : null;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static IConfiguration Empty() => new ConfigurationBuilder().AddInMemoryCollection().Build();

    [Fact]
    public void Default_path_ends_in_deming_config_json_and_env_overrides_it()
    {
        Assert.EndsWith(Path.Combine("Deming", "config.json"), UserConfig.DefaultPath(_ => null));
        Assert.Equal(ConfigPath, UserConfig.DefaultPath(Env));
    }

    [Fact]
    public void First_serve_generates_the_key_and_second_reuses_it()
    {
        var first = Empty();
        var written = UserConfig.ProvideApiKey(first, Env, "Deming:ApiKey");

        Assert.Equal(ConfigPath, written);
        var key = first["Deming:ApiKey"];
        Assert.True(key!.Length >= 40);
        Assert.Equal(key, UserConfig.Read(ConfigPath).ApiKey);

        var second = Empty();
        Assert.Null(UserConfig.ProvideApiKey(second, Env, "Deming:ApiKey"));
        Assert.Equal(key, second["Deming:ApiKey"]);
    }

    [Fact]
    public void A_configured_key_is_left_alone_and_nothing_is_written()
    {
        var configuration = Empty();
        configuration["Deming:ApiKey"] = "from-environment";

        Assert.Null(UserConfig.ProvideApiKey(configuration, Env, "Deming:ApiKey"));
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public void Unknown_fields_survive_and_no_temporary_file_is_left()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, """{"url":"http://example.test:9","note":{"keep":true}}""");

        UserConfig.ProvideApiKey(Empty(), Env, "Deming:ApiKey");

        var root = JsonDocument.Parse(File.ReadAllText(ConfigPath)).RootElement;
        Assert.Equal("http://example.test:9", root.GetProperty("url").GetString());
        Assert.True(root.GetProperty("note").GetProperty("keep").GetBoolean());
        Assert.True(root.TryGetProperty("apiKey", out _));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(ConfigPath)!));
    }

    [Fact]
    public void A_broken_file_is_an_error_not_overwritten()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, "{ not json");

        Assert.Throws<InvalidOperationException>(() => UserConfig.ProvideApiKey(Empty(), Env, "Deming:ApiKey"));
        Assert.Equal("{ not json", File.ReadAllText(ConfigPath));
    }
}
