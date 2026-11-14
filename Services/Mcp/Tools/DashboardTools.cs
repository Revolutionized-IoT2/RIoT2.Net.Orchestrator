using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RIoT2.Net.Orchestrator.Services.Dashboard;

namespace RIoT2.Net.Orchestrator.Services.Mcp.Tools
{
    /// <summary>
    /// The <see cref="McpToolGroups.Dashboard"/> tools (I60), off by default: read the dashboard in its compact form, change its
    /// pages and cards, replace it whole, and undo with its versions. Cards are checked against the UI's card catalog and the
    /// templates; every change records a dashboard version, and open UIs reload.
    /// </summary>
    [McpServerToolType]
    public sealed class DashboardTools(DashboardEditor editor, DashboardCardCatalogService catalogs, DashboardVersionService versions, McpJson json)
    {
        private const string CardHelp = "Cards: { kind, name, icon?, size?, settings?, elements: [{ role?, reportId?, commandId?, name?, icon?, properties? }] }; " +
            "list_dashboard_cards with a kind gives its settings, element slots and an example.";

        [McpServerTool(Name = "get_dashboard", Title = "Get the dashboard", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Dashboard)]
        [Description("Gets the dashboard: its pages, each with its cards (id, kind, name, icon, size, settings) and each card's elements (the " +
            "report or command they show, and their properties). Give a pageId for one page.")]
        public CallToolResult GetDashboard([Description("Only this page's id; omit for every page.")] string? pageId = null)
        {
            var dashboard = DashboardEditor.Compact(editor.Current(), string.IsNullOrWhiteSpace(pageId) ? null : pageId.Trim());
            if (dashboard == null)
                return McpJson.Error($"There is no page '{pageId}'.");
            if (pageId == null)
                dashboard["cardCatalog"] = catalogs.Get()?.UiVersion is { } ui ? $"from UI {ui}" : DashboardEditor.NoCatalog;
            return McpJson.Text(dashboard.ToJsonString());
        }

        [McpServerTool(Name = "list_dashboard_cards", Title = "List dashboard card kinds", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Dashboard)]
        [Description("Lists the dashboard card kinds (gauge, sensor, light, chart, ...) with what each shows and needs. Give a kind for its " +
            "settings with their defaults, its element slots (which report or command each takes, and their properties) and a sample card.")]
        public CallToolResult ListDashboardCards([Description("A card kind for its details; omit for the list.")] string? kind = null)
        {
            var catalog = catalogs.Get();
            if (catalog == null)
                return McpJson.Error(DashboardEditor.NoCatalog);
            if (!string.IsNullOrWhiteSpace(kind))
                return catalog.Find(kind.Trim()) is { } card ? json.Result(card)
                    : McpJson.Error($"There is no card kind '{kind}'; the kinds are {string.Join(", ", catalog.Cards.Select(x => x.Kind))}.");
            var list = new JsonArray([.. catalog.Cards.Select(card => (JsonNode)new JsonObject
            {
                ["kind"] = card.Kind,
                ["title"] = card.Title,
                ["description"] = card.Description,
                ["category"] = card.Category,
                ["elements"] = ElementSummary(card)
            })]);
            return McpJson.Text(new JsonObject { ["uiVersion"] = catalog.UiVersion, ["cards"] = list }.ToJsonString());
        }

        [McpServerTool(Name = "add_dashboard_page", Title = "Add a dashboard page", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Dashboard, Write = true)]
        [Description("Adds an empty page to the dashboard and returns it with its id.")]
        public CallToolResult AddDashboardPage(
            [Description("The page's name.")] string name,
            [Description("A Material Design Icons name, such as mdi-home-outline.")] string? icon = null,
            [Description("Where to put it, from 0; omit for the end.")] int? position = null) =>
            Edit(false, session =>
            {
                var page = new JsonObject
                {
                    ["id"] = DashboardEditSession.NewId(),
                    ["name"] = DashboardEditSession.PageName(name),
                    ["icon"] = DashboardEditSession.Icon(icon),
                    ["components"] = new JsonArray()
                };
                DashboardEditSession.Insert(session.PageList, DashboardEditSession.Position(position, session.PageList.Count), page);
                session.Summary = $"Added the page '{page["name"]}'";
                return DashboardEditor.CompactPage(page);
            });

        [McpServerTool(Name = "update_dashboard_page", Title = "Change a dashboard page", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Dashboard, Write = true)]
        [Description("Renames a page, changes its icon or moves it.")]
        public CallToolResult UpdateDashboardPage(
            [Description("The page's id from get_dashboard.")] string pageId,
            [Description("A new name.")] string? name = null,
            [Description("A new Material Design Icons name.")] string? icon = null,
            [Description("A new place, from 0.")] int? position = null) =>
            Edit(false, session =>
            {
                var page = session.Page(pageId);
                if (name != null)
                    page["name"] = DashboardEditSession.PageName(name);
                if (icon != null)
                    page["icon"] = DashboardEditSession.Icon(icon);
                if (position != null)
                {
                    session.PageList.Remove(page);
                    DashboardEditSession.Insert(session.PageList, DashboardEditSession.Position(position, session.PageList.Count), page);
                }
                session.Summary = $"Changed the page '{page["name"]}'";
                return DashboardEditor.CompactPage(page);
            });

        [McpServerTool(Name = "remove_dashboard_page", Title = "Remove a dashboard page", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Dashboard, Write = true)]
        [Description("Removes a page and its cards. restore_dashboard_version undoes it.")]
        public CallToolResult RemoveDashboardPage([Description("The page's id from get_dashboard.")] string pageId) =>
            Edit(false, session =>
            {
                var page = session.Page(pageId);
                var cards = DashboardEditor.Cards(page).Count();
                session.PageList.Remove(page);
                session.Summary = $"Removed the page '{page["name"]}' and its {cards} cards";
                return new JsonObject { ["removed"] = pageId, ["cards"] = cards };
            });

        [McpServerTool(Name = "add_dashboard_card", Title = "Add a dashboard card", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Dashboard, Write = true)]
        [Description("Adds a card to a page and returns it. The kind's settings start from its defaults; elements bind reports and commands " +
            "from list_values. A card with fixed slots (such as light) gives each element its role. " + CardHelp)]
        public CallToolResult AddDashboardCard(
            [Description("The page's id from get_dashboard.")] string pageId,
            [Description("The card kind from list_dashboard_cards.")] string kind,
            [Description("The card's title.")] string name,
            [Description("The elements: [{ role?, reportId?, commandId?, name?, icon?, properties? }].")] JsonElement? elements = null,
            [Description("Settings that differ from the kind's defaults.")] JsonElement? settings = null,
            [Description("A Material Design Icons name.")] string? icon = null,
            [Description("small, half, large or full; omit for the kind's default.")] string? size = null,
            [Description("Where to put it on the page, from 0; omit for the end.")] int? position = null) =>
            Edit(true, session =>
            {
                var page = session.Page(pageId);
                var card = session.BuildCard(new DashboardCardInput(null, kind, name, icon, Size(size), Object(settings, "settings"),
                    Array(elements) ?? []), null);
                if (card == null)
                    return null;
                var cards = DashboardEditSession.Components(page);
                DashboardEditSession.Insert(cards, DashboardEditSession.Position(position, cards.Count), card);
                session.Summary = $"Added the {kind} card '{card["name"]}' to the page '{page["name"]}'";
                return DashboardEditor.CompactCard(card);
            });

        [McpServerTool(Name = "update_dashboard_card", Title = "Change a dashboard card", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Dashboard, Write = true)]
        [Description("Changes a card. Settings merge by key (null restores a default); elements, when given, replace the card's elements " +
            "(keep an element's id to keep it). A new kind needs its elements too. " + CardHelp)]
        public CallToolResult UpdateDashboardCard(
            [Description("The card's id from get_dashboard.")] string cardId,
            [Description("A new title.")] string? name = null,
            [Description("Settings to change.")] JsonElement? settings = null,
            [Description("New elements, replacing the card's.")] JsonElement? elements = null,
            [Description("A new Material Design Icons name.")] string? icon = null,
            [Description("small, half, large or full.")] string? size = null,
            [Description("A new card kind.")] string? kind = null) =>
            Edit(true, session =>
            {
                var (page, existing) = session.Card(cardId);
                var card = session.BuildCard(new DashboardCardInput(cardId, kind, name, icon, Size(size), Object(settings, "settings"), Array(elements)), existing);
                if (card == null)
                    return null;
                var cards = DashboardEditSession.Components(page);
                cards[cards.IndexOf(existing)] = card;
                session.Summary = $"Changed the card '{card["name"]}' on the page '{page["name"]}'";
                return DashboardEditor.CompactCard(card);
            });

        [McpServerTool(Name = "move_dashboard_card", Title = "Move a dashboard card", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Dashboard, Write = true)]
        [Description("Moves a card within its page or to another page.")]
        public CallToolResult MoveDashboardCard(
            [Description("The card's id from get_dashboard.")] string cardId,
            [Description("The page to move it to; omit to stay on its page.")] string? pageId = null,
            [Description("Its new place on the page, from 0; omit for the end.")] int? position = null) =>
            Edit(false, session =>
            {
                var (from, card) = session.Card(cardId);
                var to = string.IsNullOrWhiteSpace(pageId) ? from : session.Page(pageId);
                DashboardEditSession.Components(from).Remove(card);
                var cards = DashboardEditSession.Components(to);
                DashboardEditSession.Insert(cards, DashboardEditSession.Position(position, cards.Count), card);
                session.Summary = $"Moved the card '{card["name"]}' to the page '{to["name"]}'";
                return new JsonObject { ["cardId"] = cardId, ["pageId"] = DashboardEditor.Id(to), ["position"] = cards.IndexOf(card) };
            });

        [McpServerTool(Name = "remove_dashboard_card", Title = "Remove a dashboard card", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Dashboard, Write = true)]
        [Description("Removes a card. restore_dashboard_version undoes it.")]
        public CallToolResult RemoveDashboardCard([Description("The card's id from get_dashboard.")] string cardId) =>
            Edit(false, session =>
            {
                var (page, card) = session.Card(cardId);
                DashboardEditSession.Components(page).Remove(card);
                session.Summary = $"Removed the card '{card["name"]}' from the page '{page["name"]}'";
                return new JsonObject { ["removed"] = cardId };
            });

        [McpServerTool(Name = "save_dashboard", Title = "Replace the dashboard", ReadOnly = false, Idempotent = true, Destructive = true, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Dashboard, Write = true)]
        [Description("Replaces the whole dashboard with { pages: [{ id?, name, icon?, cards: [...] }] }, as get_dashboard returns it. Pages and " +
            "cards that keep their ids keep what they leave out; those left out are removed; new ones get ids. Prefer the page and card " +
            "tools for small changes. " + CardHelp)]
        public CallToolResult SaveDashboard([Description("The whole dashboard in get_dashboard's form.")] JsonElement dashboard) =>
            Edit(true, session =>
            {
                var input = Object(dashboard, "dashboard");
                if (input?["pages"] is not JsonArray pageInputs)
                    throw new DashboardEditException("The dashboard must be an object with pages.");
                var oldPages = DashboardEditor.Pages(session.Dashboard).ToDictionary(DashboardEditor.Id, StringComparer.Ordinal);
                var oldCards = oldPages.Values.SelectMany(DashboardEditor.Cards).ToDictionary(DashboardEditor.Id, StringComparer.Ordinal);
                var usedPages = new HashSet<string>(StringComparer.Ordinal);
                var usedCards = new HashSet<string>(StringComparer.Ordinal);
                var pages = new JsonArray();
                foreach (var pageInput in pageInputs)
                {
                    if (pageInput is not JsonObject pageObject)
                        throw new DashboardEditException("Every page must be an object.");
                    var pageId = DashboardCardInput.Text(pageObject["id"]);
                    var keepPage = pageId != null && oldPages.ContainsKey(pageId) && usedPages.Add(pageId);
                    var page = new JsonObject
                    {
                        ["id"] = keepPage ? pageId : DashboardEditSession.NewId(),
                        ["name"] = DashboardEditSession.PageName(DashboardCardInput.Text(pageObject["name"])),
                        ["icon"] = DashboardEditSession.Icon(DashboardCardInput.Text(pageObject["icon"]))
                    };
                    var cards = new JsonArray();
                    foreach (var cardInput in pageObject["cards"] as JsonArray ?? [])
                    {
                        if (cardInput is not JsonObject cardObject)
                            throw new DashboardEditException("Every card must be an object.");
                        var cardData = DashboardCardInput.From(cardObject);
                        var existing = cardData.Id != null && oldCards.TryGetValue(cardData.Id, out var old) && usedCards.Add(cardData.Id) ? old : null;
                        if (session.BuildCard(cardData, existing) is { } card)
                            cards.Add(card);
                    }
                    page["components"] = cards;
                    pages.Add(page);
                }
                session.Dashboard["pages"] = pages;
                var cardCount = pages.Sum(page => ((JsonArray)page!["components"]!).Count);
                session.Summary = $"Replaced the dashboard: {pages.Count} pages, {cardCount} cards";
                return new JsonObject { ["pages"] = pages.Count, ["cards"] = cardCount, ["removedCards"] = oldCards.Count - usedCards.Count };
            });

        [McpServerTool(Name = "list_dashboard_versions", Title = "List dashboard versions", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Dashboard)]
        [Description("Lists the dashboard's saved versions, newest first (the newest is the dashboard now): when, by whom and what changed.")]
        public CallToolResult ListDashboardVersions() =>
            json.Result(versions.List().Select(DashboardVersionInfo.From).ToList());

        [McpServerTool(Name = "restore_dashboard_version", Title = "Restore a dashboard version", ReadOnly = false, Idempotent = true, Destructive = true, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Dashboard, Write = true)]
        [Description("Undoes changes: makes an earlier version from list_dashboard_versions the dashboard again (as a new version).")]
        public CallToolResult RestoreDashboardVersion([Description("The version's id from list_dashboard_versions.")] string versionId)
        {
            try
            {
                var latest = versions.Restore(versionId?.Trim() ?? "", Caller);
                return latest == null
                    ? McpJson.Error($"There is no dashboard version '{versionId}'.")
                    : json.Result(DashboardVersionInfo.From(latest));
            }
            catch (InvalidOperationException x)
            {
                return McpJson.Error(x.Message);
            }
        }

        private CallToolResult Edit(bool needsCatalog, Func<DashboardEditSession, JsonNode?> edit)
        {
            DashboardEditResult result;
            try
            {
                result = editor.Edit(Caller, needsCatalog, edit);
            }
            catch (DashboardEditException x)
            {
                return McpJson.Error(x.Message);
            }
            if (!result.Succeeded)
                return McpJson.Error(string.Join("\n", result.Errors.Concat(result.Warnings)));
            var output = result.Result as JsonObject ?? new JsonObject { ["result"] = result.Result };
            output["versionId"] = result.VersionId;
            output["changed"] = result.Changed;
            if (result.Warnings.Count > 0)
                output["warnings"] = new JsonArray([.. result.Warnings.Select(x => (JsonNode)x)]);
            return McpJson.Text(output.ToJsonString());
        }

        private static string Caller => McpCaller.Current?.User ?? "";

        private static string ElementSummary(CardKind card)
        {
            var layout = card.Elements;
            if (layout == null)
                return "none";
            static string Slot(CardSlot slot) =>
                $"{slot.Label} (report {slot.Report}, command {slot.Command}{(slot.Accepts is { Count: > 0 } accepts ? $", suits {string.Join("/", accepts)}" : "")})";
            if (layout.Each != null)
                return $"{(layout.Min == layout.Max ? $"{layout.Min}" : $"{layout.Min}-{layout.Max}")} x {Slot(layout.Each)}";
            return string.Join("; ", (layout.Slots ?? []).Select(slot => $"role {slot.Role}: {Slot(slot)}"));
        }

        private static JsonNode? Size(string? size) => string.IsNullOrWhiteSpace(size) ? null
            : int.TryParse(size, out var columns) ? JsonValue.Create(columns) : JsonValue.Create(size.Trim());

        private static JsonObject? Object(JsonElement? value, string name)
        {
            var node = value is { } element ? McpJson.Argument(element) : null;
            return node switch
            {
                null => null,
                JsonObject obj => obj,
                _ => throw new DashboardEditException($"{name} must be an object.")
            };
        }

        private static JsonArray? Array(JsonElement? value)
        {
            var node = value is { } element ? McpJson.Argument(element) : null;
            return node switch
            {
                null => null,
                JsonArray array => array,
                _ => throw new DashboardEditException("elements must be a list.")
            };
        }
    }
}
