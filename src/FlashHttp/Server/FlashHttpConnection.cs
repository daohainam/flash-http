using Microsoft.Extensions.Logging;

namespace FlashHttp.Server
{
    internal class FlashHttpConnection
    {
        private Stream stream;
        private bool isHttps;
        private HandlerSet handlerSet;
        private ILogger logger;

        public FlashHttpConnection(Stream stream, bool isHttps, HandlerSet handlerSet, ILogger logger)
        {
            this.stream = stream;
            this.isHttps = isHttps;
            this.handlerSet = handlerSet;
            this.logger = logger;
        }

        internal async Task CloseAsync(CancellationToken cancellationToken)
        {
            stream.Flush();
            await stream.DisposeAsync();
        }

        internal async Task ProcessRequestsAsync(CancellationToken cancellationToken)
        {
            var protocolHandler = new Protocol.Http11.Http11ProtocolHandler(stream, isHttps, logger);

            protocolHandler.Initialize();

            var request = await protocolHandler.ReadRequestAsync(cancellationToken);

            while (request != null)
            {
                //var response = await handlerSet.HandleRequestAsync(request, cancellationToken);
                //await protocolHandler.WriteResponseAsync(response, cancellationToken);
                if (!request.KeepAliveRequested)
                {
                    break;
                }
                request = await protocolHandler.ReadRequestAsync(cancellationToken);
            }
        }
    }
}