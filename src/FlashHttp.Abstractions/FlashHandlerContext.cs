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
    public IServiceProvider Services { get; set; } = default!;
}
