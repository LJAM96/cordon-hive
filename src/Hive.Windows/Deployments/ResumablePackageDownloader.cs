using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Hive.Windows.Deployments;

public sealed record PackageDownloadResult(
    bool Success,
    string ReasonCode,
    string? DestinationPath,
    long BytesOnDisk,
    string? ActualSha256,
    string Message);

/// <summary>
/// Downloads release packages with HTTP Range resume support and fail-closed SHA-256 verification.
/// </summary>
public sealed class ResumablePackageDownloader
{
    private const int BufferSize = 81920;
    private readonly HttpClient _httpClient;

    public ResumablePackageDownloader(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public async Task<PackageDownloadResult> DownloadAsync(
        Uri packageUri,
        string destinationPath,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            return new PackageDownloadResult(
                Success: false,
                ReasonCode: "invalid_expected_sha256",
                DestinationPath: null,
                BytesOnDisk: 0,
                ActualSha256: null,
                Message: "Expected SHA-256 digest must not be empty.");
        }

        var fullDestination = Path.GetFullPath(destinationPath);
        var partPath = $"{fullDestination}.part";
        var parentDir = Path.GetDirectoryName(fullDestination);
        if (!string.IsNullOrEmpty(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        var existingOffset = 0L;
        if (File.Exists(partPath))
        {
            existingOffset = new FileInfo(partPath).Length;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, packageUri);
        if (existingOffset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingOffset, null);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return new PackageDownloadResult(
                Success: false,
                ReasonCode: "download_transport_error",
                DestinationPath: null,
                BytesOnDisk: existingOffset,
                ActualSha256: null,
                Message: $"Package download transport error: {ex.Message}");
        }

        using (response)
        {
            FileMode writeMode;
            if (existingOffset > 0 && response.StatusCode == HttpStatusCode.PartialContent)
            {
                writeMode = FileMode.Append;
            }
            else if (response.StatusCode == HttpStatusCode.OK)
            {
                writeMode = FileMode.Create;
            }
            else
            {
                return new PackageDownloadResult(
                    Success: false,
                    ReasonCode: "download_http_error",
                    DestinationPath: null,
                    BytesOnDisk: existingOffset,
                    ActualSha256: null,
                    Message: $"Package download failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).");
            }

            try
            {
                await using var responseStream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);

                await using var fileStream = new FileStream(
                    partPath,
                    writeMode,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    useAsync: true);

                await responseStream.CopyToAsync(fileStream, BufferSize, cancellationToken).ConfigureAwait(false);
                await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                var currentBytes = File.Exists(partPath) ? new FileInfo(partPath).Length : 0L;
                return new PackageDownloadResult(
                    Success: false,
                    ReasonCode: "download_stream_error",
                    DestinationPath: null,
                    BytesOnDisk: currentBytes,
                    ActualSha256: null,
                    Message: $"Package download interrupted: {ex.Message}");
            }
        }

        string actualSha256;
        long totalBytes;
        await using (var hashStream = new FileStream(
            partPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            useAsync: true))
        {
            totalBytes = hashStream.Length;
            var hashBytes = await SHA256.HashDataAsync(hashStream, cancellationToken).ConfigureAwait(false);
            actualSha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        if (!string.Equals(actualSha256, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(partPath);
            TryDeleteFile(fullDestination);
            return new PackageDownloadResult(
                Success: false,
                ReasonCode: "sha256_mismatch",
                DestinationPath: null,
                BytesOnDisk: 0,
                ActualSha256: actualSha256,
                Message: $"Downloaded package SHA-256 '{actualSha256}' did not match expected '{expectedSha256.Trim()}'. Corrupt file deleted.");
        }

        File.Move(partPath, fullDestination, overwrite: true);

        return new PackageDownloadResult(
            Success: true,
            ReasonCode: "download_verified",
            DestinationPath: fullDestination,
            BytesOnDisk: totalBytes,
            ActualSha256: actualSha256,
            Message: "Package downloaded and SHA-256 verified.");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of corrupt file.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of corrupt file.
        }
    }
}
