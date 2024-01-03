// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

namespace Microsoft.DotNet.DarcLib;

public class GitFileContentContainer
{
    public GitFile VersionDetailsXml { get; set; }

    public GitFile VersionProps { get; set; }

    public GitFile GlobalJson { get; set; }

    public GitFile NugetConfig { get; set; }

    public GitFile DotNetToolsJson { get; set; }

    public List<GitFile> GetFilesToCommit()
    {
        var gitHubCommitsMap = new List<GitFile>
        {
            VersionDetailsXml,
            VersionProps,
            GlobalJson,
            NugetConfig
        };

        if (DotNetToolsJson != null)
        {
            gitHubCommitsMap.Add(DotNetToolsJson);
        }

        return gitHubCommitsMap;
    }
}
