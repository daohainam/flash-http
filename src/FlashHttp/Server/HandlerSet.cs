using System;
using System.Collections.Generic;
using System.Text;

namespace FlashHttp.Server;
internal class HandlerSet
{
    public readonly Dictionary<string, Action<FlashHttpRequest, FlashHttpResponse>> OnGetHandlers = [];
    public readonly Dictionary<string, Action<FlashHttpRequest, FlashHttpResponse>> OnPostHandlers = [];
    public readonly Dictionary<string, Action<FlashHttpRequest, FlashHttpResponse>> OnPutHandlers = [];
    public readonly Dictionary<string, Action<FlashHttpRequest, FlashHttpResponse>> OnDeleteHandlers = [];
    public readonly Dictionary<string, Action<FlashHttpRequest, FlashHttpResponse>> OnHeadHandlers = [];
    public readonly Dictionary<string, Action<FlashHttpRequest, FlashHttpResponse>> OnPatchHandlers = [];
    public readonly Dictionary<string, Action<FlashHttpRequest, FlashHttpResponse>> OnOptionsHandlers = [];
}
