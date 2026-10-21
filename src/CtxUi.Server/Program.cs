using CtxUi.Server;

if (args is ["health"])
    return await HealthProbe.RunAsync(HealthProbe.Configuration(), Console.Error);

if (Cli.Handles(args))
    return await Cli.RunAsync(args, Console.Out, Console.Error);

var app = ServerApp.Build(args);
await ServerApp.InitializeAsync(app);
await app.RunAsync();
return 0;
