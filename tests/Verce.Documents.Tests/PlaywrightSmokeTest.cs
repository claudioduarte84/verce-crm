using FluentAssertions;
using Microsoft.Playwright;

namespace Verce.Documents.Tests;

public class PlaywrightSmokeTest
{
    [Fact(Timeout = 60000)]
    public async Task Chromium_can_launch_and_render_a_pdf()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync("<html><body><h1>hello</h1></body></html>");
        var bytes = await page.PdfAsync();
        bytes.Length.Should().BeGreaterThan(0);
    }
}
