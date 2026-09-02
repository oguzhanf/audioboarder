using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioBoarder.Core.Patch;

/// <summary>
/// Lenient enum converter for LLM-produced JSON. Large models frequently emit
/// plausible synonyms for an enum ("database" for <c>data_store</c>, "service"
/// for <c>entity</c>, "depends" for <c>dependency</c>). The default
/// <see cref="JsonStringEnumConverter"/> throws on these, which discards the
/// ENTIRE scene patch and freezes the diagram. This converter normalises the
/// value, maps known synonyms, and falls back to a sane default instead of
/// throwing — so a single odd field can never lose a whole update.
/// </summary>
public sealed class TolerantEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var converterType = typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }

    private sealed class TolerantEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
    {
        private static readonly Dictionary<string, TEnum> Lookup = BuildLookup();
        private static readonly TEnum Default = ResolveDefault();

        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var n))
                return Enum.IsDefined(typeof(TEnum), (int)n) ? (TEnum)Enum.ToObject(typeof(TEnum), n) : Default;

            var raw = reader.GetString();
            if (string.IsNullOrWhiteSpace(raw)) return Default;
            var key = Normalize(raw);
            if (Lookup.TryGetValue(key, out var value)) return value;
            // Last resort: substring containment against known keys.
            foreach (var (k, v) in Lookup)
                if (key.Contains(k) || k.Contains(key)) return v;
            return Default;
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
            => writer.WriteStringValue(ToSnakeCase(value.ToString()));

        private static Dictionary<string, TEnum> BuildLookup()
        {
            var map = new Dictionary<string, TEnum>(StringComparer.Ordinal);
            foreach (var name in Enum.GetNames<TEnum>())
            {
                var v = Enum.Parse<TEnum>(name);
                map[Normalize(name)] = v;
            }
            foreach (var (synonym, target) in SynonymsFor(typeof(TEnum)))
            {
                if (Enum.TryParse<TEnum>(target, true, out var v))
                    // TryAdd, NOT indexer assignment: a synonym must never shadow a real
                    // enum member. "system" and "external" were legacy aliases onto
                    // Entity/Actor, and once NodeKind.System and NodeKind.External became
                    // real kinds the old aliases silently downgraded them.
                    map.TryAdd(Normalize(synonym), v);
            }
            return map;
        }

        private static TEnum ResolveDefault()
        {
            var name = DefaultFor(typeof(TEnum));
            return name is not null && Enum.TryParse<TEnum>(name, true, out var v)
                ? v
                : Enum.GetValues<TEnum>()[0];
        }
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static string? DefaultFor(Type enumType) => enumType.Name switch
    {
        "NodeKind" => "Entity",
        "EdgeKind" => "Flow",
        "NoteKind" => "General",
        "PositionHintKind" => "Auto",
        "BoundaryKind" => "Generic",
        "InteractionMode" => "Synchronous",
        _ => null,
    };

    private static IEnumerable<(string Synonym, string Target)> SynonymsFor(Type enumType) => enumType.Name switch
    {
        "NodeKind" => new[]
        {
            ("step", "Process"), ("task", "Process"), ("action", "Process"), ("activity", "Process"),
            ("operation", "Process"), ("function", "Process"), ("job", "Process"), ("stage", "Process"),
            ("workflow", "Process"), ("flow", "Process"),
            ("service", "Entity"), ("component", "Entity"), ("module", "Entity"),
            ("app", "Entity"), ("application", "Entity"), ("server", "Entity"), ("api", "Entity"),
            ("object", "Entity"), ("class", "Entity"), ("resource", "Entity"), ("thing", "Entity"),
            ("platform", "System"), ("environment", "System"), ("suite", "System"),
            ("tool", "Technology"), ("product", "Technology"), ("technology", "Technology"),
            ("control", "Security"), ("policy", "Security"), ("protection", "Security"),
            ("report", "Document"), ("artifact", "Document"), ("deliverable", "Document"),
            ("phase", "Milestone"), ("checkpoint", "Milestone"), ("deadline", "Milestone"),
            ("threat", "Risk"), ("gap", "Risk"),
            ("kpi", "Metric"), ("measure", "Metric"), ("volume", "Metric"), ("cost", "Metric"),
            ("thirdparty", "External"), ("vendor", "External"), ("partner", "External"),
            ("explanation", "Callout"), ("caveat", "Callout"), ("insight", "Callout"),
            ("database", "DataStore"), ("db", "DataStore"), ("datastore", "DataStore"), ("storage", "DataStore"),
            ("store", "DataStore"), ("cache", "DataStore"), ("queue", "DataStore"), ("table", "DataStore"),
            ("bucket", "DataStore"), ("repository", "DataStore"),
            ("user", "Actor"), ("person", "Actor"), ("role", "Actor"), ("customer", "Actor"),
            ("client", "Actor"), ("team", "Actor"), ("stakeholder", "Actor"),
            ("if", "Decision"), ("condition", "Decision"), ("branch", "Decision"), ("gateway", "Decision"),
            ("choice", "Decision"), ("switch", "Decision"),
            ("comment", "Note"), ("annotation", "Note"), ("label", "Note"),
        },
        "EdgeKind" => new[]
        {
            ("depends", "Dependency"), ("dependson", "Dependency"), ("uses", "Dependency"),
            ("requires", "Dependency"), ("needs", "Dependency"),
            ("associates", "Association"), ("relates", "Association"), ("relatedto", "Association"),
            ("links", "Association"), ("connects", "Association"), ("communicates", "Association"),
            ("inherits", "Inheritance"), ("extends", "Inheritance"), ("isa", "Inheritance"),
            ("derives", "Inheritance"), ("subtypeof", "Inheritance"),
            ("sends", "Flow"), ("calls", "Flow"), ("invokes", "Flow"), ("triggers", "Flow"),
            ("passes", "Flow"), ("data", "Flow"), ("request", "Flow"), ("response", "Flow"), ("next", "Flow"),
        },
        "NoteKind" => new[]
        {
            ("action", "ActionItem"), ("todo", "ActionItem"), ("followup", "ActionItem"), ("task", "ActionItem"),
            ("decided", "Decision"), ("resolution", "Decision"),
            ("q", "Question"), ("open", "Question"), ("openquestion", "Question"), ("query", "Question"),
            ("concern", "Risk"), ("issue", "Risk"), ("blocker", "Risk"), ("warning", "Risk"),
            ("note", "General"), ("info", "General"), ("comment", "General"), ("fyi", "General"),
        },
        "BoundaryKind" => new[]
        {
            ("platform", "System"), ("application", "System"),
            ("env", "Environment"), ("stage", "Environment"),
            ("customer", "Tenant"), ("organization", "Tenant"),
            ("vnet", "Network"), ("subnet", "Network"),
            ("securityzone", "TrustZone"), ("zone", "TrustZone"),
            ("subscription", "CloudScope"), ("resourcegroup", "CloudScope"), ("region", "CloudScope"),
            ("thirdparty", "External"), ("outside", "External"),
        },
        "InteractionMode" => new[]
        {
            ("sync", "Synchronous"), ("requestresponse", "Synchronous"),
            ("async", "Asynchronous"), ("queued", "Asynchronous"), ("event", "Asynchronous"),
            ("scheduled", "Batch"), ("bulk", "Batch"),
            ("streaming", "Stream"), ("realtime", "Stream"),
        },
        _ => Array.Empty<(string, string)>(),
    };
}
