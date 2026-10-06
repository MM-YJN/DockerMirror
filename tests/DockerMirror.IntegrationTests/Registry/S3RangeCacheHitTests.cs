using System.Net;
using System.Security.Cryptography;
using System.Text;

using DockerMirror.IntegrationTests.TestInfrastructure;

namespace DockerMirror.IntegrationTests.Registry;

public sealed class S3RangeCacheHitTests(ITestOutputHelper testOutputHelper, MinioFixture fixture) : IClassFixture<MinioFixture>
{
    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task S3RangeCacheHit_ReturnsPartialContent()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        byte[] blobContent = Encoding.UTF8.GetBytes("hello-s3-range-cache-hit-test");
        string digest = ComputeSha256Digest(blobContent);

        await using var factory = new MirrorTestFactory(
            testOutputHelper,
            fixture.Endpoint,
            MinioFixture.AccessKey,
            MinioFixture.SecretKey,
            MinioFixture.BucketName);

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"s3-range-token\",\"expires_in\":300}}",
                    Encoding.UTF8,
                    "application/json"),
            });

        factory.Handler.SetupRegistry(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(blobContent)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream") },
                },
            });

        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpResponseMessage full = await client.GetAsync($"/v2/nginx/blobs/{digest}", cancellationToken);
        full.EnsureSuccessStatusCode();

        var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/v2/nginx/blobs/{digest}");
        rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 4);
        HttpResponseMessage rangeResponse = await client.SendAsync(rangeRequest, cancellationToken);

        Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
        string? contentRange = rangeResponse.Content.Headers.ContentRange?.ToString();
        Assert.Equal($"bytes 0-4/{blobContent.Length}", contentRange);

        byte[] rangeBody = await rangeResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        Assert.Equal(5, rangeBody.Length);
        Assert.Equal(blobContent[..5], rangeBody);
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task S3RangeCacheHit_UnsatisfiableRange_Returns416()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        byte[] blobContent = Encoding.UTF8.GetBytes("s3-unsatisfiable-range-test");
        string digest = ComputeSha256Digest(blobContent);

        await using var factory = new MirrorTestFactory(
            testOutputHelper,
            fixture.Endpoint,
            MinioFixture.AccessKey,
            MinioFixture.SecretKey,
            MinioFixture.BucketName);

        factory.Handler.SetupToken(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"token\":\"s3-416-token\",\"expires_in\":300}}",
                    Encoding.UTF8,
                    "application/json"),
            });

        factory.Handler.SetupRegistry(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(blobContent)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream") },
                },
            });

        using HttpClient client = factory.CreateClient();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpResponseMessage full = await client.GetAsync($"/v2/nginx/blobs/{digest}", cancellationToken);
        full.EnsureSuccessStatusCode();

        var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/v2/nginx/blobs/{digest}");
        rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(blobContent.Length + 10, null);
        HttpResponseMessage rangeResponse = await client.SendAsync(rangeRequest, cancellationToken);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, rangeResponse.StatusCode);
        string? cr416 = rangeResponse.Content.Headers.ContentRange?.ToString();
        Assert.Equal($"bytes */{blobContent.Length}", cr416);
    }

    private static string ComputeSha256Digest(byte[] content)
    {
        byte[] hash = SHA256.HashData(content);
        return $"sha256:{Convert.ToHexStringLower(hash)}";
    }
}
