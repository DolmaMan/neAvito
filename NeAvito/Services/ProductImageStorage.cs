using SkiaSharp;
namespace NeAvito.Services;

public sealed class ProductImageStorage(IWebHostEnvironment environment, IConfiguration configuration, ILogger<ProductImageStorage> logger)
{
    public const long MaxBytes = 5 * 1024 * 1024;
    private readonly string root = configuration["Images:StoragePath"]
        ?? Path.Combine(environment.ContentRootPath, "App_Data", "product-images");
    public string PathFor(Guid id) => Path.Combine(root, $"{id:N}.webp");

    public async Task SaveAsync(Guid id, IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length is <= 0 or > MaxBytes)
            throw new InvalidImageException("Размер фотографии должен быть от 1 байта до 5 МБ.");
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp"))
            throw new InvalidImageException("Поддерживаются JPEG, PNG и WebP.");
        await using var input = file.OpenReadStream();
        using var codec = SKCodec.Create(input);
        if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp))
            throw new InvalidImageException("Файл не является поддерживаемым изображением.");
        if (codec.FrameCount > 1 || codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
            codec.Info.Width > 6000 || codec.Info.Height > 6000 || (long)codec.Info.Width * codec.Info.Height > 12000000)
            throw new InvalidImageException("Допустима одна фотография до 12 мегапикселей и до 6000 пикселей по стороне.");
        using var bitmap = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
            throw new InvalidImageException("Изображение повреждено.");
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, 85);
        if (encoded is null || encoded.Size > MaxBytes)
            throw new InvalidImageException("Не удалось обработать фотографию в пределах 5 МБ.");
        Directory.CreateDirectory(root);
        try
        {
            await using var output = new FileStream(PathFor(id), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await output.WriteAsync(encoded.ToArray(), cancellationToken);
        }
        catch { Delete(id); throw; }
    }
    public void Delete(Guid id)
    {
        try { File.Delete(PathFor(id)); }
        catch (IOException exception) { logger.LogWarning(exception, "Не удалось удалить фотографию {ImageId}", id); }
        catch (UnauthorizedAccessException exception) { logger.LogWarning(exception, "Не удалось удалить фотографию {ImageId}", id); }
    }
}
public sealed class InvalidImageException(string message) : Exception(message);
