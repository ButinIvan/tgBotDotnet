using System.Text;
using dotnetTgBot.Interfaces;
using Minio;
using Minio.DataModel.Args;

namespace dotnetTgBot.Services;

public class MinioService : IS3Repository
{
    private readonly IMinioClient _minioClient;
    private readonly string _bucketName = "dotnet-school-bot";
    private readonly string? _publicEndpointBase;

    public MinioService(string endpoint, string accessKey, string secretKey, string? publicEndpoint = null)
    {
        _publicEndpointBase = string.IsNullOrWhiteSpace(publicEndpoint) ? null : publicEndpoint;

        _minioClient = new MinioClient()
            .WithEndpoint(endpoint)
            .WithCredentials(accessKey, secretKey)
            .WithSSL(false)
            .Build();
    }

    private async Task EnsureBucketExistsAsync()
    {
        Console.WriteLine($"🔹 Проверяем существование бакета: {_bucketName}");
        var exists = await _minioClient.BucketExistsAsync(new BucketExistsArgs().WithBucket(_bucketName));

        if (!exists)
        {
            Console.WriteLine("🔹 Бакет не найден, создаем новый...");
            await _minioClient.MakeBucketAsync(new MakeBucketArgs().WithBucket(_bucketName));
            Console.WriteLine("✅ Бакет успешно создан.");
        }
        else
        {
            Console.WriteLine("✅ Бакет уже существует.");
        }
    }

    public async Task<string> UploadDocumentAsync(Guid userId, string content)
    {
        await EnsureBucketExistsAsync();

        string documentName = $"{Guid.NewGuid()}.txt";
        string objectName = $"{userId}/{documentName}";

        byte[] data = Encoding.UTF8.GetBytes(content);
        using var stream = new MemoryStream(data);

        Console.WriteLine($"🔹 Загружаем документ: {objectName} в бакет {_bucketName}");

        try
        {
            var args = new PutObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectName)
                .WithObjectSize(data.Length)
                .WithStreamData(stream)
                .WithContentType("text/plain");

            await _minioClient.PutObjectAsync(args);
            Console.WriteLine("✅ Документ успешно загружен в MinIO");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Ошибка загрузки в MinIO: {ex.Message}");
        }
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
        var objectName = $"{s3Path}";
        var args = new RemoveObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectName);
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
            return null; // не отправляем кнопку, если внешний хост не задан

        if (!Uri.TryCreate(_publicEndpointBase, UriKind.Absolute, out var targetBase))
            return null;
        if (string.IsNullOrWhiteSpace(targetBase.Host))
            return null;
        if (!string.Equals(targetBase.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(targetBase.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return null;

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