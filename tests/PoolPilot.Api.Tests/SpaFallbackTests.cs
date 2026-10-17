using System.Net;

namespace PoolPilot.Api.Tests;

/// <summary>
/// 生产包由 Kestrel 直接托管 SPA。两条边界必须钉住：
/// 客户端路由要拿到 index.html；API/Hub 的未匹配路径不能被 index.html 伪装成 200。
/// </summary>
[TestClass]
public sealed class SpaFallbackTests
{
    [TestMethod]
    public async Task ClientRoute_ServesIndexHtml_WhenWebRootHasIndex()
    {
        string webRoot = CreateWebRoot();

        try
        {
            using AuthTestApp app = new(webRoot);
            using HttpClient client = app.CreateClient();

            HttpResponseMessage response = await client.GetAsync("/public/leads");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("text/html", response.Content.Headers.ContentType?.MediaType);
            StringAssert.Contains(await response.Content.ReadAsStringAsync(), "poolpilot-spa");
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task UnknownApiPath_Returns404_InsteadOfServingTheSpa()
    {
        string webRoot = CreateWebRoot();

        try
        {
            using AuthTestApp app = new(webRoot);
            using HttpClient client = app.CreateClient();

            HttpResponseMessage response = await client.GetAsync("/api/definitely-not-here");

            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
            Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
            StringAssert.Contains(await response.Content.ReadAsStringAsync(), "Not Found");
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ClientRoute_WithoutWebRoot_Stays404()
    {
        using AuthTestApp app = new();
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/public/leads");

        Assert.AreEqual(
            HttpStatusCode.NotFound,
            response.StatusCode,
            "开发期没有 wwwroot 时不能凭空回一个前端；SPA 由 Vite 负责");
    }

    [TestMethod]
    public async Task UnknownNonGetRoute_Returns404_InsteadOfServingTheSpa()
    {
        string webRoot = CreateWebRoot();

        try
        {
            using AuthTestApp app = new(webRoot);
            using HttpClient client = app.CreateClient();

            HttpResponseMessage response = await client.PostAsync("/public/leads", content: null);

            Assert.AreEqual(
                HttpStatusCode.NotFound,
                response.StatusCode,
                "SPA 回退只服务 GET/HEAD，不能把 POST 到不存在的路径伪装成 200");
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    private static string CreateWebRoot()
    {
        string webRoot = Path.Combine(
            Path.GetTempPath(),
            $"poolpilot-webroot-{Guid.NewGuid():N}");

        Directory.CreateDirectory(webRoot);
        File.WriteAllText(
            Path.Combine(webRoot, "index.html"),
            "<!doctype html><html><head><title>poolpilot-spa</title></head><body></body></html>");

        return webRoot;
    }
}
