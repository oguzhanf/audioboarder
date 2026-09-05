using System.Text;
using System.Text.Json;

namespace AudioBoarder.Core.Scene;

/// <summary>
/// Curated architecture vocabulary for Microsoft cloud and on-premises components.
/// The taxonomy follows the Azure Architecture Center and Microsoft product families;
/// it is intentionally metadata-only so the same library can drive both the canvas UI
/// and model grounding without redistributing Microsoft artwork.
/// </summary>
public static class MicrosoftComponentCatalog
{
    public const string SourceUrl = "https://learn.microsoft.com/azure/architecture/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static IReadOnlyList<MicrosoftComponentDefinition> All { get; } =
    [
        C("azure-front-door", "Azure Front Door", "Azure / Networking", NodeKind.Technology, "Global application delivery and web application firewall.", "front door", "cdn", "waf"),
        C("application-gateway", "Azure Application Gateway", "Azure / Networking", NodeKind.Technology, "Regional layer 7 load balancer and web application firewall.", "app gateway", "waf"),
        C("load-balancer", "Azure Load Balancer", "Azure / Networking", NodeKind.Technology, "Layer 4 load balancing for Azure workloads.", "load balancer"),
        C("traffic-manager", "Azure Traffic Manager", "Azure / Networking", NodeKind.Technology, "DNS-based global traffic routing.", "traffic manager"),
        C("azure-firewall", "Azure Firewall", "Azure / Networking", NodeKind.Security, "Managed network firewall.", "firewall"),
        C("virtual-network", "Azure Virtual Network", "Azure / Networking", NodeKind.Cloud, "Private Azure network boundary.", "vnet", "virtual network"),
        C("subnet", "Subnet", "Azure / Networking", NodeKind.Cloud, "Address and security segment inside a virtual network.", "subnet"),
        C("network-security-group", "Network Security Group", "Azure / Networking", NodeKind.Security, "Layer 3 and 4 traffic filtering rules.", "nsg"),
        C("private-endpoint", "Private Endpoint", "Azure / Networking", NodeKind.Technology, "Private IP endpoint for a platform service.", "private link", "private endpoint"),
        C("expressroute", "Azure ExpressRoute", "Azure / Networking", NodeKind.Technology, "Private connectivity between on-premises networks and Microsoft cloud.", "express route"),
        C("vpn-gateway", "Azure VPN Gateway", "Azure / Networking", NodeKind.Technology, "Encrypted site-to-site and point-to-site connectivity.", "vpn"),
        C("azure-bastion", "Azure Bastion", "Azure / Networking", NodeKind.Security, "Browser-based private administration of virtual machines.", "bastion"),
        C("private-dns", "Azure Private DNS", "Azure / Networking", NodeKind.Technology, "Private DNS zones and name resolution.", "private dns"),

        C("app-service", "Azure App Service", "Azure / Compute", NodeKind.Technology, "Managed web application hosting.", "web app", "app service"),
        C("function-app", "Azure Functions", "Azure / Compute", NodeKind.Technology, "Event-driven serverless compute.", "function app", "functions"),
        C("container-apps", "Azure Container Apps", "Azure / Compute", NodeKind.Technology, "Managed container applications and jobs.", "container app"),
        C("aks", "Azure Kubernetes Service", "Azure / Compute", NodeKind.Technology, "Managed Kubernetes clusters.", "aks", "kubernetes"),
        C("virtual-machine", "Azure Virtual Machines", "Azure / Compute", NodeKind.Technology, "Infrastructure virtual machines.", "vm", "virtual machine"),
        C("vm-scale-set", "Virtual Machine Scale Sets", "Azure / Compute", NodeKind.Technology, "Autoscaling groups of virtual machines.", "vmss", "scale set"),
        C("azure-arc", "Azure Arc", "Hybrid / Management", NodeKind.Technology, "Azure management and governance for hybrid resources.", "arc enabled"),

        C("api-management", "Azure API Management", "Azure / Integration", NodeKind.Technology, "Managed API gateway, policies, and developer portal.", "apim", "api gateway"),
        C("service-bus", "Azure Service Bus", "Azure / Integration", NodeKind.Technology, "Enterprise queues and topics.", "service bus", "queue", "topic"),
        C("event-grid", "Azure Event Grid", "Azure / Integration", NodeKind.Technology, "Event routing and publish-subscribe integration.", "event grid"),
        C("event-hubs", "Azure Event Hubs", "Azure / Integration", NodeKind.Technology, "High-throughput event ingestion and streaming.", "event hub", "event stream"),
        C("logic-apps", "Azure Logic Apps", "Azure / Integration", NodeKind.Technology, "Low-code integration workflows.", "logic app"),

        C("storage-account", "Azure Storage Account", "Azure / Data", NodeKind.DataStore, "Durable object, file, queue, and table storage.", "blob", "file share", "storage"),
        C("azure-sql", "Azure SQL Database", "Azure / Data", NodeKind.DataStore, "Managed relational SQL database.", "sql database"),
        C("sql-managed-instance", "Azure SQL Managed Instance", "Azure / Data", NodeKind.DataStore, "Managed SQL Server-compatible instance.", "managed instance"),
        C("cosmos-db", "Azure Cosmos DB", "Azure / Data", NodeKind.DataStore, "Globally distributed NoSQL database.", "cosmos"),
        C("postgresql", "Azure Database for PostgreSQL", "Azure / Data", NodeKind.DataStore, "Managed PostgreSQL database.", "postgres"),
        C("redis", "Azure Managed Redis", "Azure / Data", NodeKind.DataStore, "Managed in-memory cache and data store.", "redis", "cache"),
        C("data-factory", "Azure Data Factory", "Azure / Data", NodeKind.Technology, "Cloud data integration and orchestration.", "adf", "data factory"),
        C("databricks", "Azure Databricks", "Azure / Data", NodeKind.Technology, "Lakehouse analytics and data engineering.", "databricks"),
        C("synapse", "Azure Synapse Analytics", "Azure / Data", NodeKind.Technology, "Enterprise analytics and data warehousing.", "synapse"),
        C("microsoft-fabric", "Microsoft Fabric", "Microsoft Data", NodeKind.Technology, "Unified analytics platform with OneLake.", "fabric", "onelake", "lakehouse"),
        C("power-bi", "Power BI", "Microsoft Data", NodeKind.Technology, "Business intelligence semantic models, reports, and dashboards.", "power bi"),

        C("azure-openai", "Azure OpenAI Service", "Azure / AI", NodeKind.Technology, "Enterprise access to deployed generative AI models.", "openai", "llm"),
        C("ai-foundry", "Microsoft Foundry", "Azure / AI", NodeKind.Technology, "AI application, model, and agent development platform.", "azure ai foundry", "foundry"),
        C("ai-search", "Azure AI Search", "Azure / AI", NodeKind.Technology, "Search, vector retrieval, and indexing.", "cognitive search", "vector search"),
        C("machine-learning", "Azure Machine Learning", "Azure / AI", NodeKind.Technology, "Machine learning development and operations.", "azure ml"),
        C("document-intelligence", "Azure AI Document Intelligence", "Azure / AI", NodeKind.Technology, "Document extraction and analysis.", "form recognizer"),

        C("entra-id", "Microsoft Entra ID", "Security / Identity", NodeKind.Identity, "Cloud identity and access management.", "azure ad", "active directory", "entra"),
        C("managed-identity", "Managed Identity", "Security / Identity", NodeKind.Identity, "Workload identity managed by Azure.", "managed identity"),
        C("key-vault", "Azure Key Vault", "Security / Identity", NodeKind.Security, "Secrets, keys, and certificate management.", "key vault"),
        C("defender-cloud", "Microsoft Defender for Cloud", "Security", NodeKind.Security, "Cloud security posture and workload protection.", "defender"),
        C("sentinel", "Microsoft Sentinel", "Security", NodeKind.Security, "Cloud-native SIEM and security orchestration.", "sentinel", "siem"),
        C("purview", "Microsoft Purview", "Security / Governance", NodeKind.Security, "Data governance, catalog, compliance, and risk.", "purview", "data catalog"),
        C("azure-monitor", "Azure Monitor", "Azure / Management", NodeKind.Technology, "Metrics, logs, traces, and alerts.", "monitor", "log analytics"),
        C("application-insights", "Application Insights", "Azure / Management", NodeKind.Technology, "Application performance monitoring and distributed tracing.", "app insights"),

        C("teams", "Microsoft Teams", "Microsoft 365", NodeKind.Technology, "Collaboration, meetings, chat, and calling.", "teams"),
        C("sharepoint", "SharePoint Online", "Microsoft 365", NodeKind.Technology, "Content, intranet, and collaboration sites.", "sharepoint"),
        C("onedrive", "OneDrive for Business", "Microsoft 365", NodeKind.Technology, "User file storage and synchronization.", "onedrive"),
        C("exchange-online", "Exchange Online", "Microsoft 365", NodeKind.Technology, "Cloud email and calendaring.", "exchange", "outlook"),
        C("microsoft-graph", "Microsoft Graph", "Microsoft 365", NodeKind.Technology, "Unified API for Microsoft cloud data and services.", "graph api", "ms graph"),
        C("copilot-studio", "Microsoft Copilot Studio", "Power Platform", NodeKind.Technology, "Low-code copilots and conversational agents.", "power virtual agents"),
        C("power-apps", "Power Apps", "Power Platform", NodeKind.Technology, "Low-code business applications.", "powerapps"),
        C("power-automate", "Power Automate", "Power Platform", NodeKind.Technology, "Cloud and desktop workflow automation.", "flow"),
        C("dataverse", "Microsoft Dataverse", "Power Platform", NodeKind.DataStore, "Business data platform for Power Platform and Dynamics 365.", "common data service"),
        C("dynamics-365", "Dynamics 365", "Business Applications", NodeKind.Technology, "Microsoft CRM and ERP applications.", "d365"),

        C("active-directory-ds", "Active Directory Domain Services", "On-premises", NodeKind.Identity, "On-premises directory, Kerberos, LDAP, and domain services.", "ad ds", "domain controller"),
        C("windows-server", "Windows Server", "On-premises", NodeKind.Technology, "Windows server operating system and roles.", "windows server"),
        C("sql-server", "SQL Server", "On-premises", NodeKind.DataStore, "On-premises Microsoft relational database.", "mssql"),
        C("sharepoint-server", "SharePoint Server", "On-premises", NodeKind.Technology, "On-premises content and collaboration platform.", "sharepoint on premises"),
        C("exchange-server", "Exchange Server", "On-premises", NodeKind.Technology, "On-premises email and calendaring.", "exchange on premises"),
        C("system-center", "Microsoft System Center", "On-premises", NodeKind.Technology, "Datacenter monitoring, configuration, and operations management.", "scom", "sccm", "scvmm"),
        C("azure-stack-hci", "Azure Local", "Hybrid / Infrastructure", NodeKind.Technology, "Azure-connected hyperconverged infrastructure.", "azure stack hci", "azure local"),
        C("hyper-v", "Hyper-V", "On-premises", NodeKind.Technology, "Microsoft server virtualization.", "hyper v"),
        C("on-prem-network", "On-premises Network", "On-premises", NodeKind.Cloud, "Customer-managed datacenter or branch network boundary.", "datacenter", "data center", "lan"),
    ];

    public static MicrosoftComponentDefinition? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));

    public static (double Width, double Height) MeasureCard(MicrosoftComponentDefinition component)
    {
        var size = NodeSizer.Measure(component.Name, component.Description, true, component.Kind);
        return (Math.Max(260, size.Width), Math.Max(104, size.Height));
    }

    public static int RepairLegacyDropSizes(SceneGraph scene)
    {
        var repaired = 0;
        lock (scene.SyncRoot)
        {
            foreach (var node in scene.Nodes.Values)
            {
                if (!node.Locked || node.Width != 190 || node.Height != 70 ||
                    !node.X.HasValue || !node.Y.HasValue) continue;
                var component = All.FirstOrDefault(c =>
                    node.Id.StartsWith($"user-{c.Id}-", StringComparison.Ordinal) &&
                    node.Label == c.Name && node.Description == c.Description);
                if (component is null) continue;
                var size = MeasureCard(component);
                if (scene.TryUpdateNodeGeometry(node.Id, node.X.Value, node.Y.Value, size.Width, size.Height, true))
                    repaired++;
            }
        }
        return repaired;
    }

    public static IReadOnlyList<MicrosoftComponentDefinition> Search(string? query, int limit = 40)
    {
        if (limit <= 0) return [];
        var terms = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return All
            .Select(item => (Item: item, Score: Score(item, terms)))
            .Where(x => terms.Length == 0 || x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.Category, StringComparer.Ordinal)
            .ThenBy(x => x.Item.Name, StringComparer.Ordinal)
            .Take(limit)
            .Select(x => x.Item)
            .ToArray();
    }

    public static string ToCanvasJson(AzureIconLibrary? icons = null) => JsonSerializer.Serialize(new
    {
        type = "component-library",
        source = SourceUrl,
        components = All.Select(component =>
        {
            var visual = ComponentIconVisuals.ForComponent(component, icons);
            return new
            {
                component.Id, component.Name, component.Category, component.Kind,
                component.Icon, component.Description, component.Aliases,
                visual.Svg, IconIsOfficial = visual.IsOfficial,
            };
        }),
    }, JsonOptions);

    public static string ToPromptVocabulary() =>
        string.Join("; ", All.Select(x => $"{x.Name} [{x.Category}]"));

    private static int Score(MicrosoftComponentDefinition item, string[] terms)
    {
        if (terms.Length == 0) return 1;
        var name = Normalize(item.Name);
        var category = Normalize(item.Category);
        var aliases = Normalize(string.Join(' ', item.Aliases));
        var score = 0;
        foreach (var term in terms)
        {
            if (name == term) score += 20;
            else if (name.StartsWith(term, StringComparison.Ordinal)) score += 10;
            else if (name.Contains(term, StringComparison.Ordinal)) score += 6;
            else if (aliases.Contains(term, StringComparison.Ordinal)) score += 4;
            else if (category.Contains(term, StringComparison.Ordinal)) score += 2;
            else return 0;
        }
        return score;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var result = new StringBuilder(value.Length);
        foreach (var c in value)
            result.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        return string.Join(' ', result.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static MicrosoftComponentDefinition C(
        string id,
        string name,
        string category,
        NodeKind kind,
        string description,
        params string[] aliases) =>
        new(id, name, category, kind, IconRegistry.Resolve(name, kind), description, aliases);
}

public sealed record MicrosoftComponentDefinition(
    string Id,
    string Name,
    string Category,
    NodeKind Kind,
    string Icon,
    string Description,
    IReadOnlyList<string> Aliases);
