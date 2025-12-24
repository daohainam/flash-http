using System.Threading;
using System.Threading.Tasks;

namespace FlashHttp.Abstractions;

public delegate ValueTask FlashNext(IFlashHandlerContext context, CancellationToken cancellationToken);

public delegate ValueTask FlashMiddleware(
	IFlashHandlerContext context,
	FlashNext next,
	CancellationToken cancellationToken);