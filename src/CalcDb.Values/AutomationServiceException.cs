using CalcDb.Foundation;

namespace CalcDb.Values;

/// <summary>
/// A platform Automation service (oleaut32) failed or could not allocate: names the service and keeps its HRESULT
/// (K-ERROR: an allocation failure, an invalid argument and a locked array stay distinguishable).
/// </summary>
public sealed class AutomationServiceException : Exception
{
    public AutomationServiceException()
    {
    }

    public AutomationServiceException(string message)
        : base(message)
    {
    }

    public AutomationServiceException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public AutomationServiceException(string service, int hresult)
        : base($"{service} failed ({OleAut.Hr(hresult)}).")
    {
        Service = service;
        HResult = hresult;
    }

    /// <summary>The service that failed, for example <c>SafeArrayCreate</c>.</summary>
    public string? Service { get; }
}
