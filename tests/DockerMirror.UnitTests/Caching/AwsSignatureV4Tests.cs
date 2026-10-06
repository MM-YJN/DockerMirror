using DockerMirror.Caching.S3;

namespace DockerMirror.UnitTests.Caching;

public sealed class AwsSignatureV4Tests
{
    [Fact]
    public void EmptyPayloadHashHex_IsCorrect()
    {
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", AwsSignatureV4.s_emptyPayloadHashHex);
    }

    [Fact]
    public void EncodePathSegment_PreservesUnreservedChars()
    {
        string result = AwsSignatureV4.EncodePathSegment("sha256/ab/cd/abcdef01");
        Assert.Equal("sha256/ab/cd/abcdef01", result);
    }

    [Fact]
    public void EncodePathSegment_EncodesSpaces()
    {
        string result = AwsSignatureV4.EncodePathSegment("hello world");
        Assert.Equal("hello%20world", result);
    }

    [Fact]
    public void ComputePayloadHashHex_ReturnsConsistentHash()
    {
        byte[] content = "hello world"u8.ToArray();
        using var stream = new MemoryStream(content);
        string hash1 = AwsSignatureV4.ComputePayloadHashHex(stream);
        stream.Position = 0;
        string hash2 = AwsSignatureV4.ComputePayloadHashHex(stream);

        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length);
    }

    [Fact]
    public void ComputePayloadHashHex_RestoresStreamPosition()
    {
        byte[] content = "test payload"u8.ToArray();
        using var stream = new MemoryStream(content);
        stream.Position = 5;
        long originalPosition = stream.Position;

        AwsSignatureV4.ComputePayloadHashHex(stream);

        Assert.Equal(originalPosition, stream.Position);
    }

    [Fact]
    public void Sign_ProducesAuthorizationHeader_MatchesIndependentlyVerifiedSignature()
    {
        // This test is verified against openssl-computed values.
        // The signature was independently computed using:
        //   HMAC-SHA256 chain: AWS4wJalr... → 20150830 → us-east-1 → s3 → aws4_request
        //   Canonical request hash of the expected request (GET /test-key, etc.)
        //   Final HMAC-SHA256 of the string-to-sign with the derived signing key.
        var request = new HttpRequestMessage(HttpMethod.Get, "https://test-bucket.s3.us-east-1.amazonaws.com/test-key");
        request.Headers.TryAddWithoutValidation("Host", "test-bucket.s3.us-east-1.amazonaws.com");

        var signer = new AwsSignatureV4("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "us-east-1");

        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1440938160000); // 2015-08-30T12:36:00Z
        signer.Sign(request, AwsSignatureV4.s_emptyPayloadHashHex, timestamp);

        string? auth = request.Headers.TryGetValues("Authorization", out IEnumerable<string>? authValues) ? authValues.First() : null;
        Assert.NotNull(auth);
        Assert.StartsWith("AWS4-HMAC-SHA256", auth);
        Assert.Contains("Credential=AKIDEXAMPLE/20150830/us-east-1/s3/aws4_request", auth);
        Assert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date", auth);
        Assert.Contains("Signature=9458fb43bfe9622723930b9759bf91b03a899e80a0474a24c05800b9630774d7", auth);
    }

    [Fact]
    public void Sign_SetsXAmzHeaders()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://test-bucket.s3.us-east-1.amazonaws.com/test-key");
        request.Headers.TryAddWithoutValidation("Host", "test-bucket.s3.us-east-1.amazonaws.com");

        var signer = new AwsSignatureV4("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "us-east-1");
        signer.Sign(request, AwsSignatureV4.s_emptyPayloadHashHex);

        Assert.Contains(request.Headers, h => h.Key == "x-amz-date");
        Assert.Contains(request.Headers, h => h.Key == "x-amz-content-sha256");
        Assert.Contains(request.Headers, h => h.Key == "Authorization");
    }
}
