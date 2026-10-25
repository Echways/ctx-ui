using System.Net;

namespace CtxUi.Connect.Registration;

public interface IMetadataAddressPolicy
{
    bool AllowsUri(Uri uri);

    bool AllowsAddress(IPAddress address);
}
