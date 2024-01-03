// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

namespace Microsoft.DotNet.DarcLib.Models.AzureDevOps;

public class AzureDevOpsPipelineRunDefinition
{
    public AzureDevOpsRunResourcesParameters Resources { get; set; }

    public Dictionary<string, string> TemplateParameters { get; set; }

    public Dictionary<string, AzureDevOpsVariable> Variables { get; set; }
}
