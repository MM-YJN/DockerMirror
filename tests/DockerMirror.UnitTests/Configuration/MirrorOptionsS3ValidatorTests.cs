using DockerMirror.Configuration;

using Microsoft.Extensions.Options;

namespace DockerMirror.UnitTests.Configuration;

public sealed class MirrorOptionsS3ValidatorTests
{
    [Fact]
    public void Validate_Skips_WhenBackendIsNotS3()
    {
        var options = new MirrorOptions();
        options.Cache.Backend = "FileSystem";

        ValidateOptionsResult result = new MirrorOptionsS3Validator().Validate(null, options);

        Assert.True(result.Skipped);
    }

    [Fact]
    public void Validate_Fails_WhenBackendIsS3_AndBucketIsEmpty()
    {
        var options = new MirrorOptions();
        options.Cache.Backend = "S3";
        options.Cache.S3.Bucket = "";
        options.Cache.S3.AccessKey = "ak";
        options.Cache.S3.SecretKey = "sk";
        options.Cache.S3.Region = "us-east-1";

        ValidateOptionsResult result = new MirrorOptionsS3Validator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Bucket", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_Fails_WhenBackendIsS3_AndAccessKeyIsEmpty()
    {
        var options = new MirrorOptions();
        options.Cache.Backend = "S3";
        options.Cache.S3.Bucket = "my-bucket";
        options.Cache.S3.AccessKey = "";
        options.Cache.S3.SecretKey = "sk";
        options.Cache.S3.Region = "us-east-1";

        ValidateOptionsResult result = new MirrorOptionsS3Validator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("AccessKey", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_Fails_WhenBackendIsS3_AndSecretKeyIsEmpty()
    {
        var options = new MirrorOptions();
        options.Cache.Backend = "S3";
        options.Cache.S3.Bucket = "my-bucket";
        options.Cache.S3.AccessKey = "ak";
        options.Cache.S3.SecretKey = "";
        options.Cache.S3.Region = "us-east-1";

        ValidateOptionsResult result = new MirrorOptionsS3Validator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("SecretKey", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_Fails_WhenBackendIsS3_AndRegionIsEmpty()
    {
        var options = new MirrorOptions();
        options.Cache.Backend = "S3";
        options.Cache.S3.Bucket = "my-bucket";
        options.Cache.S3.AccessKey = "ak";
        options.Cache.S3.SecretKey = "sk";
        options.Cache.S3.Region = "";

        ValidateOptionsResult result = new MirrorOptionsS3Validator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Region", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_Succeeds_WhenBackendIsS3_AndAllRequiredFieldsSet()
    {
        var options = new MirrorOptions();
        options.Cache.Backend = "S3";
        options.Cache.S3.Bucket = "my-bucket";
        options.Cache.S3.AccessKey = "ak";
        options.Cache.S3.SecretKey = "sk";
        options.Cache.S3.Region = "us-east-1";

        ValidateOptionsResult result = new MirrorOptionsS3Validator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_Succeeds_WhenBackendIsS3_AndServiceUrlIsNull()
    {
        var options = new MirrorOptions();
        options.Cache.Backend = "S3";
        options.Cache.S3.Bucket = "my-bucket";
        options.Cache.S3.AccessKey = "ak";
        options.Cache.S3.SecretKey = "sk";
        options.Cache.S3.Region = "us-east-1";
        options.Cache.S3.ServiceUrl = null;

        ValidateOptionsResult result = new MirrorOptionsS3Validator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_Succeeds_WhenBackendIsS3_AndServiceUrlIsEmpty()
    {
        var options = new MirrorOptions();
        options.Cache.Backend = "S3";
        options.Cache.S3.Bucket = "my-bucket";
        options.Cache.S3.AccessKey = "ak";
        options.Cache.S3.SecretKey = "sk";
        options.Cache.S3.Region = "us-east-1";
        options.Cache.S3.ServiceUrl = "";

        ValidateOptionsResult result = new MirrorOptionsS3Validator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_Succeeds_WhenBackendIsS3_AndServiceUrlIsValid()
    {
        var options = new MirrorOptions();
        options.Cache.Backend = "S3";
        options.Cache.S3.Bucket = "my-bucket";
        options.Cache.S3.AccessKey = "ak";
        options.Cache.S3.SecretKey = "sk";
        options.Cache.S3.Region = "us-east-1";
        options.Cache.S3.ServiceUrl = "https://minio.example:9000";

        ValidateOptionsResult result = new MirrorOptionsS3Validator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_Fails_WhenBackendIsS3_AndServiceUrlIsInvalid()
    {
        var options = new MirrorOptions();
        options.Cache.Backend = "S3";
        options.Cache.S3.Bucket = "my-bucket";
        options.Cache.S3.AccessKey = "ak";
        options.Cache.S3.SecretKey = "sk";
        options.Cache.S3.Region = "us-east-1";
        options.Cache.S3.ServiceUrl = "not-a-valid-url";

        ValidateOptionsResult result = new MirrorOptionsS3Validator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("ServiceUrl", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_ReportsMultipleErrors_WhenServiceUrlAndBucketInvalid()
    {
        var options = new MirrorOptions();
        options.Cache.Backend = "S3";
        options.Cache.S3.Bucket = "";
        options.Cache.S3.AccessKey = "ak";
        options.Cache.S3.SecretKey = "sk";
        options.Cache.S3.Region = "us-east-1";
        options.Cache.S3.ServiceUrl = "not-a-valid-url";

        ValidateOptionsResult result = new MirrorOptionsS3Validator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Bucket", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ServiceUrl", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_Skips_WhenBackendIsS3_Lowercase()
    {
        var options = new MirrorOptions();
        options.Cache.Backend = "s3";
        options.Cache.S3.Bucket = ""; // would fail if not skipped

        ValidateOptionsResult result = new MirrorOptionsS3Validator().Validate(null, options);

        Assert.False(result.Skipped);
        Assert.True(result.Failed);
    }
}
