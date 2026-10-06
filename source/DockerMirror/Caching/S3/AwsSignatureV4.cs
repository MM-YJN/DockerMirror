using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace DockerMirror.Caching.S3;

internal sealed class AwsSignatureV4
{
    private const string Algorithm = "AWS4-HMAC-SHA256";
    private const string Aws4Request = "aws4_request";
    private const string Iso8601Format = "yyyyMMddTHHmmssZ";
    private const string DateFormat = "yyyyMMdd";

    private static readonly byte[] s_emptyPayloadHash =
    [
        0xe3, 0xb0, 0xc4, 0x42, 0x98, 0xfc, 0x1c, 0x14,
        0x9a, 0xfb, 0xf4, 0xc8, 0x99, 0x6f, 0xb9, 0x24,
        0x27, 0xae, 0x41, 0xe4, 0x64, 0x9b, 0x93, 0x4c,
        0xa4, 0x95, 0x99, 0x1b, 0x78, 0x52, 0xb8, 0x55,
    ];

    private readonly byte[] _secretKeyBytes;
    private readonly string _accessKey;
    private readonly string _region;
    private readonly string _service;

    public AwsSignatureV4(string accessKey, string secretKey, string region)
        : this(accessKey, secretKey, region, "s3")
    {
    }

    internal AwsSignatureV4(string accessKey, string secretKey, string region, string service)
    {
        _secretKeyBytes = Encoding.UTF8.GetBytes("AWS4" + secretKey);
        _accessKey = accessKey;
        _region = region;
        _service = service;
    }

    internal static readonly string s_emptyPayloadHashHex = BytesToHex(s_emptyPayloadHash);

    internal static string ComputePayloadHashHex(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        long originalPosition = stream.CanSeek ? stream.Position : 0;
        byte[] hash = SHA256.HashData(stream);
        if (stream.CanSeek)
        {
            stream.Position = originalPosition;
        }

        return BytesToHex(hash);
    }

    internal static string EncodePathSegment(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        var sb = new StringBuilder(segment.Length);
        foreach (char ch in segment)
        {
            if (IsUnreserved(ch) || ch == '/')
            {
                sb.Append(ch);
            }
            else
            {
                foreach (byte b in Encoding.UTF8.GetBytes([ch]))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"%{b:X2}");
                }
            }
        }

        return sb.ToString();
    }

    private static string EncodeCanonicalUriPath(string absolutePath)
    {
        string decoded = Uri.UnescapeDataString(absolutePath);

        var sb = new StringBuilder(decoded.Length);
        foreach (char ch in decoded)
        {
            if (IsUnreserved(ch) || ch == '/')
            {
                sb.Append(ch);
            }
            else
            {
                foreach (byte b in Encoding.UTF8.GetBytes([ch]))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"%{b:X2}");
                }
            }
        }

        return sb.ToString();
    }

    private static bool IsUnreserved(char ch)
    {
        return ch is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-'
            or '_'
            or '.'
            or '~';
    }

    public void Sign(HttpRequestMessage request, string payloadHashHex)
        => Sign(request, payloadHashHex, DateTimeOffset.UtcNow);

    internal void Sign(HttpRequestMessage request, string payloadHashHex, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);

        string amzDate = now.ToString(Iso8601Format, CultureInfo.InvariantCulture);
        string dateStamp = now.ToString(DateFormat, CultureInfo.InvariantCulture);

        Uri requestUri = request.RequestUri
            ?? throw new InvalidOperationException("Request must have a URI.");

        string host = request.Headers.TryGetValues("Host", out IEnumerable<string>? hostValues)
            ? hostValues.First()
            : requestUri.Host;

        string canonicalUri = GetCanonicalUri(requestUri);
        string canonicalQuery = GetCanonicalQueryString(requestUri);

        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHashHex);

        var xAmzHeaders = new SortedDictionary<string, string>(StringComparer.Ordinal);
        CollectXAmzHeaders(request.Headers, xAmzHeaders);

        if (request.Content?.Headers is { } contentHeaders)
        {
            CollectXAmzHeaders(contentHeaders, xAmzHeaders);
        }

        var canonicalHeadersBuilder = new StringBuilder();
        canonicalHeadersBuilder.Append("host:");
        canonicalHeadersBuilder.Append(host);
        canonicalHeadersBuilder.Append('\n');

        foreach ((string? name, string? value) in xAmzHeaders)
        {
            canonicalHeadersBuilder.Append(name);
            canonicalHeadersBuilder.Append(':');
            canonicalHeadersBuilder.Append(value);
            canonicalHeadersBuilder.Append('\n');
        }

        string signedHeaders = "host;" + string.Join(";", xAmzHeaders.Keys);
        string canonicalHeaders = canonicalHeadersBuilder.ToString();

        string canonicalRequest = $"{request.Method.Method}\n" +
                               $"{canonicalUri}\n" +
                               $"{canonicalQuery}\n" +
                               $"{canonicalHeaders}\n" +
                               $"{signedHeaders}\n" +
                               $"{payloadHashHex}";

        string scope = $"{dateStamp}/{_region}/{_service}/{Aws4Request}";
        string stringToSign = $"{Algorithm}\n" +
                           $"{amzDate}\n" +
                           $"{scope}\n" +
                           $"{BytesToHex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";

        byte[] signingKey = DeriveSigningKey(dateStamp);
        string signature = BytesToHex(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign)));

        string authorization = $"{Algorithm} Credential={_accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";

        request.Headers.TryAddWithoutValidation("Authorization", authorization);
    }

    private static void CollectXAmzHeaders(HttpHeaders headers, SortedDictionary<string, string> result)
    {
        foreach ((string? name, IEnumerable<string>? values) in headers)
        {
            if (!name.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string lowerName = name.ToLowerInvariant();

            if (!result.ContainsKey(lowerName))
            {
                result[lowerName] = string.Join(",", values);
            }
        }
    }

    private static string GetCanonicalUri(Uri uri)
    {
        string path = uri.AbsolutePath;
        if (path.Length == 0)
        {
            return "/";
        }

        return EncodeCanonicalUriPath(path);
    }

    private static string GetCanonicalQueryString(Uri uri)
    {
        string query = uri.Query;

        if (string.IsNullOrEmpty(query))
        {
            return string.Empty;
        }

        query = query.TrimStart('?');
        var parameters = new List<(string Key, string Value)>();

        foreach (string param in query.Split('&'))
        {
            int eq = param.IndexOf('=');
            if (eq < 0)
            {
                string key = Uri.EscapeDataString(Uri.UnescapeDataString(param));
                parameters.Add((key, string.Empty));
            }
            else
            {
                string key = Uri.EscapeDataString(Uri.UnescapeDataString(param[..eq]));
                string value = Uri.EscapeDataString(Uri.UnescapeDataString(param[(eq + 1)..]));
                parameters.Add((key, value));
            }
        }

        parameters.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key) is var c and not 0 ? c : string.CompareOrdinal(a.Value, b.Value));

        var sb = new StringBuilder();
        for (int i = 0; i < parameters.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('&');
            }

            sb.Append(parameters[i].Key);
            sb.Append('=');
            sb.Append(parameters[i].Value);
        }

        return sb.ToString();
    }

    private byte[] DeriveSigningKey(string dateStamp)
    {
        byte[] kDate = HMACSHA256.HashData(_secretKeyBytes, Encoding.UTF8.GetBytes(dateStamp));
        byte[] kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(_region));
        byte[] kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes(_service));
        return HMACSHA256.HashData(kService, Encoding.UTF8.GetBytes(Aws4Request));
    }

    private static string BytesToHex(byte[] bytes)
        => Convert.ToHexStringLower(bytes);
}
