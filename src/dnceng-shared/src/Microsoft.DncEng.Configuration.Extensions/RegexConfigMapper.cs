// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Text.RegularExpressions;

namespace Microsoft.DncEng.Configuration.Extensions;

public static class RegexConfigMapper
{
    public static Func<string, string> Create(Regex regex, Func<string, string> keyMapper)
    {
        return value =>
        {
            if (value == null)
                return null;

            return regex.Replace(value, match =>
            {
                string key = match.Groups["key"].Value;
                return keyMapper(key);
            });
        };
    }
}
