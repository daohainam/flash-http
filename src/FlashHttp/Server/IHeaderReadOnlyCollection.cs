using System;
using System.Collections.Generic;
using System.Text;

namespace FlashHttp.Server;
internal interface IHeaderReadOnlyCollection
{
    bool TryGetValue(string name, out string value);
}
