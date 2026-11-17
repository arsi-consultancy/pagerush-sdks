using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace PageRush.Tests;

/// <summary>Tests that read or change environment variables run one at a time.</summary>
[CollectionDefinition("environment", DisableParallelization = true)]
public sealed class EnvironmentCollection
{
}

[Collection("environment")]
public sealed class ClientTests
{
    [Fact]
    public void ApiKeyFromOptionsIsTrimmed()
    {
        var t = TestClient.Answering((_, _) => Responses.Json(200, Samples.TemplateJson), o => o.ApiKey = "  " + Samples.ApiKey + "\n");
        Assert.Equal("https://api.test", t.Client.BaseUrl);
    }

    [Fact]
    public void ApiKeyFallsBackToEnvironmentAndIsRequired()
    {
        var saved = Environment.GetEnvironmentVariable("PAGERUSH_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("PAGERUSH_API_KEY", null);
            var e = Assert.Throws<ArgumentException>(() => new PageRushClient());
            Assert.Contains("PAGERUSH_API_KEY", e.Message);

            Environment.SetEnvironmentVariable("PAGERUSH_API_KEY", " pk_from_env ");
            var client = new PageRushClient();
            Assert.Equal(PageRushClient.DefaultBaseUrl, client.BaseUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PAGERUSH_API_KEY", saved);
        }
    }

    [Fact]
    public void BaseUrlFromEnvironmentAndTrailingSlashStripped()
    {
        var saved = Environment.GetEnvironmentVariable("PAGERUSH_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("PAGERUSH_BASE_URL", "http://localhost:8787/");
            Assert.Equal("http://localhost:8787", new PageRushClient("pk_x").BaseUrl);
            Assert.Equal("https://example.test/api", new PageRushClient(new PageRushClientOptions { ApiKey = "pk_x", BaseUrl = "https://example.test/api//" }).BaseUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PAGERUSH_BASE_URL", saved);
        }
    }

    [Theory]
    [InlineData("pk_live_has space")]
    [InlineData("pk_live_tab\there")]
    [InlineData("pk_live_newline\nhere")]
    [InlineData("pk_live_nul\0here")]
    [InlineData("pk_live_del\u007fhere")]
    [InlineData("pk_live_unicodeé")]
    [InlineData("pk_live_nbsp here")]
    public void InvalidApiKeyIsRejectedWithoutEchoingIt(string key)
    {
        var e = Assert.Throws<ArgumentException>(() => new PageRushClient(key));
        Assert.DoesNotContain("pk_live", e.Message);
        Assert.DoesNotContain("pk_live", e.ToString());
    }

    [Theory]
    [InlineData("ftp://pagerush.dev")]
    [InlineData("pagerush.dev")]
    [InlineData("/v1")]
    public void InvalidBaseUrlIsRejected(string url)
    {
        Assert.Throws<ArgumentException>(() => new PageRushClient(new PageRushClientOptions { ApiKey = "pk_x", BaseUrl = url }));
    }

    [Fact]
    public void ApiKeyNeverAppearsInStringOrJsonOutput()
    {
        var options = new PageRushClientOptions { ApiKey = Samples.ApiKey, BaseUrl = "https://api.test" };
        var client = new PageRushClient(options);
        foreach (var text in new[]
        {
            client.ToString(),
            options.ToString(),
            JsonSerializer.Serialize(client),
            JsonSerializer.Serialize(options),
            JsonSerializer.Serialize(client.Templates),
        })
        {
            Assert.DoesNotContain(Samples.ApiKey, text);
        }

        Assert.Contains("[redacted]", client.ToString());
    }

    [Fact]
    public async System.Threading.Tasks.Task SendsAuthUserAgentAndAccept()
    {
        var t = TestClient.Answering((_, _) => Responses.Json(200, """{"items":[],"next_cursor":null}"""));
        await t.Client.Templates.ListAsync();
        var r = Assert.Single(t.Requests);
        Assert.Equal("Bearer " + Samples.ApiKey, r.Header("Authorization"));
        Assert.Equal("pagerush-dotnet/" + PageRushClient.Version, r.Header("User-Agent"));
        Assert.Equal("application/json", r.Header("Accept"));
        Assert.Null(r.Header("Content-Type"));
        Assert.Null(r.Header("Idempotency-Key"));
    }

    [Fact]
    public void VersionMatchesCsprojAndAssembly()
    {
        var csproj = Path.Combine(RepoFiles.DotnetDir(), "src", "PageRush", "PageRush.csproj");
        var version = XDocument.Load(csproj).Descendants("Version").Single().Value;
        Assert.Equal(PageRushClient.Version, version);
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$"), PageRushClient.Version);

        var informational = typeof(PageRushClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.Equal(version, informational.Split('+')[0]);
    }

    [Fact]
    public void ChangelogHasAnEntryForTheVersionOrUnreleased()
    {
        var changelog = File.ReadAllText(Path.Combine(RepoFiles.DotnetDir(), "CHANGELOG.md"));
        Assert.True(
            changelog.Contains("## [" + PageRushClient.Version + "] - ") || changelog.Contains("## [Unreleased]"),
            "CHANGELOG.md has no entry for " + PageRushClient.Version);
    }
}

internal static class RepoFiles
{
    /// <summary>Finds the dotnet/ directory by walking up from the test binaries.</summary>
    public static string DotnetDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "PageRush", "PageRush.csproj")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("cannot find the dotnet directory");
    }
}
