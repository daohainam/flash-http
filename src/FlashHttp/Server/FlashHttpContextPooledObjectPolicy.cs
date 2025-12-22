using FlashHttp.Abstractions;
using Microsoft.Extensions.ObjectPool;

namespace FlashHttp.Server
{
    internal class FlashHttpContextPooledObjectPolicy : IPooledObjectPolicy<FlashHttpContext>
    {
        public FlashHttpContext Create()
        {
            return new FlashHttpContext
            {
                Request = default!,
                Response = default!
            };
        }

        public bool Return(FlashHttpContext obj)
        {
            obj.Request = default!;
            obj.Response = default!;
            return true;
        }
    }
}