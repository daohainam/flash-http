using System;
using System.Collections.Generic;
using System.Text;

namespace FlashHttp.Abstractions;

public readonly struct HttpHeader
{
    public string Name { get; }
    public string Value { get; }

    public HttpHeader(string name, string value)
    {
        Name = name;
        Value = value;
    }
}
