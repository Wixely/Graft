// Execute with MCPHub browser_run_code in an isolated instance already navigated
// to the probe's /projects URL. The tool supplies `page`; prepend a C# declaration
// of `fixturePassword` matching the temporary server environment. Never save it here.
await page.Locator("#signin-title").WaitForAsync();
await page.Locator("#username").FillAsync("fixture");
await page.Locator("#password").FillAsync("incorrect");
await page.Locator("button[type=submit]").ClickAsync();
await page.GetByText("Username or password was not accepted.", new() { Exact = true }).WaitForAsync();
if (await page.Locator("#password").InputValueAsync() != "") throw new Exception("Password not cleared after rejection");
await page.Locator("#password").FillAsync(fixturePassword);
await page.Locator("button[type=submit]").ClickAsync();
await page.Locator("#projects-title").WaitForAsync();
await page.GetByText("Graft sandbox", new() { Exact = true }).WaitForAsync();
var cookie = (await page.Context.CookiesAsync()).Single(c => c.Name == "__Host-graft-probe");
if (!cookie.Secure || !cookie.HttpOnly || cookie.SameSite.ToString() != "Strict") throw new Exception("Cookie flags");
await page.ReloadAsync();
await page.Locator("#projects-title").WaitForAsync();
await page.GetByText("Sign out", new() { Exact = true }).ClickAsync();
await page.Locator("#signin-title").WaitForAsync();
await page.ReloadAsync();
await page.Locator("#signin-title").WaitForAsync();
await page.SetViewportSizeAsync(390, 844);
foreach (var selector in new[] { "#username", "#password", "button[type=submit]" })
{
    var box = await page.Locator(selector).BoundingBoxAsync();
    if (box is null || box.X < 0 || box.X + box.Width > 390) throw new Exception("Mobile form overflow");
}
if (await page.Locator("[tabindex='-1']:not(input)").CountAsync() != 0) throw new Exception("Unexpected negative tabindex");
await page.SetViewportSizeAsync(1440, 900);
return "PASS: wrong password, cleared password, login, protected view, cookie flags, authenticated refresh, logout, anonymous refresh, mobile form bounds, tabindex";
