using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Azure.WebJobs;
using Microsoft.DotNet.Services.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage.Table;
using RolloutScorer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RolloutScorerAzureFunction;

public static class RolloutScorerFunction
{
    private const int ScoringBufferInDays = 2;

    [FunctionName("RolloutScorerFunction")]
    public static async Task Run([TimerTrigger("0 0 0 * * *")]TimerInfo myTimer, ILogger log)
    {
        DefaultAzureCredential tokenProvider = new();

        string deploymentEnvironment = Environment.GetEnvironmentVariable("DeploymentEnvironment") ?? "Staging";
        log.LogInformation($"INFO: Deployment Environment: {deploymentEnvironment}");

        log.LogInformation("INFO: Getting scorecard storage account key and deployment table's SAS URI from KeyVault...");
        SecretClient engKeyVaultClient = new(new Uri(Utilities.KeyVaultUri), tokenProvider);
        SecretClient dotNetEngStatusVaultClient = new(new Uri("https://DotNetEng-Status-Prod.vault.azure.net"), tokenProvider);

        KeyVaultSecret scorecardsStorageAccountKey = await engKeyVaultClient.GetSecretAsync(ScorecardsStorageAccount.KeySecretName);
        KeyVaultSecret deploymentTableSasUriBundle = await dotNetEngStatusVaultClient.GetSecretAsync("deployment-table-sas-uri");

        log.LogInformation("INFO: Getting cloud tables...");
        CloudTable scorecardsTable = Utilities.GetScorecardsCloudTable(scorecardsStorageAccountKey.Value);
        CloudTable deploymentsTable = new(new Uri(deploymentTableSasUriBundle.Value));

        List<ScorecardEntity> scorecardEntries = await GetAllTableEntriesAsync<ScorecardEntity>(scorecardsTable);
        scorecardEntries.Sort((x, y) => x.Date.CompareTo(y.Date));
        List<AnnotationEntity> deploymentEntries =
            await GetAllTableEntriesAsync<AnnotationEntity>(deploymentsTable);
        deploymentEntries.Sort((x, y) => (x.Ended ?? DateTimeOffset.MaxValue).CompareTo(y.Ended ?? DateTimeOffset.MaxValue));
        log.LogInformation($"INFO: Found {scorecardEntries?.Count ?? -1} scorecard table entries and {deploymentEntries?.Count ?? -1} deployment table entries." +
                           $"(-1 indicates that null was returned.)");

        // The deployments we care about are ones that occurred after the last scorecard
        IEnumerable<AnnotationEntity> relevantDeployments =
            deploymentEntries.Where(d => (d.Ended ?? DateTimeOffset.MaxValue) > scorecardEntries.Last().Date.AddDays(ScoringBufferInDays));
        log.LogInformation($"INFO: Found {relevantDeployments?.Count() ?? -1} relevant deployments (deployments which occurred " +
                           $"after the last scorecard). (-1 indicates that null was returned.)");

        if (relevantDeployments.Count() > 0)
        {
            log.LogInformation($"INFO: Checking to see if the most recent deployment occurred more than {ScoringBufferInDays} days ago...");
            // We have only want to score if the buffer period has elapsed since the last deployment
            // Alternatively, if too much time has elapsed since that deployment started, it means there's the BAD BUG and we should just assume this rollout completed
            if ((relevantDeployments.Last().Ended ?? DateTimeOffset.MaxValue) < DateTimeOffset.UtcNow - TimeSpan.FromDays(ScoringBufferInDays)
                || ((relevantDeployments.Last().Started ?? DateTimeOffset.MaxValue) < DateTimeOffset.UtcNow - TimeSpan.FromDays(ScoringBufferInDays + 1) && relevantDeployments.Last().Ended is null))
            {
                var scorecards = new List<Scorecard>();

                log.LogInformation("INFO: Rollouts will be scored. Fetching GitHub PAT...");
                KeyVaultSecret githubPat = await engKeyVaultClient.GetSecretAsync(Utilities.GitHubPatSecretName);

                // We'll score the deployments by service
                foreach (var deploymentGroup in relevantDeployments.GroupBy(d => d.Service))
                {
                    foreach (var deployment in deploymentGroup)
                    {
                        if (deployment.Ended is null)
                        {
                            deployment.Ended = DateTimeOffset.UtcNow;
                            await deploymentsTable.ExecuteAsync(TableOperation.Replace(deployment));
                        }
                    }

                    log.LogInformation($"INFO: Scoring {deploymentGroup?.Count() ?? -1} rollouts for repo '{deploymentGroup.Key}'");
                    RolloutScorer.RolloutScorer rolloutScorer = new()
                    {
                        Repo = deploymentGroup.Key,
                        RolloutStartDate = deploymentGroup.First().Started.GetValueOrDefault().Date,
                        RolloutWeightConfig = StandardConfig.DefaultConfig.RolloutWeightConfig,
                        GithubConfig = StandardConfig.DefaultConfig.GithubConfig,
                        Log = log,
                    };
                    log.LogInformation($"INFO: Finding repo config for {rolloutScorer.Repo}...");
                    rolloutScorer.RepoConfig = StandardConfig.DefaultConfig.RepoConfigs
                        .Find(r => r.Repo == rolloutScorer.Repo);
                    log.LogInformation($"INFO: Repo config: {rolloutScorer.RepoConfig.Repo}");
                    log.LogInformation($"INFO: Finding AzDO config for {rolloutScorer.RepoConfig.AzdoInstance}...");
                    rolloutScorer.AzdoConfig = StandardConfig.DefaultConfig.AzdoInstanceConfigs
                        .Find(a => a.Name == rolloutScorer.RepoConfig.AzdoInstance);

                    log.LogInformation($"INFO: Fetching AzDO PAT from KeyVault...");
                    SecretClient azdoConfigVaultClient = new SecretClient(new Uri(rolloutScorer.AzdoConfig.KeyVaultUri), tokenProvider);
                    KeyVaultSecret azdoPatSecret = await azdoConfigVaultClient.GetSecretAsync(rolloutScorer.AzdoConfig.PatSecretName);
                    rolloutScorer.SetupHttpClient(azdoPatSecret.Value);
                    rolloutScorer.SetupGithubClient(githubPat.Value);

                    log.LogInformation($"INFO: Attempting to initialize RolloutScorer...");
                    try
                    {
                        await rolloutScorer.InitAsync();
                    }
                    catch (ArgumentException e)
                    {
                        log.LogError($"ERROR: Error while processing {rolloutScorer.RolloutStartDate} rollout of {rolloutScorer.Repo}.");
                        log.LogError($"ERROR: {e.Message}");
                        continue;
                    }

                    log.LogInformation($"INFO: Creating rollout scorecard...");
                    scorecards.Add(await Scorecard.CreateScorecardAsync(rolloutScorer));
                    log.LogInformation($"INFO: Successfully created scorecard for {rolloutScorer.RolloutStartDate?.Date} rollout of {rolloutScorer.Repo}.");
                }

                log.LogInformation($"INFO: Uploading results for {string.Join(", ", scorecards.Select(s => s.Repo))}");
                await RolloutUploader.UploadResultsAsync(scorecards, Utilities.GetGithubClient(githubPat.Value),
                    scorecardsStorageAccountKey.Value, StandardConfig.DefaultConfig.GithubConfig, skipPr: deploymentEnvironment != "Production");
            }
            else
            {
                log.LogInformation(relevantDeployments.Last().Ended.HasValue ? $"INFO: Most recent rollout occurred less than two days ago " +
                                                                               $"({relevantDeployments.Last().Service} on {relevantDeployments.Last().Ended.Value}); waiting to score." :
                    $"Most recent rollout ({relevantDeployments.Last().Service}) is still in progress.");
            }
        }
        else
        {
            log.LogInformation($"INFO: Found no rollouts which occurred after last recorded rollout " +
                               $"({(scorecardEntries.Count > 0 ? $"date {scorecardEntries.Last().Date}" : "no rollouts in table")})");
        }
    }

    private static async Task<List<T>> GetAllTableEntriesAsync<T>(CloudTable table) where T : ITableEntity, new()
    {
        List<T> items = new List<T>();
        TableContinuationToken token = null;
        do
        {
            var queryResult = await table.ExecuteQuerySegmentedAsync(new TableQuery<T>(), token);
            foreach (var item in queryResult)
            {
                items.Add(item);
            }
            token = queryResult.ContinuationToken;
        } while (token != null);
        return items;
    }
}
