namespace FlashHttp.Abstractions;

public interface IFlashHandlerContext
{
    FlashHttpRequest Request { get; }
    FlashHttpResponse Response { get; }
    IServiceProvider Services { get; }
}
public class FlashHandlerContext : IFlashHandlerContext
{
    public FlashHttpRequest Request { get; set; } = default!;
    public FlashHttpResponse Response { get; set; } = default!;

    // Virtual so the server can defer creating a DI scope until a handler actually asks for it.
    public virtual IServiceProvider Services { get; set; } = default!;
}
