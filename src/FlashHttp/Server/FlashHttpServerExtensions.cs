using FlashHttp.Abstractions;
using System;
using System.Collections.Generic;
using System.Text;

namespace FlashHttp.Server;
public static class FlashHttpServerExtensions
{
    public static FlashHttpServer WithGetHandler(this FlashHttpServer server, string path, Action<FlashHttpRequest, FlashHttpResponse> handler)
    {
        return server.WithHandler(HttpMethodsEnum.Get, path, handler);
    }
    public static FlashHttpServer WithPostHandler(this FlashHttpServer server, string path, Action<FlashHttpRequest, FlashHttpResponse> handler)
    {
        return server.WithHandler(HttpMethodsEnum.Post, path, handler);
    }
    public static FlashHttpServer WithPutHandler(this FlashHttpServer server, string path, Action<FlashHttpRequest, FlashHttpResponse> handler)
    {
        return server.WithHandler(HttpMethodsEnum.Put, path, handler);
    }
    public static FlashHttpServer WithDeleteHandler(this FlashHttpServer server, string path, Action<FlashHttpRequest, FlashHttpResponse> handler)
    {
        return server.WithHandler(HttpMethodsEnum.Delete, path, handler);
    }
    public static FlashHttpServer WithPatchHandler(this FlashHttpServer server, string path, Action<FlashHttpRequest, FlashHttpResponse> handler)
    {
        return server.WithHandler(HttpMethodsEnum.Patch, path, handler);
    }
    public static FlashHttpServer WithHeadHandler(this FlashHttpServer server, string path, Action<FlashHttpRequest, FlashHttpResponse> handler)
    {
        return server.WithHandler(HttpMethodsEnum.Head, path, handler);
    }
    public static FlashHttpServer WithOptionsHandler(this FlashHttpServer server, string path, Action<FlashHttpRequest, FlashHttpResponse> handler)
    {
        return server.WithHandler(HttpMethodsEnum.Options, path, handler);
    }
}
