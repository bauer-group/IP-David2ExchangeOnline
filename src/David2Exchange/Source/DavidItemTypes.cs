// SPDX-License-Identifier: MIT
namespace David2Exchange.Source;

public static class DavidItemTypes
{
    public static string Kind(string type)
    {
        return type switch
        {
            "1" => "contact",
            "2" => "mail",
            "3" => "fax",
            "4" => "sms",
            "5" => "voice",
            "6" => "chat",
            "7" => "calendar",
            "8" => "link",
            "9" => "video",
            "10" => "audio",
            "11" => "tv",
            "12" => "task",
            _ => "unknown"
        };
    }

    public static bool IsMailLike(string type)
    {
        return type is "2" or "3" or "4" or "5" or "6";
    }

    public static bool IsSupported(string type)
    {
        return IsMailLike(type) || type is "1" or "7" or "12";
    }
}
