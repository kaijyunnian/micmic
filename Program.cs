using System.Diagnostics;
using System.Runtime.InteropServices;

var url = "http://localhost:5080";

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Open the default browser once Kestrel is ready (best-effort).
app.Lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Process.Start("open", url);
        else
            Process.Start("xdg-open", url);
    }
    catch { /* ignore – user can open the URL manually */ }
});

app.Run(url);
