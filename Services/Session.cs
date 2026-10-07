using Abasto.Domain;

namespace Abasto.Services;

public static class Session
{
    public static PosUser? CurrentUser { get; set; }
}
