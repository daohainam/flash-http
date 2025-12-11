using System;
using System.Collections.Generic;
using System.Text;

namespace FlashHttp.Protocol;
internal interface IProtocolHandler
{
    void Initialize(CancellationToken cancellationToken = default);
    Task<FlashHttpRequest> ReadRequestAsync(CancellationToken cancellationToken = default);
}
