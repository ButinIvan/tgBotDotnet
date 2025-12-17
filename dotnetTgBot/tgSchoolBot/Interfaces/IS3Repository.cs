namespace dotnetTgBot.Interfaces;

public interface IS3Repository
{
    Task<string> UploadDocumentAsync(Guid userId, string content);
    Task<string> DownloadDocumentAsync(string s3Path);
    Task DeleteAsync(string s3Path);
    Task<string> UploadFileAsync(string objectName, Stream stream, string contentType, long size);
    Task<string?> GetPresignedUrlAsync(string objectName, int expirySeconds = 3600);
    Task<Stream> GetObjectStreamAsync(string objectName);
}