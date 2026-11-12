using ClaudeUsageWidget;

// Writes the package / widget icons (drawn by AppLogo) into the main project's Assets folder.
//   dotnet run --project tools\RenderIcons
var assets = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Assets"));
if (args.Length == 1) assets = args[0];

AppLogo.SavePng(Path.Combine(assets, "Square44x44Logo.png"), 44, 0.06f);
AppLogo.SavePng(Path.Combine(assets, "Square150x150Logo.png"), 150, 0.22f);
AppLogo.SavePng(Path.Combine(assets, "StoreLogo.png"), 50, 0.06f);
AppLogo.SavePng(Path.Combine(assets, "WidgetIcon.png"), 64, 0.04f);
AppLogo.SavePng(Path.Combine(assets, "Logo256.png"), 256, 0.06f);
Console.WriteLine("Icons written to " + assets);
