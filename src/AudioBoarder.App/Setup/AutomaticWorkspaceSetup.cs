using Azure.Core;
using AudioBoarder.App.Configuration;
using AudioBoarder.Core.LLM;
using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services.LLM;
using AudioBoarder.Services.Transcription.Cloud;
using Microsoft.Extensions.Options;
using System.Net.Http;

namespace AudioBoarder.App.Setup;

public sealed record AutomaticWorkspacePlan(
    AzureSubscriptionInfo Subscription,
    AzureAccountInfo? ChatAccount,
    AzureAccountInfo? SpeechAccount,
    string Region,
    string Summary);

public sealed record AutomaticWorkspaceResult(
    AzureModelSelection Models, AzureAccountInfo SpeechResource)
{
    public void ApplyTo(AudioBoarderSettings settings)
    {
        settings.AzureSpeech.ResourceId = SpeechResource.Id;
        settings.AzureSpeech.Region = SpeechResource.Region;
        settings.AzureSpeech.ApiKey = null;
        settings.AzureSpeech.EndSilenceMs = 450;
        Models.ApplyTo(settings);
        settings.Realtime.MinIntervalSeconds = 2;
        settings.Realtime.MinNewSegments = 1;
        settings.Realtime.UseFastDeployment = true;
        var profile = settings.ModelAccounts.Single(p => p.Id == settings.ActiveModelAccountId);
        profile.SpeechResourceId = SpeechResource.Id;
        profile.SpeechRegion = SpeechResource.Region;
    }
}

public interface IWorkspaceConnectionProbe
{
    Task SpeechAsync(TokenCredential credential, AzureAccountInfo resource, CancellationToken ct);
    Task ChatAsync(TokenCredential credential, AzureAccountInfo account, AzureDeploymentInfo model, CancellationToken ct);
}

public sealed class WorkspaceConnectionProbe : IWorkspaceConnectionProbe
{
    public async Task SpeechAsync(TokenCredential credential, AzureAccountInfo resource, CancellationToken ct)
    {
        await using var speech = new AzureSpeechStreamingService(Options.Create(new AzureSpeechSettings
        {
            Region = resource.Region, ResourceId = resource.Id, Credential = credential, EndSilenceMs = 450,
        }));
        await speech.InitializeAsync(ct);
    }

    public async Task ChatAsync(TokenCredential credential, AzureAccountInfo account, AzureDeploymentInfo model, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        var generator = new AzureOpenAIScenePatchGenerator(Options.Create(new AzureOpenAIOptions
        {
            Endpoint = account.Endpoint, DeploymentName = model.Name,
            Model = new(account.Endpoint, model.Name, model.ModelName), Credential = credential,
            AllowJsonObjectFallback = false, MaxOutputTokens = 1200,
        }));
        var now = DateTimeOffset.UtcNow;
        var result = await generator.GenerateAsync(new ScenePatchRequest(new SceneGraph(),
            [new(Guid.NewGuid(), TranscriptSpeaker.Local,
                "Azure Front Door sends HTTPS requests to Azure App Service.", now, now)],
            Mode: GenerationMode.ContinuousExtraction), timeout.Token);
        if (result.Patch.Operations.OfType<AddNode>().Count() < 2 ||
            !result.Patch.Operations.OfType<Connect>().Any())
            throw new InvalidOperationException("The selected model did not produce a usable architecture. Run workspace setup again.");
    }
}

public sealed class AutomaticWorkspaceSetup(
    IAzureModelInventory inventory,
    IAzureProvisioningService provisioning,
    IAzureSpeechResources speechResources,
    IAzureWorkspaceAccess access,
    IWorkspaceConnectionProbe probe)
{
    private const int LiveModelScore = 105;

    public async Task<bool> HasRequiredResourcesAsync(
        TokenCredential credential, AudioBoarderSettings settings, CancellationToken ct)
    {
        var azure = settings.AzureOpenAI;
        if (string.IsNullOrWhiteSpace(azure.SubscriptionId) ||
            string.IsNullOrWhiteSpace(azure.AccountResourceId) ||
            string.IsNullOrWhiteSpace(azure.Endpoint) || string.IsNullOrWhiteSpace(azure.DeploymentName))
            return false;
        var chat = await inventory.GetAccountAsync(credential, azure.AccountResourceId, ct);
        if (chat is null || chat.FailureKind != DiscoveryFailureKind.None ||
            !chat.Deployments.Any(model => model.Name == azure.DeploymentName && model.IsChat && model.IsReady))
            return false;

        var backend = settings.CloudTranscription.Backend.ToLowerInvariant();
        if (backend is "local" or "whisper") return true;
        if (backend == "cloud")
        {
            var resources = await inventory.ListAccountsAsync(credential, azure.SubscriptionId, ct);
            return resources.FailureKind == DiscoveryFailureKind.None && resources.Accounts.Any(account =>
                string.Equals(account.Endpoint.TrimEnd('/'),
                    (settings.CloudTranscription.Endpoint ?? azure.Endpoint).TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
                account.Deployments.Any(model => model.Name == settings.CloudTranscription.DeploymentName &&
                    model.IsTranscription && model.IsReady));
        }
        if (string.IsNullOrWhiteSpace(settings.AzureSpeech.ResourceId) ||
            string.IsNullOrWhiteSpace(settings.AzureSpeech.Region))
            return false;
        var speech = string.Equals(chat.Id, settings.AzureSpeech.ResourceId, StringComparison.OrdinalIgnoreCase)
            ? chat
            : await inventory.GetAccountAsync(credential, settings.AzureSpeech.ResourceId, ct);
        return speech is { FailureKind: DiscoveryFailureKind.None, Kind: "SpeechServices" or "AIServices" } &&
            string.Equals(speech.Region, settings.AzureSpeech.Region, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<AutomaticWorkspacePlan> InspectAsync(
        TokenCredential credential, AudioBoarderSettings settings, CancellationToken ct)
    {
        var subscriptions = await inventory.ListSubscriptionsAsync(credential, ct);
        if (subscriptions.FailureKind != DiscoveryFailureKind.None || subscriptions.Subscriptions.Count == 0)
            throw new InvalidOperationException("This Azure account has no accessible subscription. Ask your administrator for subscription access, then reconnect.");
        var subscription = subscriptions.Subscriptions.FirstOrDefault(s => s.Id == settings.AzureOpenAI.SubscriptionId)
                           ?? subscriptions.Subscriptions[0];
        var resources = await inventory.ListAccountsAsync(credential, subscription.Id, ct);
        if (resources.FailureKind != DiscoveryFailureKind.None)
            throw new InvalidOperationException("Azure did not allow resource discovery. The selected subscription needs resource read access.");
        var available = resources.Accounts.Where(a => a.FailureKind == DiscoveryFailureKind.None).ToArray();
        var chat = available
            .OrderByDescending(a => a.Deployments.Any(d => d.IsReady && d.IsChat && FastScore(d.ModelName) >= LiveModelScore))
            .ThenByDescending(a => a.Id == settings.AzureOpenAI.AccountResourceId)
            .FirstOrDefault();
        var speech = (await speechResources.ListSpeechResourcesAsync(credential, subscription.Id, ct))
            .OrderByDescending(a => a.Id == settings.AzureSpeech.ResourceId)
            .ThenByDescending(a => a.Kind == "SpeechServices")
            .ThenByDescending(a => a.Region == chat?.Region)
            .FirstOrDefault();
        var region = chat?.Region ?? speech?.Region ?? "eastus2";
        var summary = $"Subscription: {subscription.Name}. " +
                      (chat is null ? "Create an Azure AI resource. " : $"Reuse {chat.Name}. ") +
                      (speech is null ? "Prepare streaming Speech. " : $"Connect streaming Speech in {speech.Region}. ") +
                      "AudioBoarder will choose a fast diagram model, check live connections, and save everything for you.";
        return new(subscription, chat, speech, region, summary);
    }

    public async Task<AutomaticWorkspaceResult> ExecuteAsync(
        TokenCredential credential, string? tenantId, AutomaticWorkspacePlan plan, bool approved,
        IProgress<string> progress, CancellationToken ct)
    {
        if (!approved) throw new InvalidOperationException("Approve workspace setup and Azure usage before continuing.");
        var group = plan.ChatAccount is not null
            ? new ResourceIdentifier(plan.ChatAccount.Id).ResourceGroupName!
            : "AudioBoarder";
        progress.Report("Preparing your Azure workspace...");
        var chat = plan.ChatAccount ?? await provisioning.CreateResourceAsync(credential,
            new(plan.Subscription.Id, group, plan.Region, $"audioboarder-{Guid.NewGuid():N}"[..25],
                "AIServices", true, true, true), progress, ct);
        var speech = plan.SpeechAccount ?? await provisioning.CreateResourceAsync(credential,
            new(plan.Subscription.Id, group, plan.Region, $"audioboarder-speech-{Guid.NewGuid():N}"[..31],
                "SpeechServices", true, true, true), progress, ct);

        var fast = chat.Deployments.Where(d => d.IsReady && d.IsChat && FastScore(d.ModelName) >= LiveModelScore)
            .OrderByDescending(d => FastScore(d.ModelName)).FirstOrDefault();
        if (fast is null)
        {
            progress.Report("Choosing a responsive diagram model...");
            var catalog = await provisioning.GetDeploymentCatalogAsync(credential, chat.Id, ct);
            var choices = catalog.Models.Where(m => m.Role == AzureModelRole.Chat && FastScore(m.Name) >= LiveModelScore)
                .OrderByDescending(m => FastScore(m.Name))
                .SelectMany(m => m.Skus.OrderByDescending(s => s.Name == "GlobalStandard")
                    .Select(s => (Model: m, Sku: s)))
                .Where(choice =>
                {
                    var quota = catalog.Quotas.FirstOrDefault(q => q.Name == choice.Sku.UsageName);
                    return quota is null || quota.Limit - quota.Current >= choice.Sku.Minimum;
                }).ToArray();
            if (choices.Length == 0)
                throw new InvalidOperationException("Azure has no compatible fast diagram model with available quota in this resource. Ask your administrator for model quota and run setup again.");
            var choice = choices[0];
            var remaining = catalog.Quotas.FirstOrDefault(q => q.Name == choice.Sku.UsageName);
            var ceiling = Math.Min(choice.Sku.Maximum,
                remaining is null ? choice.Sku.Maximum : (int)Math.Max(0, remaining.Limit - remaining.Current));
            var target = Math.Clamp(Math.Max(10, choice.Sku.DefaultCapacity), choice.Sku.Minimum, ceiling);
            var capacity = choice.Sku.AllowedValues.Count > 0
                ? choice.Sku.AllowedValues.Where(n => n <= ceiling).OrderBy(n => Math.Abs(n - target)).First()
                : choice.Sku.Minimum + (target - choice.Sku.Minimum) / choice.Sku.Step * choice.Sku.Step;
            fast = await provisioning.DeployModelAsync(credential,
                new(chat.Id, $"ab-live-{Guid.NewGuid():N}"[..20], choice.Model, choice.Sku.Name, capacity, true),
                progress, ct);
        }
        progress.Report("Validating the diagram connection...");
        await ValidateWithAccessAsync(
            () => probe.ChatAsync(credential, chat, fast, ct), credential, chat, false, progress, ct);
        progress.Report("Validating continuous live speech...");
        await ValidateWithAccessAsync(
            () => probe.SpeechAsync(credential, speech, ct), credential, speech, true, progress, ct);
        var models = new AzureModelSelection(tenantId, plan.Subscription.Id, chat, fast, fast, "speech", null, false, null);
        return new(models, speech);
    }

    private async Task ValidateWithAccessAsync(
        Func<Task> validate, TokenCredential credential, AzureAccountInfo account,
        bool speech, IProgress<string> progress, CancellationToken ct)
    {
        try { await validate(); return; }
        catch (Exception ex) when (IsAccessFailure(ex))
        {
            progress.Report("Enabling your account's speech/model usage permission...");
            await access.EnsureOwnInferenceAccessAsync(credential, account.Id, speech, true, ct);
        }
        for (var attempt = 0; ; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 5 : 10), ct);
            try { await validate(); return; }
            catch (Exception ex) when (attempt < 5 && IsAccessFailure(ex))
            {
                progress.Report("Azure is applying the access change. Keeping your setup progress...");
            }
        }
    }

    private static bool IsAccessFailure(Exception ex) =>
        ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } ||
        ex is System.ClientModel.ClientResultException { Status: 401 or 403 } ||
        ex is AudioBoarder.Services.Transcription.TranscriptionInitializationException { SafeErrorCode: "authentication_required" };

    internal static int FastScore(string? model) => model?.ToLowerInvariant() switch
    {
        var value when value?.Contains("gpt-4.1-mini") == true => 110,
        var value when value?.Contains("gpt-4o-mini") == true => 105,
        var value when value?.Contains("gpt-5-mini") == true => 100,
        var value when value?.Contains("mini") == true && value.Contains("gpt") => 95,
        var value when value?.Contains("gpt-4.1") == true => 90,
        var value when value?.Contains("gpt-4o") == true => 85,
        _ => 0,
    };
}
