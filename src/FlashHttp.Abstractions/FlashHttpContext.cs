namespace FlashHttp.Abstractions;

public interface IFlashHttpContext
{
    FlashHttpRequest Request { get; }
    FlashHttpResponse Response { get; }
}
public class FlashHttpContext : IFlashHttpContext
{
    public FlashHttpRequest Request { get; set; } = default!;
    public FlashHttpResponse Response { get; set; } = default!;
}
