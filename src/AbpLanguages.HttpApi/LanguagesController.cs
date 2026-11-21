using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.Uow;

namespace AbpLanguages.HttpApi;

/// <summary>
/// The site's languages and texts. Reading the enabled languages and a language's texts is open to anyone, a sign-in
/// page included, since a web app needs its texts before anyone signs in; the rest is for those the host's rights let
/// manage them. The package commits each change itself, so no request-wide unit of work is kept.
/// </summary>
[Route("")]
[UnitOfWork(IsDisabled = true)]
public class LanguagesController(LanguagesService languages, LanguagesCache cache, ILanguagesRights rights) : AbpControllerBase
{
    /// <summary>The site's languages: the enabled ones for anyone, each with its texts' version; every one with its progress for admins (<c>?all=true</c>).</summary>
    [HttpGet("languages")]
    [AllowAnonymous]
    public async Task<LanguagesDto> List([FromQuery] bool all = false, CancellationToken ct = default)
    {
        if (all)
        {
            await EnsureAsync(ct);
        }

        return await languages.ListAsync(all, ct);
    }

    [HttpPost("languages")]
    public async Task<LanguageDto> Create([FromBody] LanguageInput input, CancellationToken ct)
    {
        await EnsureAsync(ct);
        return await languages.CreateAsync(input, ct);
    }

    [HttpPut("languages/{code}")]
    public async Task<LanguageDto> Update(string code, [FromBody] LanguageInput input, CancellationToken ct)
    {
        await EnsureAsync(ct);
        return await languages.UpdateAsync(code, input, ct);
    }

    /// <summary>Takes a language away with its admins' texts. Neither the default nor the source language can go.</summary>
    [HttpDelete("languages/{code}")]
    public async Task Delete(string code, CancellationToken ct)
    {
        await EnsureAsync(ct);
        await languages.DeleteAsync(code, ct);
    }

    /// <summary>
    /// A language's merged texts, from memory. With <c>?v=</c> naming the current version the answer is cached for good
    /// (the version changes with any text); without it, an ETag answers repeats with 304. A language that's off is for admins.
    /// </summary>
    [HttpGet("texts/{code}")]
    [AllowAnonymous]
    public async Task<IActionResult> Texts(string code, [FromQuery] string? v, CancellationToken ct)
    {
        LanguagesSnapshot snapshot = await cache.SnapshotAsync(ct);
        if (snapshot.Find(code) is not { } language || (!language.Enabled && !await rights.MayManageAsync(ct)))
        {
            return NotFound();
        }

        TextBundle bundle = (await cache.BundleAsync(language.Code, ct))!;
        string etag = $"\"{bundle.Hash}\"";
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = v == bundle.Hash ? "public, max-age=31536000, immutable" : "no-cache";
        if (Request.Headers.IfNoneMatch.ToString().Split(',').Any(t => t.Trim() == etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return File(bundle.Json, "application/json; charset=utf-8");
    }

    /// <summary>Every text of every catalog in one language, with its source and status, for the translation editor.</summary>
    [HttpGet("texts/{code}/entries")]
    public async Task<TextEntriesDto> Entries(string code, CancellationToken ct)
    {
        await EnsureAsync(ct);
        return await languages.EntriesAsync(code, ct);
    }

    /// <summary>Sets or takes away admins' texts, all or none.</summary>
    [HttpPut("texts/{code}")]
    public async Task<SaveTextsResult> Save(string code, [FromBody] SaveTextsInput input, CancellationToken ct)
    {
        await EnsureAsync(ct);
        return await languages.SaveTextsAsync(code, input, ct);
    }

    /// <summary>One catalog's texts in one language as nested JSON, for a translator: this language's own texts only.</summary>
    [HttpGet("texts/{code}/export")]
    public async Task<JsonObject> Export(string code, [FromQuery] string? catalog, CancellationToken ct)
    {
        await EnsureAsync(ct);
        return await languages.ExportAsync(code, catalog, ct);
    }

    /// <summary>Takes a translator's file: what differs from what ships becomes admins' texts; unknown keys are listed, never kept.</summary>
    [HttpPost("texts/{code}/import")]
    public async Task<ImportTextsResult> Import(string code, [FromBody] ImportTextsInput input, CancellationToken ct)
    {
        await EnsureAsync(ct);
        return await languages.ImportAsync(code, input, ct);
    }

    private async Task EnsureAsync(CancellationToken ct)
    {
        if (!await rights.MayManageAsync(ct))
        {
            throw new UserFriendlyException("Only the site's admins change its languages and texts.", LanguagesErrorCodes.Forbidden);
        }
    }
}
