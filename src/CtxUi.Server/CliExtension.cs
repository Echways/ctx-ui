using System.CommandLine;
using Microsoft.Extensions.Options;
using CtxUi.Server.Extensions;

namespace CtxUi.Server;

public static partial class Cli
{
    private static Command ExtensionCommand(CliLeaf leaf, TextWriter output)
    {
        var archive = new Argument<string>("file") { Description = ServerText.ExtensionFileArgument };
        var id = new Argument<string>("id") { Description = ServerText.ExtensionIdArgument };
        var purge = new Option<bool>("--purge") { Description = ServerText.ExtensionPurgeOption };

        return new Command("extension", ServerText.ExtensionCommand)
        {
            leaf("install", ServerText.ExtensionInstallCommand, [archive], (parse, services, _) =>
            {
                var manifest = InstallerOf(services).Install(Path.GetFullPath(parse.GetValue(archive)!));
                output.WriteLine(string.Format(ServerText.ExtensionInstalled, manifest.Id, manifest.Version));
                return Task.CompletedTask;
            }),
            leaf("list", ServerText.ExtensionListCommand, [], (_, services, _) =>
            {
                var disabled = ExtensionCatalog.DisabledOf(services.GetRequiredService<IOptions<CtxUiOptions>>().Value);
                foreach (var status in InstallerOf(services).List(disabled))
                    output.WriteLine($"{Path.GetFileName(status.Directory)}  {status.Manifest?.Version ?? "-"}  {status.State}");
                return Task.CompletedTask;
            }),
            leaf("remove", ServerText.ExtensionRemoveCommand, [id, purge], (parse, services, _) =>
            {
                InstallerOf(services).Remove(parse.GetValue(id)!, parse.GetValue(purge));
                output.WriteLine(string.Format(ServerText.ExtensionRemoved, parse.GetValue(id)));
                return Task.CompletedTask;
            }),
        };
    }

    private static ExtensionInstaller InstallerOf(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<CtxUiOptions>>().Value;
        return new ExtensionInstaller(ExtensionCatalog.DirectoryOf(options), Path.GetFullPath(options.DataDir));
    }
}
