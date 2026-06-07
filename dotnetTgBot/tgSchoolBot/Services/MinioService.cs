using System.Text;
using dotnetTgBot.Interfaces;
using Minio;
using Minio.DataModel.Args;

namespace dotnetTgBot.Services;

public class MinioService : IS3Repository
{
    private readonly IMinioClient _minioClient;
    private readonly ILogger<MinioService> _logger;
    private readonly string _bucketName = "dotnet-school-bot";
    private readonly string? _publicEndpointBase;

    public MinioService(
        string endpoint,
        string accessKey,
        string secretKey,
        ILogger<MinioService> logger,
        string? publicEndpoint = null)
    {
        _logger = logger;
        _publicEndpointBase = string.IsNullOrWhiteSpace(publicEndpoint) ? null : publicEndpoint;

        _minioClient = new MinioClient()
            .WithEndpoint(endpoint)
            .WithCredentials(accessKey, secretKey)
            .WithSSL(false)
            .Build();
    }

    private async Task EnsureBucketExistsAsync()
    {
        var exists = await _minioClient.BucketExistsAsync(new BucketExistsArgs().WithBucket(_bucketName));

        if (!exists)
        {
            _logger.LogInformation("Creating MinIO bucket {BucketName}", _bucketName);
            await _minioClient.MakeBucketAsync(new MakeBucketArgs().WithBucket(_bucketName));
            return;
        }

        _logger.LogDebug("MinIO bucket {BucketName} already exists", _bucketName);
    }

    public async Task<string> UploadDocumentAsync(Guid userId, string content)
    {
        await EnsureBucketExistsAsync();

        var documentName = $"{Guid.NewGuid()}.txt";
        var objectName = $"{userId}/{documentName}";

        var data = Encoding.UTF8.GetBytes(content);
        using var stream = new MemoryStream(data);

        var args = new PutObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectName)
            .WithObjectSize(data.Length)
            .WithStreamData(stream)
            .WithContentType("text/plain");

        await _minioClient.PutObjectAsync(args);
        _logger.LogInformation("Uploaded document to MinIO object {ObjectName}", objectName);

        return objectName;
    }

    public async Task<string> DownloadDocumentAsync(string s3Path)
    {
        using var memoryStream = new MemoryStream();
        await _minioClient.GetObjectAsync(new GetObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(s3Path)
            .WithCallbackStream(async stream =>
            {
                await stream.CopyToAsync(memoryStream);
                memoryStream.Seek(0, SeekOrigin.Begin);
            }));

        return Encoding.UTF8.GetString(memoryStream.ToArray());
    }

    public async Task<Stream> GetObjectStreamAsync(string objectName)
    {
        var ms = new MemoryStream();
        await _minioClient.GetObjectAsync(new GetObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectName)
            .WithCallbackStream(async stream =>
            {
                await stream.CopyToAsync(ms);
                ms.Seek(0, SeekOrigin.Begin);
            }));
        ms.Seek(0, SeekOrigin.Begin);
        return ms;
    }

    public async Task DeleteAsync(string s3Path)
    {
        var args = new RemoveObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(s3Path);
        await _minioClient.RemoveObjectAsync(args);
    }

    public async Task<string> UploadFileAsync(string objectName, Stream stream, string contentType, long size)
    {
        await EnsureBucketExistsAsync();

        var args = new PutObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectName)
            .WithObjectSize(size)
            .WithStreamData(stream)
            .WithContentType(contentType);

        await _minioClient.PutObjectAsync(args);
        _logger.LogInformation("Uploaded file to MinIO object {ObjectName}", objectName);

        return objectName;
    }

    public async Task<string?> GetPresignedUrlAsync(string objectName, int expirySeconds = 3600)
    {
        await EnsureBucketExistsAsync();
        var args = new PresignedGetObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectName)
            .WithExpiry(expirySeconds);
        var url = await _minioClient.PresignedGetObjectAsync(args);

        if (string.IsNullOrWhiteSpace(_publicEndpointBase))
        {
            return null;
        }

        if (!Uri.TryCreate(_publicEndpointBase, UriKind.Absolute, out var targetBase))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(targetBase.Host))
        {
            return null;
        }

        if (!string.Equals(targetBase.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(targetBase.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var original = new Uri(url);
            var builder = new UriBuilder(original)
            {
                Scheme = targetBase.Scheme,
                Host = targetBase.Host,
                Port = targetBase.IsDefaultPort ? -1 : targetBase.Port
            };
            url = builder.Uri.ToString();
        }
        catch
        {
            return null;
        }

        return url;
    }
}
