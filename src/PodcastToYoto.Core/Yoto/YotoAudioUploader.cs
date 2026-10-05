using PodcastToYoto.Feeds;

namespace PodcastToYoto.Yoto;

/// <summary>Upload → (skip if Yoto already has the hash) → wait for transcode.</summary>
public sealed class YotoAudioUploader(YotoApiClient api)
{
    public async Task<YotoTranscodedAudio> UploadAsync(DownloadedFile file, string? filename, CancellationToken ct = default)
    {
        var target = await api.GetUploadUrlAsync(file.Sha256Hex, filename, ct);
        if (target.UploadUrl is not null)
        {
            await using var fs = File.OpenRead(file.Path);
            await api.UploadAudioAsync(target.UploadUrl, fs, file.ContentType, ct);
        }
        return await api.WaitForTranscodeAsync(target.UploadId, ct: ct);
    }
}
