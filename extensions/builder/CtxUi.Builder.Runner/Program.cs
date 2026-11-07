using System.Collections;
using CtxUi.Builder.Runner;

var variables = Environment.GetEnvironmentVariables()
    .Cast<DictionaryEntry>()
    .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value);

using var docker = new DockerEngine(new Uri("unix:///var/run/docker.sock"));
using var probe = new HttpServerProbe();

var app = RunnerEndpoints.BuildApp(
    args,
    new RunnerDependencies(RunnerOptions.FromEnvironment(variables), RunnerPorts.Default, docker, probe,
        TimeProvider.System));

await app.RunAsync();
