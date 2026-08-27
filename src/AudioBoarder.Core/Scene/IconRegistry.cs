using System.Text;

namespace AudioBoarder.Core.Scene;

/// <summary>
/// Vector icons for node kinds and well-known technologies.
/// <para>
/// Icons are drawn from <see href="https://lucide.dev">Lucide</see> (ISC licence —
/// free for commercial use). The path data is embedded rather than fetched so the
/// whiteboard keeps working entirely offline, and it is rendered as SVG so it stays
/// crisp at any zoom and can be recoloured to match the node palette.
/// </para>
/// <para>
/// This replaces the previous emoji glyphs, which rendered inconsistently across
/// fonts, could not be recoloured, and made the board look like clip-art.
/// </para>
/// </summary>
public static class IconRegistry
{
    /// <summary>Licence notice for the embedded Lucide path data.</summary>
    public const string Attribution =
        "Icons from Lucide (https://lucide.dev) — ISC License, " +
        "Copyright (c) for portions of Lucide are held by Cole Bemis 2013-2022 " +
        "as part of Feather (MIT). All other copyright (c) for Lucide are held by " +
        "Lucide Contributors 2022.";

    // Lucide icons are authored on a 24x24 grid, 2px round stroke, no fill.
    private const string ViewBox = "0 0 24 24";

    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)
    {
        // ---- generic kinds ----
        ["cog"] = "<circle cx='12' cy='12' r='3'/><path d='M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M6.34 17.66l-1.41 1.41M19.07 4.93l-1.41 1.41'/>",
        ["box"] = "<path d='M21 8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16Z'/><path d='m3.3 7 8.7 5 8.7-5'/><path d='M12 22V12'/>",
        ["git-branch"] = "<line x1='6' x2='6' y1='3' y2='15'/><circle cx='18' cy='6' r='3'/><circle cx='6' cy='18' r='3'/><path d='M18 9a9 9 0 0 1-9 9'/>",
        ["database"] = "<ellipse cx='12' cy='5' rx='9' ry='3'/><path d='M3 5V19A9 3 0 0 0 21 19V5'/><path d='M3 12A9 3 0 0 0 21 12'/>",
        ["user"] = "<path d='M19 21v-2a4 4 0 0 0-4-4H9a4 4 0 0 0-4 4v2'/><circle cx='12' cy='7' r='4'/>",
        ["users"] = "<path d='M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2'/><circle cx='9' cy='7' r='4'/><path d='M22 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75'/>",
        ["sticky-note"] = "<path d='M16 3H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h11l5-5V5a2 2 0 0 0-2-2z'/><path d='M15 21v-4a2 2 0 0 1 2-2h4'/>",
        ["server"] = "<rect width='20' height='8' x='2' y='2' rx='2'/><rect width='20' height='8' x='2' y='14' rx='2'/><line x1='6' x2='6.01' y1='6' y2='6'/><line x1='6' x2='6.01' y1='18' y2='18'/>",
        ["wrench"] = "<path d='M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z'/>",
        ["shield"] = "<path d='M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z'/>",
        ["cloud"] = "<path d='M17.5 19H9a7 7 0 1 1 6.71-9h1.79a4.5 4.5 0 1 1 0 9Z'/>",
        ["file-text"] = "<path d='M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z'/><path d='M14 2v4a2 2 0 0 0 2 2h4'/><path d='M10 9H8M16 13H8M16 17H8'/>",
        ["flag"] = "<path d='M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1z'/><line x1='4' x2='4' y1='22' y2='15'/>",
        ["alert-triangle"] = "<path d='m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3'/><path d='M12 9v4'/><path d='M12 17h.01'/>",
        ["trending-up"] = "<polyline points='22 7 13.5 15.5 8.5 10.5 2 17'/><polyline points='16 7 22 7 22 13'/>",
        ["globe"] = "<circle cx='12' cy='12' r='10'/><path d='M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20'/><path d='M2 12h20'/>",
        ["lightbulb"] = "<path d='M15 14c.2-1 .7-1.7 1.5-2.5 1-.9 1.5-2.2 1.5-3.5A6 6 0 0 0 6 8c0 1 .2 2.2 1.5 3.5.7.7 1.3 1.5 1.5 2.5'/><path d='M9 18h6'/><path d='M10 22h4'/>",
        ["help-circle"] = "<circle cx='12' cy='12' r='10'/><path d='M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3'/><path d='M12 17h.01'/>",

        // ---- technology / product concepts ----
        ["bar-chart"] = "<line x1='12' x2='12' y1='20' y2='10'/><line x1='18' x2='18' y1='20' y2='4'/><line x1='6' x2='6' y1='20' y2='16'/>",
        ["search"] = "<circle cx='11' cy='11' r='8'/><path d='m21 21-4.3-4.3'/>",
        ["key"] = "<path d='m15.5 7.5 2.3 2.3a1 1 0 0 0 1.4 0l2.1-2.1a1 1 0 0 0 0-1.4L19 4'/><path d='m21 2-9.6 9.6'/><circle cx='7.5' cy='15.5' r='5.5'/>",
        ["key-round"] = "<path d='M2.586 17.414A2 2 0 0 0 2 18.828V21a1 1 0 0 0 1 1h3a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h1a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h.172a2 2 0 0 0 1.414-.586l.814-.814a6.5 6.5 0 1 0-4-4z'/><circle cx='16.5' cy='7.5' r='.5'/>",
        ["lock"] = "<rect width='18' height='11' x='3' y='11' rx='2' ry='2'/><path d='M7 11V7a5 5 0 0 1 10 0v4'/>",
        ["eye"] = "<path d='M2.06 12.35a1 1 0 0 1 0-.7 10.75 10.75 0 0 1 19.87 0 1 1 0 0 1 0 .7 10.75 10.75 0 0 1-19.87 0'/><circle cx='12' cy='12' r='3'/>",
        ["bot"] = "<path d='M12 8V4H8'/><rect width='16' height='12' x='4' y='8' rx='2'/><path d='M2 14h2M20 14h2M15 13v2M9 13v2'/>",
        ["brain"] = "<path d='M12 5a3 3 0 1 0-5.997.125 4 4 0 0 0-2.526 5.77 4 4 0 0 0 .556 6.588A4 4 0 1 0 12 18Z'/><path d='M12 5a3 3 0 1 1 5.997.125 4 4 0 0 1 2.526 5.77 4 4 0 0 1-.556 6.588A4 4 0 1 1 12 18Z'/>",
        ["folder"] = "<path d='M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z'/>",
        ["mail"] = "<rect width='20' height='16' x='2' y='4' rx='2'/><path d='m22 7-8.97 5.7a1.94 1.94 0 0 1-2.06 0L2 7'/>",
        ["network"] = "<rect x='16' y='16' width='6' height='6' rx='1'/><rect x='2' y='16' width='6' height='6' rx='1'/><rect x='9' y='2' width='6' height='6' rx='1'/><path d='M5 16v-3a1 1 0 0 1 1-1h12a1 1 0 0 1 1 1v3'/><path d='M12 12V8'/>",
        ["plug"] = "<path d='M12 22v-5'/><path d='M9 8V2M15 8V2'/><path d='M18 8v5a4 4 0 0 1-4 4h-4a4 4 0 0 1-4-4V8Z'/>",
        ["zap"] = "<path d='M4 14a1 1 0 0 1-.78-1.63l9.9-10.2a.5.5 0 0 1 .86.46l-1.92 6.02A1 1 0 0 0 13 10h7a1 1 0 0 1 .78 1.63l-9.9 10.2a.5.5 0 0 1-.86-.46l1.92-6.02A1 1 0 0 0 11 14z'/>",
        ["workflow"] = "<rect width='8' height='8' x='3' y='3' rx='2'/><path d='M7 11v4a2 2 0 0 0 2 2h4'/><rect width='8' height='8' x='13' y='13' rx='2'/>",
        ["calendar"] = "<path d='M8 2v4M16 2v4'/><rect width='18' height='18' x='3' y='4' rx='2'/><path d='M3 10h18'/>",
        ["clock"] = "<circle cx='12' cy='12' r='10'/><polyline points='12 6 12 12 16 14'/>",
        ["scale"] = "<path d='m16 16 3-8 3 8c-.87.65-1.92 1-3 1s-2.13-.35-3-1'/><path d='m2 16 3-8 3 8c-.87.65-1.92 1-3 1s-2.13-.35-3-1'/><path d='M7 21h10'/><path d='M12 3v18'/><path d='M3 7h2c2 0 5-1 7-2 2 1 5 2 7 2h2'/>",
        ["dollar-sign"] = "<line x1='12' x2='12' y1='2' y2='22'/><path d='M17 5H9.5a3.5 3.5 0 0 0 0 7h5a3.5 3.5 0 0 1 0 7H6'/>",
        ["truck"] = "<path d='M14 18V6a2 2 0 0 0-2-2H4a2 2 0 0 0-2 2v11a1 1 0 0 0 1 1h2'/><path d='M15 18H9'/><path d='M19 18h2a1 1 0 0 0 1-1v-3.65a1 1 0 0 0-.22-.62l-3.48-4.35A1 1 0 0 0 17.52 8H14'/><circle cx='17' cy='18' r='2'/><circle cx='7' cy='18' r='2'/>",
        ["archive"] = "<rect width='20' height='5' x='2' y='3' rx='1'/><path d='M4 8v11a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8'/><path d='M10 12h4'/>",
        ["clipboard-check"] = "<rect width='8' height='4' x='8' y='2' rx='1'/><path d='M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2'/><path d='m9 14 2 2 4-4'/>",
        ["bell"] = "<path d='M10.268 21a2 2 0 0 0 3.464 0'/><path d='M3.262 15.326A1 1 0 0 0 4 17h16a1 1 0 0 0 .74-1.673C19.41 13.956 18 12.499 18 8A6 6 0 0 0 6 8c0 4.499-1.411 5.956-2.738 7.326'/>",
        ["check-circle"] = "<circle cx='12' cy='12' r='10'/><path d='m9 12 2 2 4-4'/>",
        ["graduation-cap"] = "<path d='M21.42 10.922a1 1 0 0 0-.019-1.838L12.83 5.18a2 2 0 0 0-1.66 0L2.6 9.08a1 1 0 0 0 0 1.832l8.57 3.908a2 2 0 0 0 1.66 0z'/><path d='M22 10v6'/><path d='M6 12.5V16a6 3 0 0 0 12 0v-3.5'/>",
        ["door-open"] = "<path d='M13 4h3a2 2 0 0 1 2 2v14'/><path d='M2 20h3'/><path d='M13 20h9'/><path d='M10 12v.01'/><path d='M13 4.562v16.157a1 1 0 0 1-1.242.97L5.5 20.5a1 1 0 0 1-.75-.97V4.562a1 1 0 0 1 .78-.976l6-1.6a1 1 0 0 1 1.22.976z'/>",
        ["map"] = "<path d='M14.106 5.553a2 2 0 0 0 1.788 0l3.659-1.83A1 1 0 0 1 21 4.619v12.764a1 1 0 0 1-.553.894l-4.553 2.277a2 2 0 0 1-1.788 0l-4.212-2.106a2 2 0 0 0-1.788 0l-3.659 1.83A1 1 0 0 1 3 19.381V6.618a1 1 0 0 1 .553-.894l4.553-2.277a2 2 0 0 1 1.788 0z'/><path d='M15 5.764v15M9 3.236v15'/>",
        ["octagon-alert"] = "<path d='M12.83 2a2 2 0 0 1 1.41.59l5.17 5.17A2 2 0 0 1 20 9.17v5.66a2 2 0 0 1-.59 1.41l-5.17 5.17a2 2 0 0 1-1.41.59H9.17a2 2 0 0 1-1.41-.59l-5.17-5.17A2 2 0 0 1 2 14.83V9.17a2 2 0 0 1 .59-1.41l5.17-5.17A2 2 0 0 1 9.17 2z'/><path d='M12 8v4'/><path d='M12 16h.01'/>",
        ["smartphone"] = "<rect width='14' height='20' x='5' y='2' rx='2'/><path d='M12 18h.01'/>",
        ["container"] = "<path d='M22 7.7c0-.6-.4-1.2-.8-1.5l-6.3-3.9a1.72 1.72 0 0 0-1.7 0l-10.3 6c-.5.2-.9.8-.9 1.4v6.6c0 .5.4 1.2.8 1.5l6.3 3.9a1.72 1.72 0 0 0 1.7 0l10.3-6c.5-.3.9-1 .9-1.5Z'/><path d='M10 21.9V14L2.1 9.1'/><path d='m10 14 11.9-6.9'/><path d='M14 19.8v-8.1'/><path d='M18 17.5V9.4'/>",
        ["waves"] = "<path d='M2 6c.6.5 1.2 1 2.5 1C7 7 7 5 9.5 5c2.6 0 2.4 2 5 2 1.3 0 1.9-.5 2.5-1'/><path d='M2 12c.6.5 1.2 1 2.5 1 2.5 0 2.5-2 5-2 2.6 0 2.4 2 5 2 1.3 0 1.9-.5 2.5-1'/><path d='M2 18c.6.5 1.2 1 2.5 1 2.5 0 2.5-2 5-2 2.6 0 2.4 2 5 2 1.3 0 1.9-.5 2.5-1'/>",
        ["monitor"] = "<rect width='20' height='14' x='2' y='3' rx='2'/><line x1='8' x2='16' y1='21' y2='21'/><line x1='12' x2='12' y1='17' y2='21'/>",
        ["tag"] = "<path d='M12.586 2.586A2 2 0 0 0 11.172 2H4a2 2 0 0 0-2 2v7.172a2 2 0 0 0 .586 1.414l8.704 8.704a2.426 2.426 0 0 0 3.42 0l6.58-6.58a2.426 2.426 0 0 0 0-3.42z'/><circle cx='7.5' cy='7.5' r='.5'/>",

        // Networking edge and routing, distinct from the generic cloud glyph so an
        // ingress point does not look like the platform it sits in front of.
        ["globe-lock"] = "<path d='M15.686 15A14.5 14.5 0 0 1 12 22a14.5 14.5 0 0 1 0-20 10 10 0 1 0 9.542 13'/><path d='M2 12h8.5'/><rect x='16' y='16' width='6' height='5' rx='1'/><path d='M18 16v-1.5a1.5 1.5 0 0 1 3 0V16'/>",
        ["route"] = "<circle cx='6' cy='19' r='3'/><path d='M9 19h8.5a3.5 3.5 0 0 0 0-7h-11a3.5 3.5 0 0 1 0-7H15'/><circle cx='18' cy='5' r='3'/>",
        ["app-window"] = "<rect x='2' y='4' width='20' height='16' rx='2'/><path d='M10 4v4M2 8h20M6 4v4'/>",
    };

    /// <summary>
    /// Product/technology phrases → icon name. Matching is case-insensitive substring
    /// against the node label, longest phrase first so "power bi" beats "power".
    /// </summary>
    private static readonly Dictionary<string, string> ProductIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["power bi"] = "bar-chart",
        ["fabric"] = "network",
        ["synapse"] = "network",
        ["data factory"] = "workflow",
        ["databricks"] = "container",
        ["data lake"] = "waves",
        ["lakehouse"] = "waves",
        ["onelake"] = "waves",
        ["data catalog"] = "folder",
        ["dataverse"] = "database",

        ["purview"] = "search",
        ["defender"] = "shield",
        ["sentinel"] = "eye",
        ["entra"] = "key",
        ["active directory"] = "key",
        ["intune"] = "smartphone",
        ["compliance"] = "scale",
        ["dlp"] = "shield",
        ["conditional access"] = "lock",
        ["rbac"] = "lock",
        ["encryption"] = "lock",
        ["sensitivity label"] = "tag",
        ["classification"] = "tag",
        ["retention"] = "archive",

        // ---- Azure services, by the names people actually say ------------------
        // Longest phrase wins at lookup, so these beat the bare "azure" fallback.
        ["front door"] = "globe-lock",
        ["application gateway"] = "route",
        ["app gateway"] = "route",
        ["web application firewall"] = "shield",
        ["waf"] = "shield",
        ["load balancer"] = "route",
        ["traffic manager"] = "route",
        ["ddos"] = "shield",
        ["firewall"] = "shield",
        ["bastion"] = "lock",
        ["expressroute"] = "route",
        ["private endpoint"] = "plug",
        ["private link"] = "plug",
        ["private dns"] = "globe",
        ["dns zone"] = "globe",
        ["virtual network"] = "network",
        ["vnet"] = "network",
        ["subnet"] = "network",
        ["nsg"] = "shield",
        ["network security group"] = "shield",

        ["app service"] = "app-window",
        ["web app"] = "app-window",
        ["static web app"] = "app-window",
        ["functions"] = "zap",
        ["function app"] = "zap",
        ["container app"] = "container",
        ["container registry"] = "container",
        ["aks"] = "container",
        ["service fabric"] = "container",
        ["scale set"] = "server",
        ["vmss"] = "server",
        ["virtual machine scale set"] = "server",
        ["virtual machine"] = "monitor",

        ["cosmos"] = "database",
        ["sql database"] = "database",
        ["sql managed instance"] = "database",
        ["postgres"] = "database",
        ["mysql"] = "database",
        ["redis"] = "zap",
        ["blob storage"] = "archive",
        ["table storage"] = "archive",
        ["file share"] = "folder",
        ["managed disk"] = "archive",

        ["event hub"] = "workflow",
        ["event hubs"] = "workflow",
        ["event grid"] = "workflow",
        ["service bus"] = "workflow",
        ["key vault"] = "key-round",
        ["notification hub"] = "bell",
        ["api management"] = "plug",
        ["apim"] = "plug",

        ["application insights"] = "trending-up",
        ["log analytics"] = "trending-up",
        ["azure monitor"] = "trending-up",
        ["monitor workspace"] = "trending-up",

        ["ai search"] = "search",
        ["cognitive search"] = "search",
        ["ai foundry"] = "brain",
        ["foundry"] = "brain",
        ["cognitive services"] = "brain",
        ["machine learning"] = "brain",

        ["resource group"] = "box",
        ["subscription"] = "cloud",
        ["management group"] = "cloud",
        ["landing zone"] = "cloud",
        ["arm template"] = "file-text",
        ["bicep"] = "file-text",
        ["terraform"] = "file-text",

        ["copilot"] = "bot",
        ["openai"] = "brain",
        ["llm"] = "brain",
        ["agent"] = "bot",
        ["prompt"] = "brain",

        ["sharepoint"] = "folder",
        ["onedrive"] = "cloud",
        ["teams"] = "users",
        ["outlook"] = "mail",
        ["exchange"] = "mail",

        ["azure"] = "cloud",
        ["aws"] = "cloud",
        ["gcp"] = "cloud",
        ["kubernetes"] = "container",
        ["container"] = "container",
        ["docker"] = "container",
        ["virtual machine"] = "monitor",
        ["server"] = "server",
        ["network"] = "network",
        ["firewall"] = "shield",
        ["vpn"] = "lock",
        ["endpoint"] = "plug",

        ["sql"] = "database",
        ["database"] = "database",
        ["storage account"] = "archive",
        ["blob"] = "archive",
        ["warehouse"] = "database",
        ["cache"] = "zap",
        ["queue"] = "workflow",

        ["api"] = "plug",
        ["rest"] = "plug",
        ["webhook"] = "plug",
        ["power automate"] = "zap",
        ["power apps"] = "smartphone",
        ["logic app"] = "zap",
        ["connector"] = "plug",
        ["pipeline"] = "workflow",
        ["integration"] = "network",

        ["customer"] = "user",
        ["user"] = "user",
        ["team"] = "users",
        ["stakeholder"] = "users",
        ["approval"] = "check-circle",
        ["governance"] = "scale",
        ["policy"] = "file-text",
        ["workflow"] = "workflow",
        ["training"] = "graduation-cap",
        ["onboarding"] = "door-open",
        ["budget"] = "dollar-sign",
        ["cost"] = "dollar-sign",
        ["licence"] = "file-text",
        ["license"] = "file-text",
        ["contract"] = "file-text",
        ["report"] = "bar-chart",
        ["dashboard"] = "bar-chart",
        ["roadmap"] = "map",
        ["timeline"] = "calendar",
        ["deadline"] = "clock",
        ["checkpoint"] = "calendar",
        ["risk"] = "alert-triangle",
        ["issue"] = "alert-triangle",
        ["blocker"] = "octagon-alert",
        ["question"] = "help-circle",
        ["decision"] = "scale",
        ["migration"] = "truck",
        ["backup"] = "archive",
        ["audit"] = "clipboard-check",
        ["monitoring"] = "trending-up",
        ["alert"] = "bell",
        ["incident"] = "octagon-alert",
        ["review"] = "search",
        ["release"] = "flag",
        ["launch"] = "flag",
        ["milestone"] = "flag",
    };

    private static readonly Dictionary<NodeKind, string> KindIcons = new()
    {
        [NodeKind.Process] = "cog",
        [NodeKind.Entity] = "box",
        [NodeKind.Decision] = "git-branch",
        [NodeKind.DataStore] = "database",
        [NodeKind.Actor] = "user",
        [NodeKind.Note] = "sticky-note",
        [NodeKind.System] = "server",
        [NodeKind.Technology] = "wrench",
        [NodeKind.Security] = "shield",
        [NodeKind.Cloud] = "cloud",
        [NodeKind.Document] = "file-text",
        [NodeKind.Milestone] = "flag",
        [NodeKind.Risk] = "alert-triangle",
        [NodeKind.Metric] = "trending-up",
        [NodeKind.External] = "globe",
        [NodeKind.Callout] = "lightbulb",
    };

    private static readonly string[] PhrasesByLength =
        ProductIcons.Keys.OrderByDescending(k => k.Length).ToArray();

    /// <summary>Resolves the icon name for a label/kind. Never returns null.</summary>
    public static string Resolve(string? label, NodeKind kind)
    {
        if (!string.IsNullOrWhiteSpace(label))
        {
            foreach (var phrase in PhrasesByLength)
            {
                if (ContainsPhrase(label, phrase))
                    return ProductIcons[phrase];
            }
        }
        return KindIcons.TryGetValue(kind, out var icon) ? icon : "box";
    }

    /// <summary>
    /// Whole-word (or whole-phrase) containment. Plain substring matching gave
    /// visibly wrong icons — "api" matched "Rapid prototyping", "tag" matched
    /// "Staging environment" — which is exactly what makes a board look auto-generated.
    /// </summary>
    private static bool ContainsPhrase(string label, string phrase)
    {
        var index = 0;
        while ((index = label.IndexOf(phrase, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var beforeOk = index == 0 || !char.IsLetterOrDigit(label[index - 1]);
            var end = index + phrase.Length;
            var afterOk = end >= label.Length || !char.IsLetterOrDigit(label[end]);
            if (beforeOk && afterOk) return true;
            index = end;
        }
        return false;
    }

    /// <summary>True when the label names a known product/technology, not just a category.</summary>
    public static bool IsKnownTechnology(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        return PhrasesByLength.Any(p => ContainsPhrase(label, p));
    }

    /// <summary>Renders an icon as a standalone SVG document in the requested colour.</summary>
    public static string RenderSvg(string iconName, string strokeColor, double size = 24)
    {
        var body = Paths.TryGetValue(iconName, out var p) ? p : Paths["box"];
        var sb = new StringBuilder();
        sb.Append("<svg xmlns='http://www.w3.org/2000/svg' width='").Append(size)
          .Append("' height='").Append(size)
          .Append("' viewBox='").Append(ViewBox)
          .Append("' fill='none' stroke='").Append(strokeColor)
          .Append("' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'>")
          .Append(body)
          .Append("</svg>");
        return sb.Replace('\'', '"').ToString();
    }

    /// <summary>Renders an icon as a <c>data:</c> URL for an Excalidraw image element.</summary>
    public static string RenderDataUrl(string iconName, string strokeColor, double size = 24)
        => "data:image/svg+xml;base64," +
           Convert.ToBase64String(Encoding.UTF8.GetBytes(RenderSvg(iconName, strokeColor, size)));

    public static bool Has(string iconName) => Paths.ContainsKey(iconName);
}
