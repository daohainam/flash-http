using FlashHttp.Abstractions;
using Microsoft.Extensions.ObjectPool;

namespace FlashHttp.Server
{
    internal class FlashHttpContextPooledObjectPolicy : IPooledObjectPolicy<FlashHandlerContext>
    {
        public FlashHandlerContext Create()
        {
            return new FlashHandlerContext
            {
                Request = default!,
                Response = default!
            };
        }

        public bool Return(FlashHandlerContext obj)
        {
            obj.Request = default!;
            obj.Response = default!;
            return true;
        }
    }
}