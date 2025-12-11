namespace FlashHttp.Server;
public class FlashHttpServer
{
    private readonly FlashHttpServerOptions _options;

    public FlashHttpServer(FlashHttpServerOptions options)
    {
        _options = options;
    }

    public FlashHttpServer(Action<FlashHttpServerOptions>? configureOptions = null)
    {
        _options = new FlashHttpServerOptions();
        configureOptions?.Invoke(_options);
    }

    private readonly Dictionary<string, Action<FlashHttpRequest, FlashHttpResponse>> _onGetHandlers = [];
    private readonly Dictionary<string, Action<FlashHttpRequest, FlashHttpResponse>> _onPostHandlers = [];
    private readonly Dictionary<string, Action<FlashHttpRequest, FlashHttpResponse>> _onPutHandlers = [];
    private readonly Dictionary<string, Action<FlashHttpRequest, FlashHttpResponse>> _onDeleteHandlers = [];
    private readonly Dictionary<string, Action<FlashHttpRequest, FlashHttpResponse>> _onHeadHandlers = [];
    private readonly Dictionary<string, Action<FlashHttpRequest, FlashHttpResponse>> _onPatchHandlers = [];
    private readonly Dictionary<string, Action<FlashHttpRequest, FlashHttpResponse>> _onOptionsHandlers = [];

    public FlashHttpServer WithHandler(HttpMethodsEnum method, string path, Action<FlashHttpRequest, FlashHttpResponse> handler)
    {
        switch (method)
        {
            case HttpMethodsEnum.Get:
                _onGetHandlers[path] = handler;
                break;
            case HttpMethodsEnum.Post:
                _onPostHandlers[path] = handler;
                break;
            case HttpMethodsEnum.Put:
                _onPutHandlers[path] = handler;
                break;
            case HttpMethodsEnum.Delete:
                _onDeleteHandlers[path] = handler;
                break;
            case HttpMethodsEnum.Head:
                _onHeadHandlers[path] = handler;
                break;
            case HttpMethodsEnum.Patch:
                _onPatchHandlers[path] = handler;
                break;
            case HttpMethodsEnum.Options:
                _onOptionsHandlers[path] = handler;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(method), method, null);
        }

        return this;
    }

}
