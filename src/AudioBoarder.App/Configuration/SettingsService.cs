using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AudioBoarder.App.Configuration;

public sealed record SettingsSecrets(
    string? AzureOpenAIApiKey,
    string? AzureSpeechApiKey,
    bool ClearAzureOpenAIApiKey = false,
    bool ClearAzureSpeechApiKey = false);

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _defaultPath;
    private readonly string? _portableLocalPath;
    private readonly string _localPath;

    public SettingsService()
        : this(
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json"),
            App.UserSettingsPath)
    {
    }

    internal SettingsService(string defaultPath, string localPath)
        : this(defaultPath, null, localPath)
    {
    }

    internal SettingsService(
        string defaultPath,
        string? portableLocalPath,
        string localPath)
    {
        _defaultPath = defaultPath;
        _portableLocalPath = portableLocalPath;
        _localPath = localPath;
    }

    public string LocalSettingsPath => _localPath;

    public AudioBoarderSettings Load()
    {
        var defaults = ReadObject(_defaultPath);
        if (!string.IsNullOrWhiteSpace(_portableLocalPath))
            Merge(defaults, ReadObject(_portableLocalPath));
        var local = ReadObject(_localPath);
        Merge(defaults, local);
        return defaults["AudioBoarder"]?.Deserialize<AudioBoarderSettings>(SerializerOptions)
               ?? new AudioBoarderSettings();
    }

    public async Task SaveAsync(
        AudioBoarderSettings settings,
        SettingsSecrets secrets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(secrets);

        var document = ReadObjectForSave(_localPath);
        var root = EnsureObject(document, "AudioBoarder");

        Set(root, "Theme", settings.Theme);
        Set(root, "TranscriptWindow", settings.TranscriptWindow.ToString("c"));

        var diagramIntent = EnsureObject(root, "DiagramIntent");
        Set(diagramIntent, "SelectionMode", settings.DiagramIntent.SelectionMode.ToString());
        Set(diagramIntent, "PinnedIntent", settings.DiagramIntent.PinnedIntent.ToString());

        var audio = EnsureObject(root, "Audio");
        Set(audio, "CaptureMicrophone", settings.Audio.CaptureMicrophone);
        Set(audio, "CaptureLoopback", settings.Audio.CaptureLoopback);

        var whisper = EnsureObject(root, "Whisper");
        Set(whisper, "ModelSize", settings.Whisper.ModelSize);
        Set(whisper, "Language", settings.Whisper.Language);
        Set(whisper, "WindowSeconds", settings.Whisper.WindowSeconds);

        var transcription = EnsureObject(root, "CloudTranscription");
        Set(transcription, "Backend", settings.CloudTranscription.Backend);
        Set(transcription, "DeploymentName", settings.CloudTranscription.DeploymentName);
        Set(transcription, "Language", settings.CloudTranscription.Language);
        Set(transcription, "WindowSeconds", settings.CloudTranscription.WindowSeconds);
        Set(transcription, "SilenceFlushMs", settings.CloudTranscription.SilenceFlushMs);
        Set(transcription, "MaxBufferedSeconds", settings.CloudTranscription.MaxBufferedSeconds);

        var realtime = EnsureObject(root, "Realtime");
        Set(realtime, "MinIntervalSeconds", settings.Realtime.MinIntervalSeconds);
        Set(realtime, "MinNewSegments", settings.Realtime.MinNewSegments);
        Set(realtime, "DeepPauseSeconds", settings.Realtime.DeepPauseSeconds);
        Set(realtime, "UseFastDeployment", settings.Realtime.UseFastDeployment);
        Set(realtime, "AzureIconsPath", settings.Realtime.AzureIconsPath);

        var azure = EnsureObject(root, "AzureOpenAI");
        Set(azure, "TenantId", settings.AzureOpenAI.TenantId);
        Set(azure, "SubscriptionId", settings.AzureOpenAI.SubscriptionId);
        Set(azure, "Endpoint", settings.AzureOpenAI.Endpoint);
        Set(azure, "DeploymentName", settings.AzureOpenAI.DeploymentName);
        Set(azure, "FallbackDeploymentName", settings.AzureOpenAI.FallbackDeploymentName);
        Set(azure, "UseManagedIdentity", settings.AzureOpenAI.UseManagedIdentity);
        Set(azure, "AutoDiscover", settings.AzureOpenAI.AutoDiscover);
        Set(azure, "PreferredRegion", settings.AzureOpenAI.PreferredRegion);
        SetSecret(
            azure,
            "ApiKey",
            secrets.AzureOpenAIApiKey,
            secrets.ClearAzureOpenAIApiKey);

        var speech = EnsureObject(root, "AzureSpeech");
        Set(speech, "Region", settings.AzureSpeech.Region);
        Set(speech, "ResourceId", settings.AzureSpeech.ResourceId);
        Set(speech, "Language", settings.AzureSpeech.Language);
        Set(speech, "EndSilenceMs", settings.AzureSpeech.EndSilenceMs);
        SetSecret(
            speech,
            "ApiKey",
            secrets.AzureSpeechApiKey,
            secrets.ClearAzureSpeechApiKey);

        var sessions = EnsureObject(root, "Sessions");
        Set(sessions, "AutoSave", settings.Sessions.AutoSave);
        Set(sessions, "OfferRestoreOnLaunch", settings.Sessions.OfferRestoreOnLaunch);

        var diagnostics = EnsureObject(root, "Diagnostics");
        Set(diagnostics, "EnableLocalPerformanceTelemetry",
            settings.Diagnostics.EnableLocalPerformanceTelemetry);

        var images = EnsureObject(root, "ImageGeneration");
        Set(images, "Enabled", settings.ImageGeneration.Enabled);
        Set(images, "DeploymentName", settings.ImageGeneration.DeploymentName);

        var directory = Path.GetDirectoryName(_localPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = $"{_localPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream, document, SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, _localPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static JsonObject ReadObject(string path)
    {
        if (!File.Exists(path))
            return new JsonObject();

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static JsonObject ReadObjectForSave(string path)
    {
        if (!File.Exists(path))
            return new JsonObject();

        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
               ?? throw new JsonException("The local settings root must be a JSON object.");
    }

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, sourceValue) in source)
        {
            if (sourceValue is JsonObject sourceObject &&
                target[key] is JsonObject targetObject)
            {
                Merge(targetObject, sourceObject);
            }
            else
            {
                target[key] = sourceValue?.DeepClone();
            }
        }
    }

    private static JsonObject EnsureObject(JsonObject parent, string name)
    {
        var existingName = parent.Select(pair => pair.Key).FirstOrDefault(
            key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        if (existingName is not null && parent[existingName] is JsonObject existing)
            return existing;

        var created = new JsonObject();
        parent[existingName ?? name] = created;
        return created;
    }

    private static void Set<T>(JsonObject parent, string name, T value)
    {
        var existingName = parent.Select(pair => pair.Key).FirstOrDefault(
            key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        parent[existingName ?? name] = JsonSerializer.SerializeToNode(value, SerializerOptions);
    }

    private static void SetSecret(
        JsonObject parent,
        string name,
        string? explicitlyEnteredValue,
        bool clear)
    {
        var existingName = parent.Select(pair => pair.Key).FirstOrDefault(
            key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        if (clear)
        {
            // A null tombstone intentionally overrides a key inherited from the
            // executable-adjacent portable layer. Removing the user property would
            // merely expose that lower-layer secret again.
            parent[existingName ?? name] = null;
            return;
        }
        if (!string.IsNullOrWhiteSpace(explicitlyEnteredValue))
            Set(parent, name, explicitlyEnteredValue);
    }
}
