// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.DotNet.DarcLib;

public class MergePullRequestParameters
{
    public string CommitToMerge { get; set; }
    public bool SquashMerge { get; set; } = true;
    public bool DeleteSourceBranch { get; set; } = true;
}
