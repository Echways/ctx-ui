using CtxUi.Server;

var app = ServerApp.Build(args);
await ServerApp.InitializeAsync(app);
await app.RunAsync();
