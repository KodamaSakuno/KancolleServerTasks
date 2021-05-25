using Dapper;
using Npgsql;
using PuppeteerSharp;
using PuppeteerSharp.Input;
using Serilog;
using System;

await using var pg = new NpgsqlConnection(Environment.GetEnvironmentVariable("DatabaseConn") ?? throw new InvalidOperationException("Missing DatabaseConn"));

var logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();

await new BrowserFetcher().DownloadAsync();

using var browser = await Puppeteer.LaunchAsync(new() { });

try
{
    using var page = await browser.NewPageAsync();

    var response = await page.GoToAsync("http://games.dmm.com/detail/kancolle/");

    if (response.Url != "https://www.dmm.com/my/-/redirect/=/rurl=DRVESVwZTlVZCFRLHVILWk8GWVsfXQFNAwtbSVkEWVMKDVxc")
    {
        logger.Information("Login");

        var loginId = await pg.QuerySingleAsync<string>("SELECT value #>> '{}' FROM store WHERE name = 'login_id';");
        var loginPassword = await pg.QuerySingleAsync<string>("SELECT value #>> '{}' FROM store WHERE name = 'login_password';");

        await page.WaitForSelectorAsync("#game-play-top", new WaitForSelectorOptions() { Timeout = 0 });
        await page.ClickAsync("#game-play-top");

        await page.WaitForSelectorAsync("#login_id");

        await page.FocusAsync("#login_id");
        await page.Keyboard.TypeAsync(loginId, new TypeOptions() { Delay = 50 });
        //await page.ClickAsync("label[for='save_login_id']");
        await page.FocusAsync("#password");
        await page.Keyboard.TypeAsync(loginPassword, new TypeOptions() { Delay = 50 });
        //await page.ClickAsync("label[for='save_password']");
        await page.ClickAsync("label[for='use_auto_login']");

        await page.ClickAsync("input[type=submit]", new ClickOptions() { Delay = 1000 });
        logger.Information("Submit");

        await page.WaitForNavigationAsync();
    }

    var frameHandle = await page.WaitForSelectorAsync("#game_frame");
    var frame = await frameHandle.ContentFrameAsync();
    var gameHandle = await frame.WaitForSelectorAsync("#htmlWrap");

    var url = (await frame.EvaluateExpressionAsync("document.getElementById('htmlWrap').src")).ToString();

    if (url == "http://203.104.209.7/html/maintenance.html")
    {
        logger.Information("Maintenance");
        return;
    }

    await pg.ExecuteAsync("INSERT INTO store VALUES('game_url', @url::jsonb) ON CONFLICT (name) DO UPDATE SET value = excluded.value;", new { url });

    logger.Information("Success");
}
finally
{
    await browser.CloseAsync();
}