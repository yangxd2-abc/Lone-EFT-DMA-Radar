/*
 * Lone EFT DMA Radar - Copyright (c) 2026 Lone DMA
 * Licensed under GNU AGPLv3. See https://www.gnu.org/licenses/agpl-3.0.html
 */
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Authentication;

namespace LoneEftDmaRadar.Web.TarkovDev
{
    /// <summary>
    /// Adapter for the static JSON API exposed by json.tarkov.dev.
    /// The legacy class name is retained to avoid invalidating existing callers.
    /// </summary>
    internal static class TarkovDevGraphQLApi
    {
        private const string BaseUrl = "https://json.tarkov.dev/pve/";

        internal static void Configure(IServiceCollection services)
        {
            services.AddHttpClient(nameof(TarkovDevGraphQLApi), client =>
            {
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
                client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
                client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                SslOptions = new()
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                },
                AllowAutoRedirect = true,
                AutomaticDecompression =
                    DecompressionMethods.Brotli |
                    DecompressionMethods.GZip |
                    DecompressionMethods.Deflate
            })
            .AddStandardResilienceHandler(options =>
            {
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(120);
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(45);
                options.CircuitBreaker.SamplingDuration = options.AttemptTimeout.Timeout * 2;
            });
        }

        /// <summary>
        /// Retrieves and adapts the items, maps and tasks JSON endpoints to the
        /// data model already consumed by the rest of the application.
        /// </summary>
        public static async Task<TarkovDevTypes.DataElement> GetTarkovDataAsync()
        {
            var client = Program.HttpClientFactory.CreateClient(nameof(TarkovDevGraphQLApi));
            var documents = await Task.WhenAll(
                GetJsonAsync(client, "items"),
                GetJsonAsync(client, "maps"),
                GetJsonAsync(client, "tasks"));

            try
            {
                var itemsRoot = GetData(documents[0]);
                var mapsRoot = GetData(documents[1]);
                var tasksRoot = GetData(documents[2]);

                var marketItems = ProcessItems(itemsRoot);
                var maps = ProcessMaps(mapsRoot);
                var tasks = ProcessTasks(tasksRoot, mapsRoot, maps, marketItems);
                AddStaticContainers(mapsRoot, marketItems);

                return new TarkovDevTypes.DataElement
                {
                    Items = marketItems,
                    Maps = maps,
                    Tasks = tasks
                };
            }
            finally
            {
                foreach (var document in documents)
                    document.Dispose();
            }
        }

        private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string endpoint)
        {
            using var response = await client.GetAsync(BaseUrl + endpoint,
                HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"json.tarkov.dev/{endpoint} returned {(int)response.StatusCode} " +
                    $"({response.StatusCode}): {body}",
                    null,
                    response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            return await JsonDocument.ParseAsync(stream);
        }

        private static JsonElement GetData(JsonDocument document)
        {
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("json.tarkov.dev response contains no data object.");
            }
            return data;
        }

        private static List<TarkovMarketItem> ProcessItems(JsonElement root)
        {
            var result = new List<TarkovMarketItem>();
            var categories = ReadCategoryNames(root);
            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
                return result;

            foreach (var property in items.EnumerateObject())
            {
                var item = property.Value;
                string id = ReadString(item, "id") ?? property.Name;
                TarkovMarketItem existing = null;
                Tarkov.TarkovDataManager.AllItems?.TryGetValue(id, out existing);

                var tags = existing?.Tags is not null
                    ? new HashSet<string>(existing.Tags, StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (item.TryGetProperty("categories", out var itemCategories) &&
                    itemCategories.ValueKind == JsonValueKind.Array)
                {
                    foreach (var categoryId in itemCategories.EnumerateArray())
                    {
                        if (categoryId.GetString() is string key && categories.TryGetValue(key, out var name))
                            tags.Add(name);
                    }
                }

                long basePrice = ReadInt64(item, "basePrice");
                long avg24hPrice = ReadInt64(item, "avg24hPrice");
                long lastLowPrice = ReadInt64(item, "lastLowPrice");
                result.Add(new TarkovMarketItem
                {
                    BsgId = id,
                    Name = existing?.Name ?? ReadDisplayName(item, id),
                    ShortName = existing?.ShortName ?? ReadShortName(item, id),
                    Tags = tags,
                    TraderPrice = ReadHighestTraderPrice(item),
                    FleaPrice = GetOptimalFleaPrice(basePrice, avg24hPrice, lastLowPrice),
                    Slots = Math.Max(1, ReadInt32(item, "width") * ReadInt32(item, "height"))
                });
            }
            return result;
        }

        private static Dictionary<string, string> ReadCategoryNames(JsonElement root)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!root.TryGetProperty("itemCategories", out var categories) ||
                categories.ValueKind != JsonValueKind.Object)
                return result;

            foreach (var property in categories.EnumerateObject())
                result[property.Name] = ReadDisplayName(property.Value, property.Name);
            return result;
        }

        private static long ReadHighestTraderPrice(JsonElement item)
        {
            long highest = 0;
            if (!item.TryGetProperty("sellToTrader", out var offers) ||
                offers.ValueKind != JsonValueKind.Array)
                return highest;

            foreach (var offer in offers.EnumerateArray())
                highest = Math.Max(highest, ReadInt64(offer, "priceRUB"));
            return highest;
        }

        private static long GetOptimalFleaPrice(long basePrice, params long[] candidates)
        {
            if (basePrice <= 0)
                return 0;
            foreach (long price in candidates.Where(x => x > 0).Distinct())
            {
                if (FleaTax.Calculate(price, basePrice) < price)
                    return price;
            }
            return 0;
        }

        private static List<TarkovDevTypes.MapElement> ProcessMaps(JsonElement root)
        {
            var result = new List<TarkovDevTypes.MapElement>();
            if (!root.TryGetProperty("maps", out var maps) || maps.ValueKind != JsonValueKind.Object)
                return result;

            foreach (var property in maps.EnumerateObject())
            {
                var map = JsonSerializer.Deserialize(
                    property.Value.GetRawText(),
                    Misc.JSON.AppJsonContext.Default.MapElement);
                if (map is null)
                    continue;

                var existing = Tarkov.TarkovDataManager.MapData?.Values.FirstOrDefault(x =>
                    string.Equals(x.NameId, map.NameId, StringComparison.OrdinalIgnoreCase));
                map.Name = existing?.Name ?? ReadDisplayName(property.Value, property.Name);
                result.Add(map);
            }
            return result;
        }

        private static void AddStaticContainers(JsonElement root, List<TarkovMarketItem> items)
        {
            if (!root.TryGetProperty("lootContainers", out var containers) ||
                containers.ValueKind != JsonValueKind.Object)
                return;

            foreach (var property in containers.EnumerateObject())
            {
                var container = property.Value;
                string id = ReadString(container, "id") ?? property.Name;
                TarkovMarketItem existing = null;
                Tarkov.TarkovDataManager.AllContainers?.TryGetValue(id, out existing);
                items.Add(new TarkovMarketItem
                {
                    BsgId = id,
                    Name = existing?.Name ?? ReadDisplayName(container, id),
                    ShortName = existing?.ShortName ?? ReadDisplayName(container, id),
                    Tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Static Container" },
                    TraderPrice = -1,
                    FleaPrice = -1,
                    Slots = 1
                });
            }
        }

        private static List<TarkovDevTypes.TaskElement> ProcessTasks(
            JsonElement root,
            JsonElement mapsRoot,
            List<TarkovDevTypes.MapElement> maps,
            List<TarkovMarketItem> items)
        {
            var result = new List<TarkovDevTypes.TaskElement>();
            if (!root.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Object)
                return result;

            var mapByNameId = maps
                .Where(x => !string.IsNullOrWhiteSpace(x.NameId))
                .GroupBy(x => x.NameId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            var mapById = new Dictionary<string, TarkovDevTypes.MapElement>(StringComparer.OrdinalIgnoreCase);
            if (mapsRoot.TryGetProperty("maps", out var rawMaps) && rawMaps.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in rawMaps.EnumerateObject())
                {
                    string nameId = ReadString(property.Value, "nameId");
                    if (nameId is not null && mapByNameId.TryGetValue(nameId, out var map))
                        mapById[property.Name] = map;
                }
            }
            var itemById = items
                .DistinctBy(x => x.BsgId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.BsgId, StringComparer.OrdinalIgnoreCase);

            foreach (var property in tasks.EnumerateObject())
            {
                var rawTask = property.Value;
                string taskId = ReadString(rawTask, "id") ?? property.Name;
                TarkovDevTypes.TaskElement existingTask = null;
                Tarkov.TarkovDataManager.TaskData?.TryGetValue(taskId, out existingTask);
                var objectives = new List<TarkovDevTypes.TaskElement.ObjectiveElement>();
                if (rawTask.TryGetProperty("objectives", out var rawObjectives) &&
                    rawObjectives.ValueKind == JsonValueKind.Array)
                {
                    foreach (var objective in rawObjectives.EnumerateArray())
                        objectives.Add(ProcessObjective(objective, mapById, itemById, existingTask));
                }

                result.Add(new TarkovDevTypes.TaskElement
                {
                    Id = taskId,
                    Name = existingTask?.Name ?? ReadDisplayName(rawTask, taskId),
                    Objectives = objectives
                });
            }
            return result;
        }

        private static TarkovDevTypes.TaskElement.ObjectiveElement ProcessObjective(
            JsonElement raw,
            IReadOnlyDictionary<string, TarkovDevTypes.MapElement> maps,
            IReadOnlyDictionary<string, TarkovMarketItem> items,
            TarkovDevTypes.TaskElement existingTask)
        {
            string id = ReadString(raw, "id") ?? string.Empty;
            string type = ReadString(raw, "type") ?? string.Empty;
            var existing = existingTask?.Objectives?.FirstOrDefault(x =>
                string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            var mapList = ReadMapList(raw, maps);
            var zones = ReadZones(raw, maps);
            var itemIds = ReadStringArray(raw, "items");
            string itemId = itemIds.FirstOrDefault();
            string questItemId = ReadString(raw, "questItem");
            var item = CreateMarkerItem(itemId, items);

            return new TarkovDevTypes.TaskElement.ObjectiveElement
            {
                Id = id,
                _type = type,
                Description = existing?.Description ?? ReadString(raw, "description") ?? id,
                RequiredKeys = ReadRequiredKeys(raw, items),
                Maps = mapList,
                Zones = zones,
                Count = ReadInt32(raw, "count"),
                FoundInRaid = ReadBoolean(raw, "foundInRaid"),
                Item = item,
                MarkerItem = type.Equals("mark", StringComparison.OrdinalIgnoreCase) ? item : null,
                QuestItem = CreateQuestItem(questItemId, items)
            };
        }

        private static List<TarkovDevTypes.TaskElement.ObjectiveElement.TaskMapElement> ReadMapList(
            JsonElement raw,
            IReadOnlyDictionary<string, TarkovDevTypes.MapElement> maps)
        {
            var result = new List<TarkovDevTypes.TaskElement.ObjectiveElement.TaskMapElement>();
            foreach (string id in ReadStringArray(raw, "maps"))
                result.Add(CreateTaskMap(id, maps));
            return result;
        }

        private static List<TarkovDevTypes.TaskElement.ObjectiveElement.TaskZoneElement> ReadZones(
            JsonElement raw,
            IReadOnlyDictionary<string, TarkovDevTypes.MapElement> maps)
        {
            var result = new List<TarkovDevTypes.TaskElement.ObjectiveElement.TaskZoneElement>();
            if (!raw.TryGetProperty("zones", out var zones) || zones.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var zone in zones.EnumerateArray())
            {
                if (!zone.TryGetProperty("position", out var position) ||
                    position.ValueKind != JsonValueKind.Object)
                    continue;
                result.Add(new TarkovDevTypes.TaskElement.ObjectiveElement.TaskZoneElement
                {
                    Id = ReadString(zone, "id") ?? string.Empty,
                    Position = ReadPosition(position),
                    Map = CreateTaskMap(ReadString(zone, "map"), maps)
                });
            }
            return result;
        }

        private static TarkovDevTypes.TaskElement.ObjectiveElement.TaskMapElement CreateTaskMap(
            string id,
            IReadOnlyDictionary<string, TarkovDevTypes.MapElement> maps)
        {
            if (id is not null && maps.TryGetValue(id, out var map))
            {
                return new()
                {
                    NameId = map.NameId,
                    Name = map.Name,
                    NormalizedName = NormalizeName(map.Name)
                };
            }
            return new() { NameId = id, Name = id, NormalizedName = NormalizeName(id) };
        }

        private static List<List<TarkovDevTypes.TaskElement.ObjectiveElement.MarkerItemClass>> ReadRequiredKeys(
            JsonElement raw,
            IReadOnlyDictionary<string, TarkovMarketItem> items)
        {
            var result = new List<List<TarkovDevTypes.TaskElement.ObjectiveElement.MarkerItemClass>>();
            if (!raw.TryGetProperty("requiredKeys", out var groups) || groups.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var group in groups.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Array)
                    continue;
                result.Add(group.EnumerateArray()
                    .Select(x => CreateMarkerItem(x.GetString(), items))
                    .Where(x => x is not null)
                    .ToList());
            }
            return result;
        }

        private static TarkovDevTypes.TaskElement.ObjectiveElement.MarkerItemClass CreateMarkerItem(
            string id,
            IReadOnlyDictionary<string, TarkovMarketItem> items)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;
            items.TryGetValue(id, out var item);
            return new()
            {
                Id = id,
                Name = item?.Name ?? id,
                ShortName = item?.ShortName ?? id
            };
        }

        private static TarkovDevTypes.TaskElement.ObjectiveElement.ObjectiveQuestItem CreateQuestItem(
            string id,
            IReadOnlyDictionary<string, TarkovMarketItem> items)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;
            items.TryGetValue(id, out var item);
            return new()
            {
                Id = id,
                Name = item?.Name ?? id,
                ShortName = item?.ShortName ?? id,
                NormalizedName = NormalizeName(item?.Name ?? id),
                Description = item?.Name ?? id
            };
        }

        private static List<string> ReadStringArray(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var array) ||
                array.ValueKind != JsonValueKind.Array)
                return new();
            return array.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
        }

        private static Vector3 ReadPosition(JsonElement position) => new(
            ReadSingle(position, "x"),
            ReadSingle(position, "y"),
            ReadSingle(position, "z"));

        private static string ReadDisplayName(JsonElement element, string id)
        {
            string normalized = ReadString(element, "normalizedName");
            if (!string.IsNullOrWhiteSpace(normalized))
                return PrettyName(normalized);
            string name = ReadString(element, "name");
            return IsPlaceholder(name, id) ? id : name ?? id;
        }

        private static string ReadShortName(JsonElement element, string id)
        {
            string shortName = ReadString(element, "shortName");
            return IsPlaceholder(shortName, id) ? ReadDisplayName(element, id) : shortName;
        }

        private static bool IsPlaceholder(string value, string id) =>
            string.IsNullOrWhiteSpace(value) ||
            value.StartsWith(id + " ", StringComparison.OrdinalIgnoreCase);

        private static string PrettyName(string value) => string.Join(' ',
            value.Split('-', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => char.ToUpperInvariant(x[0]) + x[1..]));

        private static string NormalizeName(string value) =>
            string.IsNullOrWhiteSpace(value)
                ? value
                : value.Trim().ToLowerInvariant().Replace(' ', '-');

        private static string ReadString(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static int ReadInt32(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value))
                return 0;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
                return number;
            if (value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return number;
            return 0;
        }

        private static long ReadInt64(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value))
                return 0;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
                return number;
            if (value.ValueKind == JsonValueKind.String &&
                long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return number;
            return 0;
        }

        private static float ReadSingle(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value))
                return 0;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float number))
                return number;
            if (value.ValueKind == JsonValueKind.String &&
                float.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                return number;
            return 0;
        }

        private static bool ReadBoolean(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            value.GetBoolean();
    }
}
