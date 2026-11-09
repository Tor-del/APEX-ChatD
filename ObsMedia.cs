namespace TwitchChatAgent;

internal static class ObsMedia
{
    public static string FileFilter => Ui.T("Изображения и видео") + "|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp;*.mp4;*.webm;*.ogv";

    public static string? ContentType(string? path) => Path.GetExtension(path)?.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".ogv" => "video/ogg",
        _ => null
    };
}

