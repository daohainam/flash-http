using System;

namespace FlashHttp.Abstractions;

public sealed class HttpStatusCodes
{
    // 1xx Informational
    public const int Status_100_Continue = 100;
    public const int Status_101_SwitchingProtocols = 101;
    public const int Status_102_Processing = 102;
    public const int Status_103_EarlyHints = 103;

    // 2xx Success
    public const int Status_200_OK = 200;
    public const int Status_201_Created = 201;
    public const int Status_202_Accepted = 202;
    public const int Status_203_NonAuthoritativeInformation = 203;
    public const int Status_204_NoContent = 204;
    public const int Status_205_ResetContent = 205;
    public const int Status_206_PartialContent = 206;
    public const int Status_207_MultiStatus = 207;
    public const int Status_208_AlreadyReported = 208;
    public const int Status_226_IMUsed = 226;

    // 3xx Redirection
    public const int Status_300_MultipleChoices = 300;
    public const int Status_301_MovedPermanently = 301;
    public const int Status_302_Found = 302;
    public const int Status_303_SeeOther = 303;
    public const int Status_304_NotModified = 304;
    public const int Status_305_UseProxy = 305;
    public const int Status_306_SwitchProxy = 306;
    public const int Status_307_TemporaryRedirect = 307;
    public const int Status_308_PermanentRedirect = 308;

    // 4xx Client Error
    public const int Status_400_BadRequest = 400;
    public const int Status_401_Unauthorized = 401;
    public const int Status_402_PaymentRequired = 402;
    public const int Status_403_Forbidden = 403;
    public const int Status_404_NotFound = 404;
    public const int Status_405_MethodNotAllowed = 405;
    public const int Status_406_NotAcceptable = 406;
    public const int Status_407_ProxyAuthenticationRequired = 407;
    public const int Status_408_RequestTimeout = 408;
    public const int Status_409_Conflict = 409;
    public const int Status_410_Gone = 410;
    public const int Status_411_LengthRequired = 411;
    public const int Status_412_PreconditionFailed = 412;
    public const int Status_413_ContentTooLarge = 413;
    public const int Status_414_URITooLong = 414;
    public const int Status_415_UnsupportedMediaType = 415;
    public const int Status_416_RangeNotSatisfiable = 416;
    public const int Status_417_ExpectationFailed = 417;
    public const int Status_418_ImATeapot = 418;
    public const int Status_421_MisdirectedRequest = 421;
    public const int Status_422_UnprocessableContent = 422;
    public const int Status_423_Locked = 423;
    public const int Status_424_FailedDependency = 424;
    public const int Status_425_TooEarly = 425;
    public const int Status_426_UpgradeRequired = 426;
    public const int Status_428_PreconditionRequired = 428;
    public const int Status_429_TooManyRequests = 429;
    public const int Status_431_RequestHeaderFieldsTooLarge = 431;
    public const int Status_451_UnavailableForLegalReasons = 451;

    // 5xx Server Error
    public const int Status_500_InternalServerError = 500;
    public const int Status_501_NotImplemented = 501;
    public const int Status_502_BadGateway = 502;
    public const int Status_503_ServiceUnavailable = 503;
    public const int Status_504_GatewayTimeout = 504;
    public const int Status_505_HTTPVersionNotSupported = 505;
    public const int Status_506_VariantAlsoNegotiates = 506;
    public const int Status_507_InsufficientStorage = 507;
    public const int Status_508_LoopDetected = 508;
    public const int Status_510_NotExtended = 510;
    public const int Status_511_NetworkAuthenticationRequired = 511;
}
