using Dapper;
using Microsoft.Playwright;
using Npgsql;
using Serilog;
using System;

await using var pg = new NpgsqlConnection(Environment.GetEnvironmentVariable("DatabaseConn") ?? throw new InvalidOperationException("Missing DatabaseConn"));

var logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions()
{
    Args = new[] { "--no-sandbox" },
});

await using var context = await browser.NewContextAsync(new()
{
    StorageStatePath = "state.json",
});

var page = await context.NewPageAsync();

var response = await page.GotoAsync("http://games.dmm.com/detail/kancolle/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

if (response.Url is "http://games.dmm.com/detail/kancolle/")
{
    logger.Information("Login");

    var loginId = await pg.QuerySingleAsync<string>("SELECT value #>> '{}' FROM store WHERE name = 'login_id';");
    var loginPassword = await pg.QuerySingleAsync<string>("SELECT value #>> '{}' FROM store WHERE name = 'login_password';");

    await page.WaitForSelectorAsync("#game-play-top");
    await page.ClickAsync("#game-play-top");

    await page.WaitForSelectorAsync("#login_id");

    await page.FocusAsync("#login_id");
    await page.Keyboard.TypeAsync(loginId, new() { Delay = 50 });
    await page.ClickAsync("label[for='save_login_id']");

    await page.FocusAsync("#password");
    await page.Keyboard.TypeAsync(loginPassword, new() { Delay = 50 });
    await page.ClickAsync("label[for='save_password']");

    await page.ClickAsync("label[for='use_auto_login']");
    await page.ClickAsync("input[type=submit]");
    logger.Information("Submit");

    await page.WaitForNavigationAsync();

    await context.StorageStateAsync(new()
    {
        Path = "state.json",
    });
}

var frameHandle = await page.WaitForSelectorAsync("#game_frame");
var frame = await frameHandle.ContentFrameAsync();
var gameHandle = await frame.WaitForSelectorAsync("#htmlWrap");

var url = (await frame.EvaluateAsync("document.getElementById('htmlWrap').src")).ToString();

if (url is "http://203.104.209.7/html/maintenance.html")
{
    logger.Information("Maintenance");
    return;
}

await pg.ExecuteAsync("INSERT INTO store VALUES('game_url', @url::jsonb) ON CONFLICT (name) DO UPDATE SET value = excluded.value;", new { url = '"' + url + '"' });

logger.Information("Success");
