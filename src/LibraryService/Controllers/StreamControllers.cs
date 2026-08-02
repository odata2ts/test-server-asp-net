using Library.Catalog;
using LibraryService.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Routing.Controllers;

namespace LibraryService.Controllers;

/// <summary>
/// Media entity streams and the <c>Edm.Stream</c> property.
///
/// The reference model has three of them, deliberately in different positions: <c>EBook</c> is a media
/// entity *inside* the inheritance hierarchy, <c>AudiobookChapter</c> is a media entity that is at the
/// same time a *contained* entity, and <c>Audiobook.Sample</c> is a stream *property* rather than an
/// entity's content.
///
/// Routed explicitly. The media-entity convention would cover the first case, but not a contained media
/// entity, and naming the routes here keeps the three cases visibly distinct.
/// </summary>
public class MediaStreamController(LibraryData data) : ODataController
{
    private const string DefaultContentType = "application/octet-stream";

    /// <summary>Content of a media entity, e.g. an <c>EBook</c>.</summary>
    [HttpGet("odata/v4/library/Media({key})/$value")]
    public IActionResult GetContent([FromRoute] Guid key)
    {
        if (data.Media.All(m => m.Id != key))
        {
            return NotFound();
        }

        return data.EntityContent.TryGetValue(key, out var content)
            ? File(content.Bytes, content.ContentType)
            // The entity exists but carries no content yet - "no content", not "not found".
            : NoContent();
    }

    /// <summary>Replaces the content of a media entity.</summary>
    [HttpPut("odata/v4/library/Media({key})/$value")]
    public async Task<IActionResult> PutContent([FromRoute] Guid key)
    {
        if (data.Media.All(m => m.Id != key))
        {
            return NotFound();
        }

        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer);
        data.EntityContent[key] = new MediaContent(
            Request.ContentType ?? DefaultContentType,
            buffer.ToArray());

        return NoContent();
    }

    /// <summary>
    /// Clears the content of a media entity.
    ///
    /// 404 says the *entity* is unknown, not that it currently has no content: an entity without content
    /// answers 204 on GET above, so reporting 404 here for the same state would contradict it. Deleting
    /// twice therefore succeeds twice, which is what makes DELETE idempotent.
    /// </summary>
    [HttpDelete("odata/v4/library/Media({key})/$value")]
    public IActionResult DeleteContent([FromRoute] Guid key)
    {
        if (data.Media.All(m => m.Id != key))
        {
            return NotFound();
        }

        data.EntityContent.Remove(key);
        return NoContent();
    }

    /// <summary>
    /// The <c>Sample</c> stream property of an audiobook. Reached through the type cast, because the
    /// property is declared on <c>Audiobook</c> rather than on <c>Medium</c>.
    /// </summary>
    [HttpGet("odata/v4/library/Media({key})/Library.Catalog.Audiobook/Sample")]
    public IActionResult GetSample([FromRoute] Guid key)
    {
        if (data.Media.OfType<Audiobook>().All(a => a.Id != key))
        {
            return NotFound();
        }

        return data.SampleContent.TryGetValue(key, out var content)
            ? File(content.Bytes, content.ContentType)
            : NoContent();
    }

    [HttpPut("odata/v4/library/Media({key})/Library.Catalog.Audiobook/Sample")]
    public async Task<IActionResult> PutSample([FromRoute] Guid key)
    {
        if (data.Media.OfType<Audiobook>().All(a => a.Id != key))
        {
            return NotFound();
        }

        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer);
        data.SampleContent[key] = new MediaContent(Request.ContentType ?? DefaultContentType, buffer.ToArray());
        return NoContent();
    }

    /// <summary>
    /// Clears the <c>Sample</c> stream property.
    ///
    /// A stream property always exists as part of its entity - its content being absent is a state, not a
    /// missing resource - so this answers 204 whether or not there was content, and 404 only for an
    /// unknown audiobook. Spec: OData V4.01 Part 1, "Deleting a Stream Property".
    /// </summary>
    [HttpDelete("odata/v4/library/Media({key})/Library.Catalog.Audiobook/Sample")]
    public IActionResult DeleteSample([FromRoute] Guid key)
    {
        if (data.Media.OfType<Audiobook>().All(a => a.Id != key))
        {
            return NotFound();
        }

        data.SampleContent.Remove(key);
        return NoContent();
    }

    /// <summary>Content of a contained media entity: a chapter of an audiobook.</summary>
    [HttpGet("odata/v4/library/Media({key})/Library.Catalog.Audiobook/Chapters({chapterKey})/$value")]
    public IActionResult GetChapterContent([FromRoute] Guid key, [FromRoute] int chapterKey)
    {
        if (!ChapterExists(key, chapterKey))
        {
            return NotFound();
        }

        return data.ChapterContent.TryGetValue((key, chapterKey), out var content)
            ? File(content.Bytes, content.ContentType)
            : NoContent();
    }

    [HttpPut("odata/v4/library/Media({key})/Library.Catalog.Audiobook/Chapters({chapterKey})/$value")]
    public async Task<IActionResult> PutChapterContent([FromRoute] Guid key, [FromRoute] int chapterKey)
    {
        if (!ChapterExists(key, chapterKey))
        {
            return NotFound();
        }

        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer);
        data.ChapterContent[(key, chapterKey)] =
            new MediaContent(Request.ContentType ?? DefaultContentType, buffer.ToArray());

        return NoContent();
    }

    private bool ChapterExists(Guid audiobookId, int chapterId) =>
        data.Media.OfType<Audiobook>().FirstOrDefault(a => a.Id == audiobookId)?.Chapters
            .Any(c => c.Id == chapterId) == true;
}
