using AudioBoarder.App.Configuration;
using AudioBoarder.Services.Imaging;
using AudioBoarder.Services.LLM;
using AudioBoarder.Services.Transcription.Cloud;

namespace AudioBoarder.App.Setup;

public sealed record AzureDeploymentChoice(AzureAccountInfo Account, AzureDeploymentInfo Deployment)
{
    public string DisplayName => $"{Account.Name} / {Deployment.DisplayName}";
}

public sealed record AzureModelSelection(
    string? TenantId,
    string SubscriptionId,
    AzureAccountInfo Account,
    AzureDeploymentInfo Chat,
    AzureDeploymentInfo? FastChat,
    string TranscriptionBackend,
    AzureDeploymentChoice? Transcription,
    bool EnableImages,
    AzureDeploymentChoice? Image)
{
    public void ApplyTo(AudioBoarderSettings settings)
    {
        var azure = settings.AzureOpenAI;
        azure.TenantId = TenantId;
        azure.SubscriptionId = SubscriptionId;
        azure.AccountResourceId = Account.Id;
        azure.Endpoint = Account.Endpoint;
        azure.DeploymentName = Chat.Name;
        azure.FallbackDeploymentName = FastChat?.Name;
        azure.Model = new(Account.Endpoint, Chat.Name, Chat.ModelName);
        azure.FallbackModel = FastChat is null ? null : new(Account.Endpoint, FastChat.Name, FastChat.ModelName);
        azure.PreferredRegion = Account.Region;
        azure.UseManagedIdentity = true;
        azure.ApiKey = null;
        // An explicit selection must never be replaced by automatic model ranking.
        azure.AutoDiscover = false;
        settings.CloudTranscription.Backend = TranscriptionBackend;
        settings.CloudTranscription.Endpoint = Transcription?.Account.Endpoint;
        settings.CloudTranscription.DeploymentName = Transcription?.Deployment.Name;
        settings.CloudTranscription.Model = Transcription is null ? null :
            new(Transcription.Account.Endpoint, Transcription.Deployment.Name, Transcription.Deployment.ModelName);
        settings.ImageGeneration.Enabled = EnableImages;
        settings.ImageGeneration.Endpoint = Image?.Account.Endpoint;
        settings.ImageGeneration.DeploymentName = Image?.Deployment.Name;
        settings.ImageGeneration.Model = Image is null ? null :
            new(Image.Account.Endpoint, Image.Deployment.Name, Image.Deployment.ModelName);

        var profile = settings.ModelAccounts.FirstOrDefault(p =>
            p.Id == settings.ActiveModelAccountId &&
            string.Equals(p.TenantId, TenantId, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            profile = new ModelAccountSettings { Name = Account.Name };
            settings.ModelAccounts.Add(profile);
        }
        profile.CaptureFrom(azure, settings.CloudTranscription, settings.ImageGeneration);
        settings.ActiveModelAccountId = profile.Id;
    }
}

internal static class AzureRuntimeConfiguration
{
    public static void Apply(
        AudioBoarderSettings selected,
        AudioBoarderSettings runtime,
        AzureOpenAIOptions chat,
        CloudTranscriptionOptions transcription,
        ImageGeneratorOptions image)
    {
        runtime.AzureOpenAI = selected.AzureOpenAI;
        runtime.CloudTranscription = selected.CloudTranscription;
        runtime.ImageGeneration = selected.ImageGeneration;
        runtime.ModelAccounts = selected.ModelAccounts;
        runtime.ActiveModelAccountId = selected.ActiveModelAccountId;

        chat.Endpoint = selected.AzureOpenAI.Endpoint;
        chat.DeploymentName = selected.AzureOpenAI.DeploymentName;
        chat.FallbackDeploymentName = selected.AzureOpenAI.FallbackDeploymentName;
        chat.Model = selected.AzureOpenAI.Model;
        chat.FallbackModel = selected.AzureOpenAI.FallbackModel;
        chat.TenantId = selected.AzureOpenAI.TenantId;
        chat.ApiKey = selected.AzureOpenAI.ApiKey;
        chat.UseManagedIdentity = selected.AzureOpenAI.UseManagedIdentity;

        transcription.Endpoint = selected.CloudTranscription.Endpoint ?? chat.Endpoint;
        transcription.DeploymentName = selected.CloudTranscription.DeploymentName;
        transcription.Backend = selected.CloudTranscription.Backend;
        transcription.Model = selected.CloudTranscription.Model;
        transcription.TenantId = chat.TenantId;
        transcription.ApiKey = chat.ApiKey;
        transcription.UseManagedIdentity = chat.UseManagedIdentity;

        image.Endpoint = selected.ImageGeneration.Endpoint ?? chat.Endpoint;
        image.DeploymentName = selected.ImageGeneration.DeploymentName;
        image.Model = selected.ImageGeneration.Model;
        image.TenantId = chat.TenantId;
        image.ApiKey = chat.ApiKey;
        image.UseManagedIdentity = chat.UseManagedIdentity;
    }
}
