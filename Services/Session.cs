using SupermercadoPOS.Domain;

namespace SupermercadoPOS.Services;

public static class Session
{
    public static PosUser? CurrentUser { get; set; }
}
