using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Game.Command;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads; 
using Dalamud.Game.Network.Structures;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using System.Diagnostics;

namespace MarketSniper
{
    public class SnipedItemRecord
    {
        public uint ItemId { get; set; }
        public string ItemName { get; set; } = "Unknown Item";
        public int LocalLowPrice { get; set; }
        public int DcLowestPrice { get; set; }
        public string DcCheapestWorld { get; set; } = "Unknown";
        public int RegionLowestPrice { get; set; }
        public string RegionCheapestWorld { get; set; } = "Unknown";
        public int PotentialProfit { get; set; }
        public string TimeString { get; set; } = string.Empty;
        public bool IsExcellentDeal { get; set; }
        public DateTime LastScannedDateTime { get; set; } = DateTime.Now;
    }

    public class MarketSniper : IDalamudPlugin
    {
        [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
        [PluginService] public static IChatGui Chat { get; private set; } = null!;
        [PluginService] public static IMarketBoard MarketBoard { get; private set; } = null!;
        [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
        [PluginService] public static IPluginLog Log { get; private set; } = null!;
        [PluginService] public static IDataManager DataManager { get; private set; } = null!;
        [PluginService] public static IFramework Framework { get; private set; } = null!;
        [PluginService] public static IClientState ClientState { get; private set; } = null!; 

        private readonly HttpClient httpClient = new();
        private bool isUiOpen = true; 
        private int snipeDiscountPercentage = 30; 

        private List<SnipedItemRecord> scannedHistoryList = new();
        private string saveFilePath = string.Empty;

        private uint dynamicTargetItemId = 0;
        private string myManualHomeWorld = "Balmung"; 
        private string myAutomatedDataCenter = "Crystal"; 
        private string myCurrentRegionGroup = "North-America"; 
        private string logSearchQuery = string.Empty;

        private readonly Dictionary<string, string[]> regionalDcGroups = new()
        {
            { "North-America", new[] { "Aether", "Primal", "Crystal", "Dynamis" } },
            { "Europe", new[] { "Chaos", "Light" } },
            { "Japan", new[] { "Elemental", "Gaia", "Mana", "Meteor" } },
            { "Oceania", new[] { "Materia" } }
        };

        private readonly Dictionary<string, string> worldToDcMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<uint, DateTime> lastScannedTimes = new();
        private readonly object debounceLock = new();

        public MarketSniper()
        {
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MarketSniperFFXIVDalamudPlugin/1.0.0");

            try
            {
                saveFilePath = Path.Combine(PluginInterface.GetPluginConfigDirectory(), "SniperHistoryCache.json");
                LoadHistoryFromFile();
                BuildStaticWorldMappingCache();
            }
            catch (Exception ex)
            {
                Log.Error($"Could not initialize save storage directory: {ex.Message}");
            }

            Framework.RunOnFrameworkThread(() => Chat.Print("====== MARKET SNIPER ENGINE LOADING ======"));
            Log.Information("Marketboard Sniper API Sync Initializing...");

            try
            {
                CommandManager.AddHandler("/msniper", new CommandInfo(OnCommand)
                {
                    HelpMessage = "Toggle the master history tracking dashboard."
                });
            }
            catch (Exception ex) { Log.Error($"Failed to register command: {ex.Message}"); }

            MarketBoard.OfferingsReceived += OnOfferingsReceived;
            PluginInterface.UiBuilder.Draw += DrawUi;

            UpdateLocationDetailsFromClient();

            Framework.RunOnFrameworkThread(() => Chat.Print(">> API Validation Sync Matrix Online!"));
        }

        private void OnCommand(string command, string args)
        {
            isUiOpen = !isUiOpen;
        }

        private void BuildStaticWorldMappingCache()
        {
            try
            {
                var worldSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>();
                if (worldSheet == null) return;

                foreach (var row in worldSheet)
                {
                    string wName = row.Name.ToString();
                    if (string.IsNullOrEmpty(wName)) continue;

                    if (row.DataCenter.RowId > 0)
                    {
                        var dcRow = row.DataCenter.Value;
                        string dcName = dcRow.Name.ToString();
                        if (!string.IsNullOrEmpty(dcName))
                        {
                            worldToDcMap[wName] = dcName;
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Error($"Failed to compile world mapping lookups: {ex.Message}"); }
        }

        private void LoadHistoryFromFile()
        {
            try
            {
                if (!File.Exists(saveFilePath)) return;

                string jsonContent = File.ReadAllText(saveFilePath);
                var deserialized = JsonSerializer.Deserialize<List<SnipedItemRecord>>(jsonContent);
                if (deserialized != null)
                {
                    lock (scannedHistoryList)
                    {
                        scannedHistoryList = deserialized.OrderByDescending(x => x.LastScannedDateTime).ToList();
                    }
                    Log.Information($"Successfully reloaded {scannedHistoryList.Count} cached item records.");
                }
            }
            catch (Exception ex) { Log.Error($"Error loading persistent file cache: {ex.Message}"); }
        }

        private void SaveHistoryToFile()
        {
            try
            {
                string dir = PluginInterface.GetPluginConfigDirectory();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                lock (scannedHistoryList)
                {
                    string jsonContent = JsonSerializer.Serialize(scannedHistoryList, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(saveFilePath, jsonContent);
                }
            }
            catch (Exception ex) { Log.Debug($"Handled storage writing sweep smoothly: {ex.Message}"); }
        }

        private void OpenCacheDirectoryInExplorer()
        {
            try
            {
                string dir = PluginInterface.GetPluginConfigDirectory();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to open local data storage directory: {ex.Message}");
            }
        }

        private void UpdateLocationDetailsFromClient()
        {
            try
            {
                if (ClientState == null) return;

                var worldIdProp = ClientState.GetType().GetProperty("WorldId");
                if (worldIdProp != null)
                {
                    ushort wId = Convert.ToUInt16(worldIdProp.GetValue(ClientState));
                    if (wId > 0 && ResolveWorldAndDcFromId(wId)) return;
                }

                var localPlayerProp = ClientState.GetType().GetProperty("LocalPlayer");
                if (localPlayerProp != null)
                {
                    var playerObj = localPlayerProp.GetValue(ClientState);
                    if (playerObj != null)
                    {
                        var homeWorldProp = playerObj.GetType().GetProperty("HomeWorld");
                        if (homeWorldProp != null)
                        {
                            var worldValueObj = homeWorldProp.GetValue(playerObj);
                            if (worldValueObj != null)
                            {
                                var idProp = worldValueObj.GetType().GetProperty("RowId");
                                if (idProp != null)
                                {
                                    ushort wId = Convert.ToUInt16(idProp.GetValue(worldValueObj));
                                    if (wId > 0 && ResolveWorldAndDcFromId(wId)) return;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Debug($"Runtime location lookup handled smoothly: {ex.Message}"); }
        }

        private bool ResolveWorldAndDcFromId(ushort worldId)
        {
            try
            {
                var worldSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>();
                if (worldSheet != null)
                {
                    var worldRow = worldSheet.GetRow(worldId);
                    string detectedWorld = worldRow.Name.ToString();
                    
                    if (!string.IsNullOrEmpty(detectedWorld))
                    {
                        myManualHomeWorld = detectedWorld;
                        
                        if (worldRow.DataCenter.RowId > 0)
                        {
                            var dcRow = worldRow.DataCenter.Value;
                            string detectedDc = dcRow.Name.ToString();
                            if (!string.IsNullOrEmpty(detectedDc))
                            {
                                myAutomatedDataCenter = detectedDc;
                                
                                foreach (var pair in regionalDcGroups)
                                {
                                    if (pair.Value.Contains(detectedDc, StringComparer.OrdinalIgnoreCase))
                                    {
                                        myCurrentRegionGroup = pair.Key;
                                        break;
                                    }
                                }
                            }
                        }
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private void OnOfferingsReceived(IMarketBoardCurrentOfferings currentOfferings)
        {
            try
            {
                if (currentOfferings == null) return;
                var listings = currentOfferings.ItemListings;
                if (listings == null || listings.Count == 0) return;

                uint itemId = (uint)listings[0].ItemId;
                if (itemId == 0) return;

                lock (debounceLock)
                {
                    if (lastScannedTimes.TryGetValue(itemId, out var lastScanTime))
                    {
                        if ((DateTime.Now - lastScanTime).TotalSeconds < 1.5) return; 
                    }
                    lastScannedTimes[itemId] = DateTime.Now;
                }

                UpdateLocationDetailsFromClient();
                dynamicTargetItemId = itemId;

                string targetDcName = myAutomatedDataCenter;
                string itemName = GetItemNameString(dynamicTargetItemId);
                int activeMarginThreshold = this.snipeDiscountPercentage;

                _ = Task.Run(async () => 
                {
                    try { await FetchComprehensiveDcPrices(itemId, itemName, targetDcName, activeMarginThreshold); }
                    catch (Exception ex) { Log.Error($"Error inside background task processing: {ex}"); }
                });
            }
            catch (Exception ex) { Log.Error($"CRITICAL: OnOfferingsReceived crashed: {ex.Message}"); }
        }

        private async Task FetchComprehensiveDcPrices(uint itemId, string itemName, string dataCenterName, int activeMargin)
        {
            try
            {
                string url = $"https://universalis.app/api/v2/{myCurrentRegionGroup}/{itemId}?entries=99";
                string responseJson = await httpClient.GetStringAsync(url);
                
                using JsonDocument doc = JsonDocument.Parse(responseJson);
                JsonElement root = doc.RootElement;

                int localCheapestPrice = int.MaxValue;
                int dcLowestPrice = int.MaxValue;
                string dcCheapestWorld = "Other Worlds";

                int regionLowestPrice = int.MaxValue;
                string regionCheapestWorld = "Other Region Worlds";

                if (root.TryGetProperty("listings", out var listingsProp) && listingsProp.ValueKind == JsonValueKind.Array)
                {
                    var allListings = listingsProp.EnumerateArray().ToList();

                    var localHomeWorldRow = allListings
                        .Where(l => {
                            if (!l.TryGetProperty("pricePerUnit", out var p) || p.GetInt32() <= 0) return false;
                            string wName = l.TryGetProperty("worldName", out var w) ? w.GetString() ?? "" : "";
                            return string.Equals(wName, myManualHomeWorld, StringComparison.OrdinalIgnoreCase);
                        })
                        .OrderBy(l => l.GetProperty("pricePerUnit").GetInt32())
                        .FirstOrDefault();

                    if (localHomeWorldRow.ValueKind != JsonValueKind.Undefined)
                    {
                        localCheapestPrice = localHomeWorldRow.GetProperty("pricePerUnit").GetInt32();
                    }

                    var localDcListingsRow = allListings
                        .Where(l => {
                            if (!l.TryGetProperty("pricePerUnit", out var p) || p.GetInt32() <= 0) return false;
                            string wName = l.TryGetProperty("worldName", out var w) ? w.GetString() ?? "" : "";
                            
                            if (worldToDcMap.TryGetValue(wName, out var actualDc))
                            {
                                return string.Equals(actualDc, myAutomatedDataCenter, StringComparison.OrdinalIgnoreCase);
                            }
                            return false;
                        })
                        .OrderBy(l => l.GetProperty("pricePerUnit").GetInt32())
                        .FirstOrDefault();

                    if (localDcListingsRow.ValueKind != JsonValueKind.Undefined)
                    {
                        dcLowestPrice = localDcListingsRow.GetProperty("pricePerUnit").GetInt32();
                        dcCheapestWorld = localDcListingsRow.TryGetProperty("worldName", out var w) ? w.GetString() ?? "Unknown" : "Unknown";
                    }

                    if (regionalDcGroups.TryGetValue(myCurrentRegionGroup, out var allowedDcs))
                    {
                        var globalCheapestRow = allListings
                            .Where(l => {
                                if (!l.TryGetProperty("pricePerUnit", out var p) || p.GetInt32() <= 0) return false;
                                string wName = l.TryGetProperty("worldName", out var w) ? w.GetString() ?? "" : "";
                                
                                if (worldToDcMap.TryGetValue(wName, out var targetDc))
                                {
                                    return allowedDcs.Contains(targetDc, StringComparer.OrdinalIgnoreCase);
                                }
                                return false; 
                            })
                            .OrderBy(l => l.GetProperty("pricePerUnit").GetInt32())
                            .FirstOrDefault();

                        if (globalCheapestRow.ValueKind != JsonValueKind.Undefined)
                        {
                            regionLowestPrice = globalCheapestRow.GetProperty("pricePerUnit").GetInt32();
                            regionCheapestWorld = globalCheapestRow.TryGetProperty("worldName", out var wNameProp) ? wNameProp.GetString() ?? "Unknown" : "Unknown";
                        }
                    }
                }

                if (localCheapestPrice == int.MaxValue) localCheapestPrice = 0;
                if (dcLowestPrice == int.MaxValue) { dcLowestPrice = localCheapestPrice; dcCheapestWorld = myManualHomeWorld; }
                if (regionLowestPrice == int.MaxValue) { regionLowestPrice = localCheapestPrice; regionCheapestWorld = myManualHomeWorld; }

                double discountMultiplier = (100 - activeMargin) / 100.0;
                bool isUiDeal = localCheapestPrice > 0 && dcLowestPrice <= (localCheapestPrice * discountMultiplier) && !string.Equals(dcCheapestWorld, myManualHomeWorld, StringComparison.OrdinalIgnoreCase);
                int regionalSavingsDifference = localCheapestPrice - dcLowestPrice;

                var newRecord = new SnipedItemRecord
                {
                    ItemId = itemId,
                    ItemName = itemName,
                    LocalLowPrice = localCheapestPrice,
                    DcLowestPrice = dcLowestPrice,
                    DcCheapestWorld = dcCheapestWorld,
                    RegionLowestPrice = regionLowestPrice,
                    RegionCheapestWorld = regionCheapestWorld,
                    PotentialProfit = regionalSavingsDifference,
                    TimeString = DateTime.Now.ToString("HH:mm:ss"),
                    IsExcellentDeal = isUiDeal,
                    LastScannedDateTime = DateTime.Now
                };

                lock (scannedHistoryList)
                {
                    scannedHistoryList.RemoveAll(x => x.ItemId == itemId);
                    scannedHistoryList.Insert(0, newRecord); 
                }

                SaveHistoryToFile();

                Framework.RunOnFrameworkThread(() =>
                {
                    var masterBuilder = new SeStringBuilder();

                    int absoluteCheapestPrice = regionLowestPrice;
                    string absoluteCheapestWorld = regionCheapestWorld;

                    if (dcLowestPrice < absoluteCheapestPrice && dcLowestPrice > 0)
                    {
                        absoluteCheapestPrice = dcLowestPrice;
                        absoluteCheapestWorld = dcCheapestWorld;
                    }

                    if (localCheapestPrice <= absoluteCheapestPrice || string.Equals(absoluteCheapestWorld, myManualHomeWorld, StringComparison.OrdinalIgnoreCase))
                    {
                        masterBuilder.AddUiForeground(43) // Safe Green
                                     .AddText($"[MARKET SNIPER] {itemName} is cheapest on your home world! ({localCheapestPrice:N0} Gil)")
                                     .AddUiForegroundOff();
                    }
                    else
                    {
                        int gilSavings = localCheapestPrice - absoluteCheapestPrice;
                        
                        bool isCrossDataCenter = false;
                        if (worldToDcMap.TryGetValue(absoluteCheapestWorld, out var destinationDc))
                        {
                            isCrossDataCenter = !string.Equals(destinationDc, myAutomatedDataCenter, StringComparison.OrdinalIgnoreCase);
                        }

                        if (isCrossDataCenter)
                        {
                            // MEGA DEAL (Cross Data Center): Forced sharp Red using UI token 17
                            masterBuilder.AddUiForeground(17) 
                                         .AddText($"[MARKET SNIPER] [MEGA DEAL] Go to {absoluteCheapestWorld} ({destinationDc}) for lowest! Price: {absoluteCheapestPrice:N0} Gil (Saves {gilSavings:N0} Gil over {myManualHomeWorld})")
                                         .AddUiForegroundOff();
                        }
                        else
                        {
                            // Local Data Center Deal: Forced clean, vibrant Yellow using UI token 63
                            masterBuilder.AddUiForeground(63) 
                                         .AddText($"[MARKET SNIPER] Go to {absoluteCheapestWorld} for lowest! Price: {absoluteCheapestPrice:N0} Gil (Saves {gilSavings:N0} Gil over {myManualHomeWorld})")
                                         .AddUiForegroundOff();
                        }
                    }

                    Chat.Print(new XivChatEntry { Type = XivChatType.SystemMessage, Message = masterBuilder.Build() });
                });
            }
            catch (Exception ex) { Log.Error($"API Parse Error: {ex.Message}"); }
        }

        private string GetItemNameString(uint id)
        {
            if (id == 0) return "None (Scan an item on Marketboard)";
            if (id == 5674) return "Savage Might Materia I";
            if (id == 5675) return "Savage Might Materia II";
            if (id == 5676) return "Savage Might Materia III";
            if (id == 5677) return "Savage Might Materia IV";
            if (id == 5678) return "Savage Might Materia V";

            try
            {
                var sheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
                if (sheet != null)
                {
                    var row = sheet.GetRow(id);
                    return row.Name.ToString();
                }
            }
            catch { }
            return $"Item #{id}";
        }

        private void DrawUi()
        {
            if (!isUiOpen) return;

            ImGui.SetNextWindowSize(new Vector2(900, 480), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(900, 380), new Vector2(1600, 1000));

            if (ImGui.Begin("Marketboard Sniper - Master Command Console", ref isUiOpen))
            {
                UpdateLocationDetailsFromClient();

                // ------------------ LINE 1: ITEM TRACKING & REGIONAL INFO ------------------
                ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.2f, 1.0f), "AUTOMATED TARGET SCANNER:");
                ImGui.SameLine();
                ImGui.TextUnformatted($"Tracking Item: {GetItemNameString(dynamicTargetItemId)}");

                string locationMetadataText = $"Home: {myManualHomeWorld} | DC: {myAutomatedDataCenter}";
                float metadataWidth = ImGui.CalcTextSize(locationMetadataText).X;
                float currentAvailX = ImGui.GetContentRegionAvail().X;
                
                if (currentAvailX > metadataWidth + 20f)
                {
                    ImGui.SameLine(ImGui.GetWindowWidth() - metadataWidth - 20f);
                }
                else
                {
                    ImGui.SameLine();
                }
                ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), locationMetadataText);

                // ------------------ LINE 2: CONTROL BUTTONS ------------------
                ImGui.Spacing();
                
                float fullAvailableWidth = ImGui.GetContentRegionAvail().X;
                float directoryButtonWidth = 150f;
                float leftSideScanButtonWidth = Math.Max(200f, fullAvailableWidth - directoryButtonWidth - 15f);

                if (dynamicTargetItemId > 0)
                {
                    if (ImGui.Button("Check Current Prices Across Region", new Vector2(leftSideScanButtonWidth, 0)))
                    {
                        string targetDcName = myAutomatedDataCenter;
                        string itemName = GetItemNameString(dynamicTargetItemId);
                        int activeMarginThreshold = this.snipeDiscountPercentage;

                        _ = Task.Run(async () => 
                        {
                            try { await FetchComprehensiveDcPrices(dynamicTargetItemId, itemName, targetDcName, activeMarginThreshold); }
                            catch (Exception ex) { Log.Error($"Manual UI refresh query failed: {ex.Message}"); }
                        });
                    }
                }
                else
                {
                    ImGui.BeginDisabled();
                    ImGui.Button("Select Marketboard Target To Scan", new Vector2(leftSideScanButtonWidth, 0));
                    ImGui.EndDisabled();
                }

                ImGui.SameLine();
                
                if (ImGui.Button("Open Cache Folder", new Vector2(directoryButtonWidth, 0)))
                {
                    OpenCacheDirectoryInExplorer();
                }

                ImGui.Separator();

                // ------------------ LINE 3: DC TRAVEL PRIVILEGES ------------------
                if (regionalDcGroups.TryGetValue(myCurrentRegionGroup, out var list))
                {
                    string allowedListString = string.Join(", ", list);
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), $"Your regional DC Travel limits: [{allowedListString}]");
                }

                ImGui.Text("Configure Sniper Discount Alert Trigger Threshold:");
                ImGui.SameLine();
                
                float remainingSliderWidth = Math.Max(180f, ImGui.GetContentRegionAvail().X - 15f);
                ImGui.SetNextItemWidth(remainingSliderWidth);
                ImGui.SliderInt("##uiDiscountSlider", ref snipeDiscountPercentage, 5, 95, "%d%% margin savings");
                
                ImGui.Separator();

                // ------------------ LIVE SEARCH BAR FILTER ------------------
                ImGui.TextColored(new Vector4(0.4f, 0.7f, 1.0f, 1.0f), "Search Scanned History Logs:");
                ImGui.SameLine();
                
                float searchInputWidth = Math.Max(250f, ImGui.GetContentRegionAvail().X - 45f);
                ImGui.SetNextItemWidth(searchInputWidth);
                
                ImGui.InputTextWithHint("##historyLogSearchInput", "Type an item name to filter history...", ref logSearchQuery, 128);
                
                ImGui.SameLine();
                if (ImGui.Button("X##ClearSearchHistoryLog"))
                {
                    logSearchQuery = string.Empty;
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clear search filters.");

                // ------------------ DATA HISTORY LOG TABLE ------------------
                if (ImGui.BeginTable("SniperDcMasterHistoryTable", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, 0)))
                {
                    ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 95f); 
                    ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 65f);
                    ImGui.TableSetupColumn("Scanned Item Name", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Your World", ImGuiTableColumnFlags.WidthFixed, 100f);
                    ImGui.TableSetupColumn("DC Lowest", ImGuiTableColumnFlags.WidthFixed, 100f);
                    ImGui.TableSetupColumn("DC World", ImGuiTableColumnFlags.WidthFixed, 100f);
                    ImGui.TableSetupColumn("Region Low", ImGuiTableColumnFlags.WidthFixed, 100f);
                    ImGui.TableSetupColumn("Region World", ImGuiTableColumnFlags.WidthFixed, 100f);
                    ImGui.TableHeadersRow();

                    uint itemToRemoveThisTick = 0;

                    lock (scannedHistoryList)
                    {
                        foreach (var record in scannedHistoryList)
                        {
                            if (!string.IsNullOrEmpty(logSearchQuery) && 
                                record.ItemName.IndexOf(logSearchQuery, StringComparison.OrdinalIgnoreCase) == -1)
                            {
                                continue;
                            }

                            ImGui.TableNextRow();
                            ImGui.TableSetColumnIndex(0);
                            
                            if (ImGui.Button($"[R]##Refresh_{record.ItemId}"))
                            {
                                string targetDcName = myAutomatedDataCenter;
                                int activeMarginThreshold = this.snipeDiscountPercentage;
                                dynamicTargetItemId = record.ItemId;

                                _ = Task.Run(async () =>
                                {
                                    try { await FetchComprehensiveDcPrices(record.ItemId, record.ItemName, targetDcName, activeMarginThreshold); }
                                    catch (Exception ex) { Log.Error($"Row update failure: {ex.Message}"); }
                                });
                            }

                            ImGui.SameLine();
                            if (ImGui.Button($"[X]##Delete_{record.ItemId}"))
                            {
                                itemToRemoveThisTick = record.ItemId;
                            }

                            ImGui.TableSetColumnIndex(1);
                            ImGui.Text(record.TimeString);

                            ImGui.TableSetColumnIndex(2);
                            if (ImGui.Selectable($"{record.ItemName}##SelectRowItem_{record.ItemId}"))
                            {
                                dynamicTargetItemId = record.ItemId;
                            }
                            if (ImGui.IsItemHovered())
                            {
                                ImGui.SetTooltip("Left-click to track item. Right-click to view clipboard options.");
                            }

                            if (ImGui.BeginPopupContextItem($"ItemContextMenu_##{record.ItemId}"))
                            {
                                if (ImGui.Selectable($"Copy Item Name to Clipboard##Action_{record.ItemId}"))
                                {
                                    ImGui.SetClipboardText(record.ItemName);
                                    
                                    Framework.RunOnFrameworkThread(() => {
                                        var copyNotice = new SeStringBuilder()
                                            .AddUiForeground(43)
                                            .AddText($"[MARKET SNIPER] Copied to clipboard: \"{record.ItemName}\"")
                                            .AddUiForegroundOff()
                                            .Build();
                                        Chat.Print(new XivChatEntry { Type = XivChatType.SystemMessage, Message = copyNotice });
                                    });
                                }
                                ImGui.EndPopup();
                            }

                            ImGui.TableSetColumnIndex(3);
                            ImGui.Text($"{record.LocalLowPrice:N0}");

                            ImGui.TableSetColumnIndex(4);
                            ImGui.Text($"{record.DcLowestPrice:N0}");

                            ImGui.TableSetColumnIndex(5);
                            if (string.Equals(record.DcCheapestWorld, myManualHomeWorld, StringComparison.OrdinalIgnoreCase))
                                ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.2f, 1.0f), record.DcCheapestWorld);
                            else
                                ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.4f, 1.0f), record.DcCheapestWorld);

                            ImGui.TableSetColumnIndex(6);
                            ImGui.Text($"{record.RegionLowestPrice:N0}");

                            ImGui.TableSetColumnIndex(7);
                            if (string.Equals(record.RegionCheapestWorld, myManualHomeWorld, StringComparison.OrdinalIgnoreCase))
                                ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.2f, 1.0f), record.RegionCheapestWorld);
                            else
                                ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), record.RegionCheapestWorld);
                        }

                        if (itemToRemoveThisTick > 0)
                        {
                            scannedHistoryList.RemoveAll(x => x.ItemId == itemToRemoveThisTick);
                            if (dynamicTargetItemId == itemToRemoveThisTick) dynamicTargetItemId = 0;
                            SaveHistoryToFile();
                        }
                    }
                    ImGui.EndTable();
                }
            }
            ImGui.End();
        }

        public void Dispose()
        {
            SaveHistoryToFile();
            MarketBoard.OfferingsReceived -= OnOfferingsReceived;
            CommandManager.RemoveHandler("/msniper");
            httpClient.Dispose();
        }
    }
}