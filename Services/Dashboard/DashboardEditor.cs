using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RIoT2.Core.Models;
using RIoT2.Net.Orchestrator.Services.Persistence;
using CoreValueType = RIoT2.Core.ValueType;

namespace RIoT2.Net.Orchestrator.Services.Dashboard
{
    /// <summary>The outcome of a dashboard edit: what the tool returns, or why the edit was refused.</summary>
    public sealed record DashboardEditResult(JsonNode? Result, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, string? VersionId,
        bool Changed)
    {
        public bool Succeeded => Errors.Count == 0;
    }

    /// <summary>A card as an MCP client writes it (the compact form); every member but the kind is optional.</summary>
    public sealed record DashboardCardInput(string? Id, string? Kind, string? Name, string? Icon, JsonNode? Size, JsonObject? Settings,
        JsonArray? Elements)
    {
        public static DashboardCardInput From(JsonObject card) => new(
            Text(card["id"]), Text(card["kind"]), Text(card["name"]), Text(card["icon"]), card["size"]?.DeepClone(),
            card["settings"] as JsonObject, card["elements"] as JsonArray);

        internal static string? Text(JsonNode? node) =>
            node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
    }

    /// <summary>
    /// Edits the stored dashboard for the MCP dashboard tools (ADR 0018, I60), one edit at a time: reads it, changes it in the
    /// compact form's terms, checks cards against the <see cref="DashboardCardCatalog"/> and the templates, and saves it, which
    /// records a <see cref="DashboardVersion"/> with the edit's summary.
    /// </summary>
    public sealed partial class DashboardEditor(IOrchestratorConfigurationService configuration, IStoredObjectStore store,
        DashboardCardCatalogService catalogs, DashboardVersionService versions)
    {
        public const int MaxNameLength = 100;
        public const string NoCatalog = "There is no card catalog yet: open the RIoT2 UI (1.14.0 or later) once, and it uploads its card kinds.";
        private static readonly Dictionary<string, int> Sizes = new(StringComparer.OrdinalIgnoreCase) { ["small"] = 3, ["half"] = 6, ["large"] = 9, ["full"] = 12 };

        private readonly Lock _gate = new();

        /// <summary>The stored dashboard as JSON: templates as ids, without reports. An empty one when none is stored.</summary>
        public JsonObject Current()
        {
            var stored = store.GetAll<DashboardConfiguration>().FirstOrDefault();
            var json = stored == null ? new JsonObject { ["id"] = "", ["name"] = "", ["pages"] = new JsonArray() } : DashboardVersionService.Definition(stored);
            if (json["pages"] is not JsonArray)
                json["pages"] = new JsonArray();
            return json;
        }

        /// <summary>The compact form of <paramref name="dashboard"/>, or of its page <paramref name="pageId"/>; <c>null</c> for an unknown page.</summary>
        public static JsonObject? Compact(JsonObject dashboard, string? pageId = null)
        {
            var pages = Pages(dashboard).Where(p => pageId == null || Id(p) == pageId).Select(CompactPage).ToList();
            if (pageId != null)
                return pages.FirstOrDefault()?.AsObject();
            return new JsonObject { ["pages"] = new JsonArray([.. pages]) };
        }

        public static JsonObject CompactPage(JsonObject page) => new()
        {
            ["id"] = Id(page),
            ["name"] = page["name"]?.DeepClone(),
            ["icon"] = page["icon"]?.DeepClone(),
            ["cards"] = new JsonArray([.. Cards(page).Select(CompactCard)])
        };

        public static JsonObject CompactCard(JsonObject card)
        {
            var compact = new JsonObject
            {
                ["id"] = Id(card),
                ["kind"] = card["kind"]?.DeepClone(),
                ["name"] = card["name"]?.DeepClone(),
                ["icon"] = card["icon"]?.DeepClone(),
                ["size"] = SizeName(card["size"]),
                ["settings"] = card["settings"]?.DeepClone() ?? new JsonObject()
            };
            if (card["kind"] == null)
                compact["legacyType"] = card["type"]?.DeepClone();
            compact["elements"] = new JsonArray([.. Elements(card).Select(element =>
            {
                var properties = element["properties"]?.DeepClone() as JsonObject ?? [];
                var role = DashboardCardInput.Text(properties["role"]);
                properties.Remove("role");
                var item = new JsonObject { ["id"] = Id(element), ["name"] = element["name"]?.DeepClone() };
                if (DashboardCardInput.Text(element["icon"]) is { Length: > 0 } icon)
                    item["icon"] = icon;
                if (role != null)
                    item["role"] = role;
                if (TemplateId(element, "reportTemplate") is { } report)
                    item["reportId"] = report;
                if (TemplateId(element, "commandTemplate") is { } command)
                    item["commandId"] = command;
                item["properties"] = properties;
                return (JsonNode)item;
            })]);
            return compact;
        }

        /// <summary>
        /// Runs <paramref name="edit"/> on a copy of the dashboard and saves the result with <paramref name="summary"/>'s text, unless
        /// it reported errors. <paramref name="needsCatalog"/> refuses the edit while no UI has uploaded a card catalog.
        /// </summary>
        public DashboardEditResult Edit(string caller, bool needsCatalog, Func<DashboardEditSession, JsonNode?> edit)
        {
            ArgumentNullException.ThrowIfNull(edit);
            lock (_gate)
            {
                var catalog = catalogs.Get();
                if (needsCatalog && catalog == null)
                    return new(null, [NoCatalog], [], null, false);
                var session = new DashboardEditSession(Current(), catalog, configuration.GetReportTemplates().ToList(),
                    configuration.GetCommandTemplates().ToList());
                JsonNode? result;
                try
                {
                    result = edit(session);
                }
                catch (DashboardEditException x)
                {
                    session.Errors.Add(x.Message);
                    result = null;
                }
                if (session.Errors.Count > 0)
                    return new(null, session.Errors, session.Warnings, null, false);

                var before = versions.Latest?.Id;
                using (DashboardSaveContext.Use(DashboardSources.Mcp, caller, session.Summary))
                {
                    if (string.IsNullOrEmpty(configuration.SaveDashboardConfiguration(session.Dashboard.ToJsonString())))
                        return new(null, ["The dashboard could not be saved."], session.Warnings, null, false);
                }
                var latest = versions.Latest?.Id;
                return new(result, [], session.Warnings, latest, latest != before);
            }
        }

        internal static IEnumerable<JsonObject> Pages(JsonObject dashboard) => (dashboard["pages"] as JsonArray ?? []).OfType<JsonObject>();
        internal static IEnumerable<JsonObject> Cards(JsonObject page) => (page["components"] as JsonArray ?? []).OfType<JsonObject>();
        internal static IEnumerable<JsonObject> Elements(JsonObject card) => (card["elements"] as JsonArray ?? []).OfType<JsonObject>();
        internal static string Id(JsonObject item) => DashboardCardInput.Text(item["id"]) ?? "";

        private static string? TemplateId(JsonObject element, string key) =>
            element[key] is JsonObject template && DashboardCardInput.Text(template["id"]) is { Length: > 0 } id ? id : null;

        internal static JsonNode SizeName(JsonNode? size)
        {
            var columns = size is JsonValue value && value.TryGetValue<int>(out var number) ? number : 3;
            var name = Sizes.FirstOrDefault(x => x.Value == columns).Key;
            return name != null ? JsonValue.Create(name) : JsonValue.Create(columns);
        }

        /// <summary>A size as a name (small, half, large, full) or columns (3, 6, 9, 12); <c>null</c> when it is neither.</summary>
        internal static int? ParseSize(JsonNode? size)
        {
            if (size is not JsonValue value)
                return null;
            if (value.GetValueKind() == JsonValueKind.String)
                return Sizes.TryGetValue(value.GetValue<string>().Trim(), out var columns) ? columns : null;
            return value.TryGetValue<int>(out var number) && number is 3 or 6 or 9 or 12 ? number : null;
        }

        [GeneratedRegex("^mdi-[a-z0-9-]{1,80}$")]
        internal static partial Regex IconPattern();
    }

    /// <summary>A refused edit, with the message the tool returns.</summary>
    public sealed class DashboardEditException(string message) : Exception(message);

    /// <summary>One edit's working copy of the dashboard, with the catalog, the templates, and the edit's errors, warnings and summary.</summary>
    public sealed class DashboardEditSession(JsonObject dashboard, DashboardCardCatalog? catalog, List<ReportTemplate> reports,
        List<CommandTemplate> commands)
    {
        public JsonObject Dashboard { get; } = dashboard;
        public DashboardCardCatalog? Catalog { get; } = catalog;
        public List<string> Errors { get; } = [];
        public List<string> Warnings { get; } = [];
        public string Summary { get; set; } = "";

        public JsonArray PageList => Dashboard["pages"] as JsonArray ?? throw new DashboardEditException("The dashboard has no pages.");

        public JsonObject Page(string? pageId) =>
            DashboardEditor.Pages(Dashboard).FirstOrDefault(p => DashboardEditor.Id(p) == pageId)
            ?? throw new DashboardEditException($"There is no page '{pageId}'. get_dashboard lists the pages and their ids.");

        public (JsonObject Page, JsonObject Card) Card(string? cardId)
        {
            foreach (var page in DashboardEditor.Pages(Dashboard))
                foreach (var card in DashboardEditor.Cards(page))
                    if (DashboardEditor.Id(card) == cardId)
                        return (page, card);
            throw new DashboardEditException($"There is no card '{cardId}'. get_dashboard lists the cards and their ids.");
        }

        public static JsonArray Components(JsonObject page)
        {
            if (page["components"] is not JsonArray cards)
                page["components"] = cards = [];
            return cards;
        }

        public static string NewId() => Guid.NewGuid().ToString();

        public static string PageName(string? name)
        {
            var trimmed = name?.Trim() ?? "";
            if (trimmed.Length is 0 or > DashboardEditor.MaxNameLength)
                throw new DashboardEditException($"A page name must be 1-{DashboardEditor.MaxNameLength} characters.");
            return trimmed;
        }

        public static JsonNode? Icon(string? icon)
        {
            if (string.IsNullOrWhiteSpace(icon))
                return null;
            var trimmed = icon.Trim();
            if (!DashboardEditor.IconPattern().IsMatch(trimmed))
                throw new DashboardEditException($"'{icon}' is not an icon: use a Material Design Icons name such as mdi-thermometer.");
            return trimmed;
        }

        /// <summary>Where to insert: <paramref name="position"/> from 0, or the end; out of range is an error.</summary>
        public static int Position(int? position, int count)
        {
            if (position == null)
                return count;
            if (position < 0 || position > count)
                throw new DashboardEditException($"position must be 0-{count}.");
            return position.Value;
        }

        public static void Insert(JsonArray list, int index, JsonNode node)
        {
            if (index >= list.Count)
                list.Add(node);
            else
                list.Insert(index, node);
        }

        /// <summary>
        /// A card as the dashboard stores it, from <paramref name="input"/> over <paramref name="existing"/> (an update keeps what
        /// the input leaves out). Records errors (and returns <c>null</c>) when the card doesn't fit its kind or the templates.
        /// </summary>
        public JsonObject? BuildCard(DashboardCardInput input, JsonObject? existing)
        {
            var errorCount = Errors.Count;
            var kindName = input.Kind ?? DashboardCardInput.Text(existing?["kind"]);
            var label = input.Name ?? DashboardCardInput.Text(existing?["name"]) ?? kindName ?? "card";
            if (kindName == null)
            {
                // A card saved before card kinds: only its name, icon and size change; the UI upgrades it at its next save.
                if (existing == null)
                {
                    Errors.Add("A new card needs a kind; list_dashboard_cards lists them.");
                    return null;
                }
                if (input.Settings != null || input.Elements != null)
                    Errors.Add($"Card '{label}' has no kind yet: set a kind to change its settings or elements.");
                var kept = (JsonObject)existing.DeepClone();
                ApplyCommon(kept, input, label);
                return Errors.Count > errorCount ? null : kept;
            }

            var catalog = Catalog ?? throw new DashboardEditException(DashboardEditor.NoCatalog);
            var kind = catalog.Find(kindName);
            if (kind == null)
            {
                Errors.Add($"There is no card kind '{kindName}'; the kinds are {string.Join(", ", catalog.Cards.Select(x => x.Kind))}.");
                return null;
            }
            var sameKind = existing != null && DashboardCardInput.Text(existing["kind"]) == kind.Kind;

            var card = new JsonObject
            {
                ["id"] = existing?["id"]?.DeepClone() ?? DashboardEditSession.NewId(),
                ["name"] = existing?["name"]?.DeepClone() ?? "",
                ["description"] = existing?["description"]?.DeepClone() ?? "",
                ["icon"] = existing?["icon"]?.DeepClone(),
                ["type"] = kind.LegacyType,
                ["size"] = existing?["size"]?.DeepClone() ?? kind.DefaultSize,
                ["kind"] = kind.Kind
            };
            ApplyCommon(card, input, label);

            var settings = (JsonObject)kind.Defaults.DeepClone();
            if (sameKind && existing!["settings"] is JsonObject previous)
                foreach (var (key, value) in previous)
                    settings[key] = value?.DeepClone();
            if (input.Settings != null)
                ApplySettings($"Card '{label}'", kind.Settings, kind.Defaults, settings, input.Settings);
            card["settings"] = settings;
            card["type"] = LegacyType(kind, settings);

            var elements = input.Elements != null ? BuildElements(kind, input.Elements, sameKind ? existing : null, label)
                : sameKind ? existing!["elements"]?.DeepClone() as JsonArray ?? [] : BuildElements(kind, [], null, label);
            card["elements"] = elements;
            return Errors.Count > errorCount ? null : card;
        }

        private void ApplyCommon(JsonObject card, DashboardCardInput input, string label)
        {
            if (input.Name != null)
            {
                if (input.Name.Trim().Length > DashboardEditor.MaxNameLength)
                    Errors.Add($"Card '{label}': the name must be at most {DashboardEditor.MaxNameLength} characters.");
                card["name"] = input.Name.Trim();
            }
            if (input.Icon != null)
            {
                try
                {
                    card["icon"] = Icon(input.Icon);
                }
                catch (DashboardEditException x)
                {
                    Errors.Add(x.Message);
                }
            }
            if (input.Size != null)
            {
                var size = DashboardEditor.ParseSize(input.Size);
                if (size == null)
                    Errors.Add($"Card '{label}': size must be small, half, large or full (3, 6, 9 or 12 columns).");
                else
                    card["size"] = size;
            }
        }

        private static int LegacyType(CardKind kind, JsonObject settings)
        {
            var by = kind.LegacyTypeBy;
            if (by != null && settings[by.Key] is JsonValue value && value.GetValueKind() == JsonValueKind.String
                && by.Values.TryGetValue(value.GetValue<string>(), out var type))
                return type;
            return kind.LegacyType;
        }

        private JsonArray BuildElements(CardKind kind, JsonArray inputs, JsonObject? existing, string label)
        {
            var layout = kind.Elements;
            var result = new JsonArray();
            var items = inputs.ToList();
            if (layout == null)
            {
                if (items.Count > 0)
                    Errors.Add($"Card '{label}': a {kind.Kind} card has no elements.");
                return result;
            }
            if (layout.Each != null && (items.Count < layout.Min || items.Count > layout.Max))
                Errors.Add($"Card '{label}': a {kind.Kind} card needs {Range(layout.Min ?? 0, layout.Max ?? 0)} {(layout.Max == 1 ? "element" : "elements")} ({layout.Each.Label}).");
            var previous = existing == null ? [] : DashboardEditor.Elements(existing).ToDictionary(DashboardEditor.Id, StringComparer.Ordinal);
            var roles = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < items.Count; index++)
            {
                if (items[index] is not JsonObject item)
                {
                    Errors.Add($"Card '{label}': every element must be an object.");
                    continue;
                }
                var role = DashboardCardInput.Text(item["role"]);
                var slot = layout.Each;
                if (layout.Slots != null)
                {
                    var roleNames = string.Join(", ", layout.Slots.Select(x => x.Role));
                    slot = layout.Slots.FirstOrDefault(x => x.Role == role);
                    if (slot == null)
                    {
                        Errors.Add($"Card '{label}': every element needs a role, one of {roleNames}.");
                        continue;
                    }
                    if (!roles.Add(slot.Role!))
                        Errors.Add($"Card '{label}': the role '{slot.Role}' is used twice.");
                }
                var id = DashboardCardInput.Text(item["id"]);
                var element = BuildElement(item, slot!, id != null && previous.TryGetValue(id, out var old) ? old : null, $"Card '{label}', element {index + 1}");
                if (element != null)
                    result.Add(element);
            }
            foreach (var slot in layout.Slots ?? [])
                if ((slot.Report == CardRequirement.Required || slot.Command == CardRequirement.Required) && !roles.Contains(slot.Role!))
                    Errors.Add($"Card '{label}': the element with role '{slot.Role}' ({slot.Label}) is required.");
            return result;
        }

        private static string Range(int min, int max) => min == max ? $"{min}" : $"{min}-{max}";

        private JsonObject? BuildElement(JsonObject input, CardSlot slot, JsonObject? existing, string where)
        {
            var errorCount = Errors.Count;
            var reportId = DashboardCardInput.Text(input["reportId"]);
            var commandId = DashboardCardInput.Text(input["commandId"]);
            var report = Template(reportId, slot.Report, "report", reports.Select(x => (x.Id, x.Name)), where);
            var command = Template(commandId, slot.Command, "command", commands.Select(x => (x.Id, x.Name)), where);
            var reportTemplate = report == null ? null : reports.First(x => x.Id == report);
            var commandTemplate = command == null ? null : commands.First(x => x.Id == command);

            var properties = (JsonObject?)slot.Defaults?.DeepClone() ?? [];
            if (existing?["properties"] is JsonObject previous)
                foreach (var (key, value) in previous)
                    properties[key] = value?.DeepClone();
            if (input["properties"] is JsonObject given)
            {
                var fields = slot.Settings ?? [];
                var unknown = given.Select(x => x.Key).Where(key => key != "role" && fields.All(f => f.Key != key) && slot.Defaults?.ContainsKey(key) != true).ToList();
                if (unknown.Count > 0)
                    Errors.Add($"{where}: unknown properties {string.Join(", ", unknown)}; the properties are {Keys(fields, slot.Defaults)}.");
                else
                    ApplySettings(where, fields, slot.Defaults ?? [], properties, given);
            }
            else if (input["properties"] != null)
                Errors.Add($"{where}: properties must be an object.");
            properties.Remove("role");
            if (slot.Role != null)
                properties["role"] = slot.Role;

            var schema = reportTemplate?.Schema;
            var memberField = (slot.Settings ?? []).FirstOrDefault(x => x.Type == "member");
            if (memberField != null && reportTemplate != null && TypeOf(schema, reportTemplate.Type) == "object" && schema?.Properties is { Count: > 0 })
            {
                var member = DashboardCardInput.Text(properties[memberField.Key]) ?? "";
                if (member.Length == 0)
                {
                    var accepts = memberField.Accepts ?? slot.Accepts;
                    var chosen = schema.Properties.FirstOrDefault(x => accepts == null || accepts.Contains(TypeOf(x.Value, null) ?? "")).Key
                        ?? schema.Properties.First().Key;
                    properties[memberField.Key] = chosen;
                    Warnings.Add($"{where}: shows the member '{chosen}' of report '{reportTemplate.Name}'; the members are {string.Join(", ", schema.Properties.Keys)}.");
                }
                else if (Member(schema, member) == null)
                    Errors.Add($"{where}: report '{reportTemplate.Name}' has no member '{member}'; the members are {string.Join(", ", schema.Properties.Keys)}.");
            }
            if (reportTemplate != null && slot.Accepts is { Count: > 0 } && TypeOf(schema, reportTemplate.Type) is { } type && type != "object"
                && !slot.Accepts.Contains(type))
                Warnings.Add($"{where}: report '{reportTemplate.Name}' is a {type}; this element suits {string.Join(" or ", slot.Accepts)}.");

            if (Errors.Count > errorCount)
                return null;
            var name = DashboardCardInput.Text(input["name"]) ?? DashboardCardInput.Text(existing?["name"]) ?? reportTemplate?.Name ?? commandTemplate?.Name ?? slot.Label;
            JsonNode? icon = existing?["icon"]?.DeepClone();
            if (DashboardCardInput.Text(input["icon"]) is { } iconName)
                icon = Icon(iconName);
            return new JsonObject
            {
                ["id"] = existing?["id"]?.DeepClone() ?? DashboardEditSession.NewId(),
                ["name"] = name.Trim(),
                ["icon"] = icon,
                ["numberOfPreviousReports"] = reportTemplate?.MaintainHistory == true ? Math.Max(1, slot.History ?? 1) : 1,
                ["reportTemplate"] = report == null ? null : new JsonObject { ["id"] = report },
                ["commandTemplate"] = command == null ? null : new JsonObject { ["id"] = command },
                ["properties"] = properties
            };
        }

        private string? Template(string? id, string requirement, string what, IEnumerable<(string Id, string Name)> templates, string where)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                if (requirement == CardRequirement.Required)
                    Errors.Add($"{where}: {what}Id is required (list_values with kind '{what}' lists them).");
                return null;
            }
            if (requirement == CardRequirement.None)
            {
                Errors.Add($"{where}: this element takes no {what}.");
                return null;
            }
            if (!templates.Any(x => x.Id == id))
            {
                Errors.Add($"{where}: there is no {what} '{id}' (list_values with kind '{what}' lists them).");
                return null;
            }
            return id;
        }

        /// <summary>Applies <paramref name="given"/> to <paramref name="target"/>: each key must be a field, each value fit it; <c>null</c> restores the default.</summary>
        private void ApplySettings(string where, List<CardField> fields, JsonObject defaults, JsonObject target, JsonObject given)
        {
            foreach (var (key, value) in given)
            {
                if (key == "role")
                    continue;
                var field = fields.FirstOrDefault(x => x.Key == key);
                if (field == null && !defaults.ContainsKey(key))
                {
                    Errors.Add($"{where}: unknown setting '{key}'; the settings are {Keys(fields, defaults)}.");
                    continue;
                }
                if (value == null)
                {
                    if (defaults.ContainsKey(key))
                        target[key] = defaults[key]?.DeepClone();
                    else
                        target.Remove(key);
                    continue;
                }
                var problem = field == null ? null : Check(field, value);
                if (problem != null)
                    Errors.Add($"{where}: '{key}' {problem}");
                else
                    target[key] = value.DeepClone();
            }
        }

        private static string Keys(List<CardField> fields, JsonObject? defaults)
        {
            var keys = fields.Select(x => x.Key).Concat(defaults?.Select(x => x.Key) ?? []).Where(x => x != "role").Distinct().ToList();
            return keys.Count == 0 ? "none" : string.Join(", ", keys);
        }

        /// <summary>Why <paramref name="value"/> doesn't fit <paramref name="field"/>; <c>null</c> when it does.</summary>
        internal static string? Check(CardField field, JsonNode value)
        {
            var kind = value.GetValueKind();
            switch (field.Type)
            {
                case "number":
                    if (kind != JsonValueKind.Number)
                        return "must be a number.";
                    var number = value.GetValue<double>();
                    if (field.Min != null && number < field.Min || field.Max != null && number > field.Max)
                        return $"must be {field.Min?.ToString() ?? "any"} to {field.Max?.ToString() ?? "any"}.";
                    return null;
                case "boolean":
                    return kind is JsonValueKind.True or JsonValueKind.False ? null : "must be true or false.";
                case "select":
                    var options = field.Options ?? [];
                    return options.Any(x => JsonNode.DeepEquals(x.Value, value)) ? null
                        : $"must be one of {string.Join(", ", options.Select(x => x.Value?.ToJsonString()))}.";
                case "value":
                    return null;
                case "list":
                    if (value is not JsonArray items)
                        return "must be a list of objects.";
                    foreach (var item in items)
                    {
                        if (item is not JsonObject entry)
                            return "must be a list of objects.";
                        foreach (var (key, member) in entry)
                        {
                            var itemField = field.Fields?.FirstOrDefault(x => x.Key == key);
                            if (itemField == null)
                                return $"items have no '{key}'; their fields are {string.Join(", ", (field.Fields ?? []).Select(x => x.Key))}.";
                            if (member != null && Check(itemField, member) is { } problem)
                                return $"item '{key}' {problem}";
                        }
                    }
                    return null;
                default:
                    return kind == JsonValueKind.String ? null : "must be text.";
            }
        }

        internal static string? TypeOf(ValueSchema? schema, CoreValueType? type) => schema?.Type ?? type switch
        {
            CoreValueType.Boolean => "boolean",
            CoreValueType.Number => "number",
            CoreValueType.Text => "string",
            CoreValueType.Entity => "object",
            CoreValueType.TextArray => "array",
            _ => null
        };

        /// <summary>The schema of a member path such as <c>temperature</c> or <c>sensor.temperature</c>.</summary>
        internal static ValueSchema? Member(ValueSchema? schema, string path)
        {
            foreach (var part in path.Split('.'))
                if (schema?.Properties == null || !schema.Properties.TryGetValue(part, out schema))
                    return null;
            return schema;
        }
    }
}
