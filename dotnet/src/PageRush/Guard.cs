using System;

namespace PageRush;

internal static class Guard
{
    public static void NotNull(object? value, string paramName)
    {
#if NET
        ArgumentNullException.ThrowIfNull(value, paramName);
#else
        if (value is null)
        {
            throw new ArgumentNullException(paramName);
        }
#endif
    }
}
