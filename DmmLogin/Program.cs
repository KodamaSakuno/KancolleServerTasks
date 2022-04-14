using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using Serilog;
using StackExchange.Redis;
using System;
using System.IO;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();

using var logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .CreateLogger();

const string StateFilename = "/var/playwright/dmm.json";

using var redis = ConnectionMultiplexer.Connect(configuration["Redis:Host"]);

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.ConnectAsync(configuration["Playwright:Endpoint"]);

await using var context = await browser.NewContextAsync(File.Exists(StateFilename) ? new()
{
    StorageStatePath = StateFilename,
} : null);

var page = await context.NewPageAsync();

var response = await page.GotoAsync("http://games.dmm.com/detail/kancolle/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

if (response.Url is "http://games.dmm.com/detail/kancolle/")
{
    logger.Information("Login");

    var loginId = Environment.GetEnvironmentVariable("LoginId") ?? throw new InvalidOperationException("Missing LoginId");
    var loginPassword = Environment.GetEnvironmentVariable("LoginPassword") ?? throw new InvalidOperationException("LoginPassword");

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
        Path = StateFilename,
    });
}

var frameHandle = await page.WaitForSelectorAsync("#game_frame");
var frame = await frameHandle.ContentFrameAsync();
var gameHandle = await frame.WaitForSelectorAsync("#htmlWrap");

var url = (await frame.EvaluateAsync("document.getElementById('htmlWrap').src")).ToString();

var db = redis.GetDatabase();
var transaction = db.CreateTransaction();

if (url is "http://203.104.209.7/html/maintenance.html")
{
    _ = transaction.HashSetAsync("game", "maintenance", true);

    logger.Information("Maintenance");
}
else
{
    _ = transaction.HashSetAsync("game", "url", url);
    _ = transaction.HashSetAsync("game", "maintenance", false);

    logger.Information("Success");
}

await transaction.ExecuteAsync();
