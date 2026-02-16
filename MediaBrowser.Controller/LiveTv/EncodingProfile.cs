using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MediaBrowser.Controller.LiveTv;

/// <summary>
/// Represents the encoding parameters for a transcoding session.
/// Used to match warm pool entries to client requests.
/// </summary>
public class EncodingProfile
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EncodingProfile"/> class.
    /// </summary>
    /// <param name="videoCodec">The output video codec.</param>
    /// <param name="audioCodec">The output audio codec.</param>
    /// <param name="videoBitrate">The output video bitrate.</param>
    /// <param name="audioBitrate">The output audio bitrate.</param>
    /// <param name="width">The output width.</param>
    /// <param name="height">The output height.</param>
    /// <param name="audioChannels">The output audio channels.</param>
    public EncodingProfile(
        string? videoCodec,
        string? audioCodec,
        int? videoBitrate,
        int? audioBitrate,
        int? width,
        int? height,
        int? audioChannels)
    {
        VideoCodec = videoCodec ?? string.Empty;
        AudioCodec = audioCodec ?? string.Empty;
        VideoBitrate = videoBitrate ?? 0;
        AudioBitrate = audioBitrate ?? 0;
        Width = width ?? 0;
        Height = height ?? 0;
        AudioChannels = audioChannels ?? 0;
    }

    /// <summary>
    /// Gets the output video codec.
    /// </summary>
    public string VideoCodec { get; }

    /// <summary>
    /// Gets the output audio codec.
    /// </summary>
    public string AudioCodec { get; }

    /// <summary>
    /// Gets the output video bitrate.
    /// </summary>
    public int VideoBitrate { get; }

    /// <summary>
    /// Gets the output audio bitrate.
    /// </summary>
    public int AudioBitrate { get; }

    /// <summary>
    /// Gets the output width.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Gets the output height.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Gets the output audio channels.
    /// </summary>
    public int AudioChannels { get; }

    /// <summary>
    /// Computes a hash string representing this encoding profile.
    /// </summary>
    /// <returns>A 32-character hex string (MD5 hash).</returns>
    public string ComputeHash()
    {
        var profileString = string.Format(
            CultureInfo.InvariantCulture,
            "{0}|{1}|{2}|{3}|{4}x{5}|{6}ch",
            VideoCodec,
            AudioCodec,
            VideoBitrate,
            AudioBitrate,
            Width,
            Height,
            AudioChannels);

        var hash = MD5.HashData(Encoding.UTF8.GetBytes(profileString));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Returns a string representation of this encoding profile.
    /// </summary>
    /// <returns>A human-readable profile string.</returns>
    public override string ToString()
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}/{1} {2}x{3} V:{4}kbps A:{5}kbps {6}ch",
            VideoCodec,
            AudioCodec,
            Width,
            Height,
            VideoBitrate / 1000,
            AudioBitrate / 1000,
            AudioChannels);
    }
}
