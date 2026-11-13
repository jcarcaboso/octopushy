using System.Xml;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Octopushy.Server.Api;
using Octopushy.Server.Data;

namespace Octopushy.Server.Icons;

/// <summary>The icons the Services page shows: the icons directory, and each service's own. See docs/service-icons.md.</summary>
public static class IconEndpoints
{
    public const int MaxPictureBytes = 1024 * 1024;

    public static readonly string[] PictureTypes = ["image/png", "image/jpeg", "image/webp", "image/svg+xml"];

    public static void MapIcons(this RouteGroupBuilder api)
    {
        api.MapGet("/icons", (IconDirectory icons) => TypedResults.Ok(new IconNamesDto(icons.Names())))
            .WithTags("Inventory")
            .WithName("listIcons");

        api.MapGet("/icons/{name}", Results<FileStreamHttpResult, NotFound> (string name, IconDirectory icons, HttpResponse response) =>
        {
            if (icons.Open(name) is not { } file)
                return TypedResults.NotFound();
            Protect(response, "private, max-age=3600");
            return TypedResults.Stream(file.Content, file.ContentType);
        })
            .Produces(StatusCodes.Status200OK, typeof(Stream), PictureTypes[0], PictureTypes[1..])
            .WithTags("Inventory")
            .WithName("getIcon");

        api.MapGet("/services/{id:guid}/icon", async Task<Results<FileContentHttpResult, NotFound>> (Guid id, OctopushyDb db, HttpResponse response,
            CancellationToken ct) =>
        {
            var picture = await db.ServiceIcons.AsNoTracking().Where(i => i.ResourceId == id && i.Picture != null)
                .Select(i => new { i.Picture, i.ContentType }).FirstOrDefaultAsync(ct);
            if (picture is null)
                return TypedResults.NotFound();
            // The URL the inventory gives carries a version, so a replaced picture is a new URL.
            Protect(response, "private, max-age=31536000, immutable");
            return TypedResults.File(picture.Picture!, picture.ContentType);
        })
            .Produces(StatusCodes.Status200OK, typeof(Stream), PictureTypes[0], PictureTypes[1..])
            .WithTags("Inventory")
            .WithName("getServiceIcon");

        api.MapPut("/services/{id:guid}/icon/picture", async Task<Results<NoContent, NotFound, ProblemHttpResult>> (Guid id, HttpRequest request,
            OctopushyDb db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var media) || media.MediaType.Value?.ToLowerInvariant() is not { } type
                || !PictureTypes.Contains(type))
                return TypedResults.Problem(title: "A picture is a PNG, JPEG, WebP or SVG image, sent as image/png, image/jpeg, image/webp or image/svg+xml.",
                    statusCode: StatusCodes.Status415UnsupportedMediaType);
            if (request.ContentLength > MaxPictureBytes || await ReadAsync(request.Body, ct) is not { } bytes)
                return TypedResults.Problem(title: "A picture is at most 1 MiB.", statusCode: StatusCodes.Status413PayloadTooLarge);
            if (!Matches(type, bytes))
                return TypedResults.Problem(title: $"The picture is not the {type} it was sent as.", statusCode: StatusCodes.Status400BadRequest);
            if (await IconOfAsync(db, id, ct) is not { } icon)
                return TypedResults.NotFound();
            (icon.Name, icon.Picture, icon.ContentType, icon.SetAt) = (null, bytes, type, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            return TypedResults.NoContent();
        })
            .Accepts(typeof(Stream), PictureTypes[0], PictureTypes[1..])
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .WithTags("Inventory")
            .WithName("setServiceIconPicture");

        api.MapPut("/services/{id:guid}/icon/predefined", async Task<Results<NoContent, NotFound, ProblemHttpResult>> (Guid id, ServiceIconChoice choice,
            IconDirectory icons, OctopushyDb db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!icons.Has(choice.Name))
                return TypedResults.Problem(title: $"The icons directory has no file '{choice.Name}'.", statusCode: StatusCodes.Status400BadRequest);
            if (await IconOfAsync(db, id, ct) is not { } icon)
                return TypedResults.NotFound();
            (icon.Name, icon.Picture, icon.ContentType, icon.SetAt) = (choice.Name, null, null, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            return TypedResults.NoContent();
        })
            .WithTags("Inventory")
            .WithName("setServiceIconPredefined");

        api.MapDelete("/services/{id:guid}/icon", async (Guid id, OctopushyDb db, CancellationToken ct) =>
        {
            await db.ServiceIcons.Where(i => i.ResourceId == id).ExecuteDeleteAsync(ct);
            return TypedResults.NoContent();
        })
            .WithTags("Inventory")
            .WithName("deleteServiceIcon");
    }

    /// <summary>A file opened directly is its own document: an SVG must not run script in the app's origin.</summary>
    private static void Protect(HttpResponse response, string cacheControl)
    {
        response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.CacheControl = cacheControl;
    }

    /// <summary>The service's icon row, added when it has none; null when there is no such service.</summary>
    private static async Task<ServiceIcon?> IconOfAsync(OctopushyDb db, Guid id, CancellationToken ct)
    {
        if (!await db.Resources.AnyAsync(r => r.Id == id && r.Kind == ResourceKinds.Service && r.RetiredAt == null, ct))
            return null;
        var icon = await db.ServiceIcons.FirstOrDefaultAsync(i => i.ResourceId == id, ct);
        if (icon is null)
            db.ServiceIcons.Add(icon = new ServiceIcon { ResourceId = id });
        return icon;
    }

    /// <summary>The body; null when it is longer than <see cref="MaxPictureBytes"/>.</summary>
    private static async Task<byte[]?> ReadAsync(Stream body, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxPictureBytes)
                return null;
        }
        return buffer.ToArray();
    }

    /// <summary>Whether the bytes are what <paramref name="type"/> says: the format's signature, or for SVG, XML with an svg root and no DTD.</summary>
    public static bool Matches(string type, byte[] bytes) => type switch
    {
        "image/png" => bytes.AsSpan().StartsWith((byte[])[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]),
        "image/jpeg" => bytes.AsSpan().StartsWith((byte[])[0xFF, 0xD8, 0xFF]),
        "image/webp" => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
        "image/svg+xml" => IsSvg(bytes),
        _ => false,
    };

    private static bool IsSvg(byte[] bytes)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(bytes), settings);
            if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "svg")
                return false;
            while (reader.Read())
            {
            }
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }
}
