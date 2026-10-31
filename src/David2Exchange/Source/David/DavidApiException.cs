// SPDX-License-Identifier: MIT
namespace David2Exchange.Source.David;

public sealed class DavidApiException : Exception
{
    public DavidApiException(string message) : base(message)
    {
    }

    public DavidApiException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
