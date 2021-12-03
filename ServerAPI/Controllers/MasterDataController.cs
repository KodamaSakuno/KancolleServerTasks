using Microsoft.AspNetCore.Mvc;
using ServerAPI.Services;

namespace ServerAPI.Controllers;

[ApiController]
[Route("masterData")]
public class MasterDataController : ControllerBase
{
    private readonly MasterDataService _masterDataService;

    public MasterDataController(MasterDataService masterDataService)
    {
        _masterDataService = masterDataService;
    }

    [HttpGet("latestVersion")]
    public async Task<DateTimeOffset> GetLatestVersion() =>
        await _masterDataService.GetLatestVersionAsync();
}
