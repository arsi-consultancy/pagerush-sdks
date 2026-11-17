using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PageRush.Tests;

public sealed class TemplateFileTests
{
    [Theory]
    [InlineData("main.typ")]
    [InlineData("data/rows.CSV")]
    [InlineData("a.txt")]
    [InlineData("b.json")]
    [InlineData("c.svg")]
    [InlineData("d.xml")]
    [InlineData("e.md")]
    [InlineData("f.yml")]
    [InlineData("g.yaml")]
    [InlineData("refs.bib")]
    public void TextExtensionsAreUtf8(string path)
    {
        var f = TemplateFile.FromString(path, "héllo");
        Assert.Equal(TemplateFile.EncodingUtf8, f.Encoding);
        Assert.Equal("héllo", f.Content);
        Assert.Equal(path, f.Path);
        Assert.Equal("héllo", TemplateFile.FromBytes(path, Encoding.UTF8.GetBytes("héllo")).Content);
    }

    [Theory]
    [InlineData("logo.png")]
    [InlineData("fonts/Inter.ttf")]
    [InlineData("noext")]
    [InlineData("dir.typ/image")]
    public void OtherFilesAreBase64(string path)
    {
        var f = TemplateFile.FromBytes(path, [0, 255, 1]);
        Assert.Equal(TemplateFile.EncodingBase64, f.Encoding);
        Assert.Equal("AP8B", f.Content);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("é")), TemplateFile.FromString(path, "é").Content);
    }

    [Fact]
    public void InvalidUtf8TextFileIsRejected()
    {
        Assert.Throws<ArgumentException>(() => TemplateFile.FromBytes("main.typ", [0xff, 0xfe]));
    }

    [Fact]
    public void FromPathReadsTheFileAndUsesForwardSlashes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pagerush-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var local = Path.Combine(dir, "logo.png");
            File.WriteAllBytes(local, [1, 2, 3]);
            var f = TemplateFile.FromPath(local);
            Assert.Equal("logo.png", f.Path);
            Assert.Equal("AQID", f.Content);

            var nested = TemplateFile.FromPath(local, Path.Combine("assets", "img", "logo.png"));
            Assert.Equal("assets/img/logo.png", nested.Path);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>A fact that runs only when PAGERUSH_API_KEY is set.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PAGERUSH_API_KEY")))
        {
            Skip = "PAGERUSH_API_KEY not set";
        }
    }
}

public sealed class LiveTests
{
    [LiveFact]
    public async Task CreateRenderDelete()
    {
        var client = new PageRushClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var created = await client.Templates.CreateAsync(
            new CreateTemplateOptions { Name = "pagerush-dotnet smoke test", Source = "#let data = json(\"data.json\")\nHello #data.name" },
            cts.Token);
        try
        {
            var result = await client.Templates.RenderAsync(created.Template.Id, new RenderOptions { Data = new { name = ".NET" } }, cts.Token);
            var pdf = result.Pdf;
            if (result.Queued)
            {
                var job = await client.Jobs.WaitAsync(result.JobId!, cancellationToken: cts.Token);
                Assert.Equal(JobStatus.Succeeded, job.Status);
                pdf = await client.Jobs.DownloadAsync(job.Id, cts.Token);
            }

            Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf!, 0, 4));
        }
        finally
        {
            await client.Templates.DeleteAsync(created.Template.Id, CancellationToken.None);
        }
    }
}
