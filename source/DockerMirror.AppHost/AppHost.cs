IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<ContainerResource> minio = builder.AddContainer("minio", "pgsty/minio:latest")
    .WithArgs("server", "/data", "--console-address", ":9001")
    .WithEnvironment("MINIO_ROOT_USER", "minioadmin")
    .WithEnvironment("MINIO_ROOT_PASSWORD", "minioadmin")
    .WithHttpEndpoint(9000, name: "s3")
    .WithHttpEndpoint(9001, name: "console")
    .WithVolume("dockermirror-minio-data", "/data")
    .WithLifetime(ContainerLifetime.Persistent);

EndpointReference s3Endpoint = minio.GetEndpoint("s3");

IResourceBuilder<ContainerResource> createBucket = builder.AddContainer("minio-createbucket", "pgsty/mc:latest")
    .WithEntrypoint("/bin/sh")
    .WithArgs(
        "-c",
        "until mc alias set local $MINIO_ENDPOINT minioadmin minioadmin; do sleep 1; done && mc mb --ignore-existing local/dockermirror")
    .WithEnvironment("MINIO_ENDPOINT", s3Endpoint)
    .WaitFor(minio);

builder.AddProject<Projects.DockerMirror>("dockermirror")
    .WithEnvironment("Mirror__Cache__Enabled", "true")
    .WithEnvironment("Mirror__Cache__Backend", "S3")
    .WithEnvironment("Mirror__Cache__S3__Bucket", "dockermirror")
    .WithEnvironment("Mirror__Cache__S3__Region", "us-east-1")
    .WithEnvironment("Mirror__Cache__S3__ServiceUrl", s3Endpoint)
    .WithEnvironment("Mirror__Cache__S3__AccessKey", "minioadmin")
    .WithEnvironment("Mirror__Cache__S3__SecretKey", "minioadmin")
    .WithEnvironment("Mirror__Cache__S3__UsePathStyle", "true")
    .WithEnvironment("Mirror__Cache__Eviction__Enabled", "true")
    .WithEnvironment("Mirror__Cache__Eviction__MaxAge", "12:00:00")
    .WaitForCompletion(createBucket);

builder.Build().Run();
