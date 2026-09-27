using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Services.Plex;
using NzbWebDAV.Services.Prefetch;

namespace NzbWebDAV.Api.Controllers.Plex;

/// <summary>All operations require both the API key and a frontend-attested admin session.</summary>
[ApiController]
[Route("api/plex/{**operation}")]
[RequestSizeLimit(512 * 1024)]
public sealed class PlexController(PlexOwnerAuthenticator owners, PlexAccountService accounts,
    PlexServerConfigService servers, PlexCatalogueService catalogue, PlexApiClient api,
    PlexAccountConfigService accountConfig, PlexLibraryMetadataService libraryMetadata,
    ConfigManager config) : PostOnlyApiController
{
    private string _owner = "";

    protected override void AuthenticateRequest(ConfigManager configManager)
    {
        base.AuthenticateRequest(configManager);
        _owner = owners.Validate(HttpContext.Request.Headers[PlexOwnerAuthenticator.HeaderName].ToString());
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var operation = HttpContext.Request.RouteValues["operation"]?.ToString();
        var ct = HttpContext.RequestAborted;
        try
        {
            if (operation == "login/start") return Ok(await accounts.StartAsync(_owner, ct).ConfigureAwait(false));
            if (operation == "servers") return Ok(new { servers = servers.GetServers() });
            if (operation == "accounts") return Ok(new { accounts = accountConfig.GetAccounts() });
            var request = await ReadOperationAsync(ct).ConfigureAwait(false);
            switch (operation)
            {
                case "login/poll":
                    var login = await accounts.PollAsync(_owner, Required(request.Handle), ct).ConfigureAwait(false);
                    var savedAccount = login.State == "connected"
                        ? await accountConfig.SaveLoginAsync(_owner, Required(request.Handle), ct).ConfigureAwait(false) : null;
                    return Ok(new { login.State, login.ExpiresAt, accountId = savedAccount?.Id });
                case "account/select":
                    return Ok(await accountConfig.SelectAsync(_owner, Required(request.AccountId), ct).ConfigureAwait(false));
                case "account/disconnect":
                    await accountConfig.DisconnectAsync(Required(request.AccountId), ct).ConfigureAwait(false);
                    return Ok(new { status = true });
                case "home/users":
                    return Ok(new { users = await accountConfig.GetHomeUsersAsync(_owner, Required(request.Handle), ct).ConfigureAwait(false) });
                case "home/switch":
                    return Ok(await accountConfig.SwitchHomeUserAsync(_owner, Required(request.Handle), Required(request.UserId), request.Pin, ct).ConfigureAwait(false));
                case "login/cancel":
                    accounts.Cancel(_owner, Required(request.Handle));
                    return Ok(new { status = true });
                case "discover":
                    return Ok(new { servers = await accounts.DiscoverAsync(_owner, Required(request.Handle), ct).ConfigureAwait(false) });
                case "test":
                    var identity = await servers.TestAsync(_owner, request.Server ?? throw new ArgumentException("A server is required."), ct).ConfigureAwait(false);
                    return Ok(new { success = true, identity.MachineIdentifier, identity.Version });
                case "save":
                    return Ok(new { servers = await servers.SaveAsync(_owner, request.Servers ?? throw new ArgumentException("A server list is required."), ct).ConfigureAwait(false) });
                case "disconnect":
                    await servers.DisconnectAsync(Required(request.ServerId), ct).ConfigureAwait(false);
                    return Ok(new { status = true });
                case "libraries":
                    return Ok(await catalogue.GetLibrariesAsync(servers.GetServer(Required(request.ServerId)), request.ForceRefresh, ct).ConfigureAwait(false));
                case "library/sync":
                    return Ok(libraryMetadata.RequestSync());
                case "library/status":
                    return Ok(libraryMetadata.Status);
                case "users":
                    return Ok(await catalogue.GetUsersAsync(servers.GetServer(Required(request.ServerId)), request.ForceRefresh, ct).ConfigureAwait(false));
                case "sources":
                    return Ok(await catalogue.GetSourcesAsync(servers.GetServer(Required(request.ServerId)), request.LibraryId, request.ForceRefresh, ct).ConfigureAwait(false));
                case "preview":
                    var previewServer = servers.GetServer(Required(request.ServerId));
                    var previewItems = await api.GetPreviewAsync(previewServer, Required(request.Key), request.Limit, ct).ConfigureAwait(false);
                    previewServer = servers.GetServer(previewServer.Id);
                    var settings = PrefetchSettings.Parse(config.GetEffectiveConfigValue(ConfigKeys.SmartPrefetchSettings));
                    var source = settings.Sources.FirstOrDefault(source => source.ServerId == previewServer.Id && source.Key == request.Key);
                    return Ok(new { items = previewItems.Select(item =>
                    {
                        var status = PrefetchPolicy.DescribeSourceCandidate(item, settings, previewServer.PathMappings, source);
                        return item with { MappingStatus = status.Status, MappingReason = status.Reason };
                    }) });
                default:
                    return NotFound();
            }
        }
        catch (PlexRequestException exception)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { status = false, error = exception.Message });
        }
        catch (InvalidOperationException)
        {
            return Conflict(new { status = false, error = "The Plex operation is not available in the current state." });
        }
    }

    private async Task<PlexOperationRequest> ReadOperationAsync(CancellationToken ct)
    {
        if (!HttpContext.Request.HasJsonContentType()) throw new ArgumentException("Plex operations require a JSON body.");
        try
        {
            return await HttpContext.Request.ReadFromJsonAsync<PlexOperationRequest>(ct).ConfigureAwait(false)
                ?? throw new ArgumentException("A Plex operation body is required.");
        }
        catch (JsonException) { throw new ArgumentException("Invalid Plex operation JSON."); }
    }

    private static string Required(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 2048
        ? value : throw new ArgumentException("A required Plex operation field is missing or too long.");
}

public sealed record PlexOperationRequest
{
    public string? AccountId { get; init; }
    public string? UserId { get; init; }
    public string? Pin { get; init; }
    public string? Handle { get; init; }
    public string? ServerId { get; init; }
    public string? LibraryId { get; init; }
    public string? Key { get; init; }
    public int Limit { get; init; } = 10;
    public bool ForceRefresh { get; init; }
    public PlexServerSaveRequest? Server { get; init; }
    public IReadOnlyList<PlexServerSaveRequest>? Servers { get; init; }
    public override string ToString() => "PlexOperationRequest { credentials = [redacted] }";
}
