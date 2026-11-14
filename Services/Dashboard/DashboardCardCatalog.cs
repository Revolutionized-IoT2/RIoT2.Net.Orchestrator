using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RIoT2.Core.Interfaces;
using RIoT2.Net.Orchestrator.Services.Persistence;

namespace RIoT2.Net.Orchestrator.Services.Dashboard
{
    /// <summary>
    /// The UI's dashboard card kinds (ADR 0018, I60): what each kind is, its settings with their defaults and its element
    /// slots. The UI builds it from its card definitions and uploads it; the orchestrator keeps the newest UI version's as
    /// <c>StoredObjects/DashboardCardCatalog/default.json</c>, and the MCP dashboard tools build and check cards with it.
    /// </summary>
    public sealed class DashboardCardCatalog : IStoredObject
    {
        public const string DefaultId = "default";
        public const string FormatName = "riot2-card-catalog";
        public const int CurrentVersion = 1;

        public string Id { get; set; } = DefaultId;
        public string Format { get; set; } = FormatName;
        public int Version { get; set; } = CurrentVersion;

        /// <summary>The version of the UI that uploaded it.</summary>
        public string UiVersion { get; set; } = "";

        /// <summary>The UI's hash of <see cref="Cards"/>; the UI uploads again when its own differs.</summary>
        public string Hash { get; set; } = "";

        public DateTimeOffset UploadedAt { get; set; }
        public List<CardKind> Cards { get; set; } = [];

        public CardKind? Find(string? kind) => kind == null ? null : Cards.FirstOrDefault(x => x.Kind == kind);
    }

    /// <summary>One card kind, as RIoT2.UI's <c>defineCard</c> declares it (functions left out).</summary>
    public sealed class CardKind
    {
        public string Kind { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Category { get; set; } = "";
        public string? Icon { get; set; }

        /// <summary>The size a new card gets: 3, 6, 9 or 12 columns.</summary>
        public int DefaultSize { get; set; }

        public bool Compact { get; set; }

        /// <summary>The legacy <c>type</c> older readers see, for the default settings.</summary>
        public int LegacyType { get; set; }

        /// <summary>When the legacy type depends on one setting: that setting and the type for each of its values.</summary>
        public CardLegacyTypeBy? LegacyTypeBy { get; set; }

        public JsonObject Defaults { get; set; } = [];
        public List<CardField> Settings { get; set; } = [];
        public CardElementLayout? Elements { get; set; }

        /// <summary>The gallery's sample card: a worked example.</summary>
        public JsonObject? Sample { get; set; }
    }

    public sealed class CardLegacyTypeBy
    {
        public string Key { get; set; } = "";
        public Dictionary<string, int> Values { get; set; } = [];
    }

    /// <summary>One setting of a card or element, as the UI's settings form edits it.</summary>
    public sealed class CardField
    {
        public string Key { get; set; } = "";

        /// <summary>text, textarea, markdown, number, boolean, select, icon, color, unit, member, value or list.</summary>
        public string Type { get; set; } = "";

        public string Label { get; set; } = "";
        public string? Hint { get; set; }
        public double? Min { get; set; }
        public double? Max { get; set; }
        public double? Step { get; set; }
        public List<CardOption>? Options { get; set; }

        /// <summary>A list's item fields.</summary>
        public List<CardField>? Fields { get; set; }

        /// <summary>A member field's value types.</summary>
        public List<string>? Accepts { get; set; }

        /// <summary>A value field's source: command or report.</summary>
        public string? Source { get; set; }

        /// <summary>The UI shows the field only in some cases (a condition that can't be serialized).</summary>
        public bool Conditional { get; set; }
    }

    public sealed class CardOption
    {
        public JsonNode? Value { get; set; }
        public string Title { get; set; } = "";
    }

    /// <summary>A card's elements: a list of alike items (<see cref="Each"/>, <see cref="Min"/>-<see cref="Max"/>) or fixed slots.</summary>
    public sealed class CardElementLayout
    {
        public CardSlot? Each { get; set; }
        public int? Min { get; set; }
        public int? Max { get; set; }
        public List<CardSlot>? Slots { get; set; }
    }

    /// <summary>What one element binds: a report to show, a command to send, and its own settings.</summary>
    public sealed class CardSlot
    {
        public string? Role { get; set; }
        public string Label { get; set; } = "";
        public string? Hint { get; set; }

        /// <summary>required, optional or none.</summary>
        public string Report { get; set; } = CardRequirement.None;

        /// <summary>required, optional or none.</summary>
        public string Command { get; set; } = CardRequirement.None;

        public List<string>? Accepts { get; set; }
        public int? History { get; set; }
        public JsonObject? Defaults { get; set; }
        public List<CardField>? Settings { get; set; }
    }

    public static class CardRequirement
    {
        public const string Required = "required";
        public const string Optional = "optional";
        public const string None = "none";
        public static readonly IReadOnlyList<string> All = [Required, Optional, None];
    }

    /// <summary>The outcome of an upload: the stored catalog, or the problems, and whether a newer UI's catalog is kept.</summary>
    public sealed record CardCatalogSaveResult(DashboardCardCatalog? Catalog, IReadOnlyList<string> Errors, bool Conflict = false);

    /// <summary>Reads and stores the <see cref="DashboardCardCatalog"/>.</summary>
    public sealed partial class DashboardCardCatalogService(IStoredObjectStore store, TimeProvider time)
    {
        public const int MaxCards = 200;
        public const string FieldTypes = "text, textarea, markdown, number, boolean, select, icon, color, unit, member, value, list";
        private static readonly HashSet<string> KnownFieldTypes =
            ["text", "textarea", "markdown", "number", "boolean", "select", "icon", "color", "unit", "member", "value", "list"];

        public DashboardCardCatalog? Get() => store.Get<DashboardCardCatalog>(DashboardCardCatalog.DefaultId);

        /// <summary>
        /// Stores <paramref name="upload"/> unless it is invalid, or comes from an older UI than the stored catalog
        /// (<see cref="CardCatalogSaveResult.Conflict"/>), so two UI versions don't replace each other's.
        /// </summary>
        public CardCatalogSaveResult Save(DashboardCardCatalog upload)
        {
            ArgumentNullException.ThrowIfNull(upload);
            var errors = Validate(upload);
            if (errors.Count > 0)
                return new(null, errors);
            var stored = Get();
            if (stored != null && CompareVersions(upload.UiVersion, stored.UiVersion) < 0)
                return new(null, [$"The stored catalog is from the newer UI {stored.UiVersion}; this UI is {upload.UiVersion}."], true);

            upload.Id = DashboardCardCatalog.DefaultId;
            upload.UploadedAt = time.GetUtcNow();
            return store.Save(upload) == null ? new(null, ["The card catalog could not be stored."]) : new(upload, []);
        }

        internal static List<string> Validate(DashboardCardCatalog catalog)
        {
            var errors = new List<string>();
            if (catalog.Format != DashboardCardCatalog.FormatName)
                errors.Add($"format must be '{DashboardCardCatalog.FormatName}'.");
            if (catalog.Version != DashboardCardCatalog.CurrentVersion)
                errors.Add($"version must be {DashboardCardCatalog.CurrentVersion}.");
            if (string.IsNullOrWhiteSpace(catalog.UiVersion) || catalog.UiVersion.Length > 50)
                errors.Add("uiVersion must be 1-50 characters.");
            if (catalog.Hash?.Length > 200)
                errors.Add("hash must be at most 200 characters.");
            if (catalog.Cards == null || catalog.Cards.Count is 0 or > MaxCards)
            {
                errors.Add($"cards must have 1-{MaxCards} card kinds.");
                return errors;
            }
            foreach (var group in catalog.Cards.GroupBy(x => x?.Kind).Where(x => x.Count() > 1))
                errors.Add($"The kind '{group.Key}' is in the catalog twice.");
            foreach (var card in catalog.Cards)
            {
                if (card == null || string.IsNullOrWhiteSpace(card.Kind) || !KindPattern().IsMatch(card.Kind))
                {
                    errors.Add("Every card needs a kind of letters, digits, '-' and '_' (at most 50).");
                    continue;
                }
                if (card.DefaultSize is not (3 or 6 or 9 or 12))
                    errors.Add($"{card.Kind}: defaultSize must be 3, 6, 9 or 12.");
                card.Defaults ??= [];
                card.Settings ??= [];
                ValidateFields(card.Kind, card.Settings, errors);
                var layout = card.Elements;
                if (layout == null)
                    continue;
                if ((layout.Each == null) == (layout.Slots == null))
                    errors.Add($"{card.Kind}: elements must have either each or slots.");
                if (layout.Each != null && (layout.Min is null or < 0 || layout.Max is null || layout.Max < layout.Min))
                    errors.Add($"{card.Kind}: elements with each need min and max.");
                foreach (var slot in (layout.Slots ?? []).Concat(layout.Each == null ? [] : [layout.Each]))
                {
                    if (slot == null)
                    {
                        errors.Add($"{card.Kind}: an element slot is empty.");
                        continue;
                    }
                    if (!CardRequirement.All.Contains(slot.Report) || !CardRequirement.All.Contains(slot.Command))
                        errors.Add($"{card.Kind}: a slot's report and command must be required, optional or none.");
                    ValidateFields(card.Kind, slot.Settings ?? [], errors);
                }
                if (layout.Slots != null && layout.Slots.Any(x => string.IsNullOrEmpty(x?.Role)))
                    errors.Add($"{card.Kind}: every slot needs a role.");
            }
            return errors;
        }

        private static void ValidateFields(string kind, List<CardField> fields, List<string> errors)
        {
            foreach (var field in fields)
            {
                if (field == null || string.IsNullOrWhiteSpace(field.Key) || !KnownFieldTypes.Contains(field.Type))
                {
                    errors.Add($"{kind}: every setting needs a key and a type ({FieldTypes}).");
                    continue;
                }
                if (field.Type == "list")
                    ValidateFields(kind, field.Fields ?? [], errors);
            }
        }

        /// <summary>Compares UI versions by their numeric part (<c>1.14.0-dev</c> is 1.14.0); an unreadable one is the oldest.</summary>
        internal static int CompareVersions(string? a, string? b) => Parse(a).CompareTo(Parse(b));

        private static Version Parse(string? text)
        {
            var core = (text ?? "").Split('-', '+')[0].Trim().TrimStart('v');
            return Version.TryParse(core, out var version) ? version : new Version(0, 0);
        }

        [GeneratedRegex("^[A-Za-z0-9_-]{1,50}$")]
        private static partial Regex KindPattern();
    }
}
