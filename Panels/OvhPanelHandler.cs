using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using nocscienceat.XPlanePanel;
using nocscienceat.XPlanePanel.Services;
using nocscienceat.XPlaneWebConnector.Interfaces;
using System.Collections.Concurrent;


namespace nocscienceat.JavaSimulatorOVH.Panels;

/// <summary>
/// OVH (Overhead Panel) handler - manages communication between hardware and X-Plane.
/// </summary>
public partial class OvhPanelHandler : JavaSimulatorPanelHandlerBase
{
    private readonly Dictionary<string, Func<int, Task>> _commandHandlers;
    private readonly ConcurrentDictionary<string, bool> _ledStateCache = new();
    private readonly ConcurrentDictionary<string, string> _hardwareValueCache = new();

    // Cached state values for toggle logic
    // No volatile/lock needed — all access is from the single panel work task
    private int _crewOxySwitch;       // Crew oxygen switch state
    private int _flap3Mode;           // FLAP3 mode state
    private int _xFeedValve;          // X-Feed valve state
    private int _adirs1Ir;            // ADIRS 1 IR switch state
    private int _adirs3Ir;            // ADIRS 3 IR switch state
    private int _adirs2Ir;            // ADIRS 2 IR switch state
    private float _panelBrightness;     // Panel brightness (cached to avoid redundant sends)
    private int _lightDomeHandshake = -1;    // Written by connector thread, read by connect flow (Volatile access)
    private readonly HashSet<string> _initReceivedCommands = [];  // T/R commands received during init
    private bool _initSyncComplete;                               // Stops tracking after sync is done

    public override string PanelName => "OVH";

    public OvhPanelHandler(IXPlaneWebConnector connector, IConfiguration configuration, ILogger<OvhPanelHandler> logger,
        IDataRefCommandProvider? overrideProvider = null)
        : base(connector, configuration, logger, overrideProvider)
    {
        // Build command dispatch table 
        _commandHandlers = new Dictionary<string, Func<int, Task>>
        {
            // Korry Buttons K01-K23
            ["K01"] = HandleK01_ApuStart,   ["K02"] = HandleK02_ApuMaster,
            ["K03"] = HandleK03_AntiIceWing,["K04"] = HandleK04_AntiIceEng1,
            ["K05"] = HandleK05_AntiIceEng2,["K06"] = HandleK06_CrewOxy,
            ["K07"] = HandleK07_Pack1,      ["K08"] = HandleK08_ApuBleed,
            ["K09"] = HandleK09_Pack2,
            ["K10"] = HandleK10_Adirs1,     ["K11"] = HandleK11_Adirs3,
            ["K12"] = HandleK12_Adirs2,     ["K13"] = HandleK13_Flap3,
            ["K14"] = HandleK14_Bat1,       ["K15"] = HandleK15_Bat2,
            ["K16"] = HandleK16_ExtPwr,
            ["K17"] = HandleK17_FuelPumpLTk1, ["K18"] = HandleK18_FuelPumpLTk2,
            ["K19"] = HandleK19_FuelPumpCtrL, ["K20"] = HandleK20_XFeed,
            ["K21"] = HandleK21_FuelPumpCtrR, ["K22"] = HandleK22_FuelPumpRTk1,
            ["K23"] = HandleK23_FuelPumpRTk2,
            
            // Toggle Switches T01-T12
            ["T01"] = HandleT01_Strobe, ["T02"] = HandleT02_Beacon,
            ["T03"] = HandleT03_Wing,   ["T04"] = HandleT04_Nav,
            ["T05"] = HandleT05_Rwy,    ["T06"] = HandleT06_LandL,
            ["T07"] = HandleT07_LandR,  ["T08"] = HandleT08_Nose,
            ["T09"] = HandleT09_Seat,   ["T10"] = HandleT10_NoSmoke,
            ["T11"] = HandleT11_Exit,   ["T12"] = HandleT12_Dome,
            
            // Rotary Knobs R01-R04
            ["R01"] = HandleR01_Nav1, ["R02"] = HandleR02_Nav3,
            ["R03"] = HandleR03_Nav2, ["R04"] = HandleR04_Wiper,
            
            // Test Buttons B01-B04
            ["B01"] = HandleB01_TestEng1, ["B02"] = HandleB02_TestApu,
            ["B03"] = HandleB03_TestEng2, 
            
            // Call All
            ["B04"] = HandleB04_CallAll,
            
            // Panel Brightness
            ["OVD"] = HandleOVHD_Brightness,
            
            // Handshake responses (empty handlers - just logging)
            ["VER"] = d => { _logger.LogInformation("## FW version {Value}", VersionString(d)); return Task.CompletedTask; },
            ["PCB"] = d => { _logger.LogInformation("## PCB Version: {Value}", VersionString(d)); return Task.CompletedTask; },

            ["PNLV"] = d => { _logger.LogDebug("Panel light: {Value}", d / 10.0); return Task.CompletedTask; }
        };
    }


    protected override async Task OnConnectedAsync(CancellationToken cancellationToken)
    {
        // Subscribe to LightDome for round-trip handshake verification.
        // The callback fires on a connector thread; OnConnectedAsync runs on the connect
        // flow, so we use Volatile for thread-safe access to _lightDomeHandshake.
        
        IDisposable handshakeSub = await _connector.SubscribeAsync(
            GetDataRefPath("LightDome"),
            (int value) => Volatile.Write(ref _lightDomeHandshake, value));

        try
        {
            string lightDomePath = GetDataRefPath("LightDome");
            bool handshakeComplete = false;

            while (!handshakeComplete)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Set DIM (1)
                await _connector.SetDataRefValueAsync(lightDomePath, 1);
                await Task.Delay(500, cancellationToken);
                bool dimConfirmed = Volatile.Read(ref _lightDomeHandshake) == 1;

                await Task.Delay(500, cancellationToken);

                // Set OFF (0)
                await _connector.SetDataRefValueAsync(lightDomePath, 0);
                await Task.Delay(500, cancellationToken);
                bool offConfirmed = Volatile.Read(ref _lightDomeHandshake) == 0;

                await Task.Delay(500, cancellationToken);

                handshakeComplete = dimConfirmed && offConfirmed;

                if (!handshakeComplete)
                    _logger.LogDebug("OVH: LightDome handshake cycle incomplete (dim={Dim}, off={Off}), retrying ...",
                        dimConfirmed, offConfirmed);
            }

            _logger.LogInformation("OVH: LightDome round-trip handshake verified");
        }
        finally
        {
            handshakeSub.Dispose();
        }
        
        await base.OnConnectedAsync(cancellationToken);

        // After serial init the hardware reports non-zero toggle/rotary positions.
        // Schedule a delayed sync to send value 0 for any unreported switches,
        // keeping X-Plane in sync with the actual hardware state.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000, cancellationToken);
                EnqueueWork(SyncUnreportedSwitchStatesAsync);
            }
            catch (OperationCanceledException) { }
        }, cancellationToken);
    }

    /// <summary>
    /// Subscribes to all OVH-related datarefs.
    /// </summary>
    protected override async Task SubscribeToDataRefsAsync(CancellationToken cancellationToken)
    {
       
     
        SendToHardware("PCB");
        await Task.Delay(500, cancellationToken);
        SendToHardware("VER");
        await Task.Delay(500, cancellationToken);
        SendToHardware("CHKD");
        await Task.Delay(2000, cancellationToken);


        // ========================================================================
        // BATTERY VOLTAGES - Displayed on hardware 7-segment displays
        // ========================================================================

        await _connector.SubscribeAsync(GetDataRefPath("BAT1_V"), (float value) =>
        {

            string formatted = (value * 10f).ToString("#.");
            if (HasValueChanged("BT1", formatted))
            {
                SendToHardware("BT1", formatted);
            }
        });
        await _connector.SubscribeAsync(GetDataRefPath("BAT2_V"), (float value) =>
        {
            string formatted = (value * 10f).ToString("#.");
            if (HasValueChanged("BT2", formatted))
            {
                SendToHardware("BT2", formatted);
            }
        });

        // ========================================================================
        // KORRY BUTTON ANNUNCIATOR LEDS (K1-K23)
        // ========================================================================

        // K1: APU START
        await SubscribeLedAsync("ApuStart_U", "K_U1");
        await SubscribeLedAsync("ApuStart_L", "K_L1");
        // K2: APU MASTER
        await SubscribeLedAsync("ApuMaster_U", "K_U2");
        await SubscribeLedAsync("ApuMaster_L", "K_L2");
        // K3: WING ANTI-ICE
        await SubscribeLedAsync("AntiIceWing_U", "K_U3");
        await SubscribeLedAsync("AntiIceWing_L", "K_L3");
        // K4: ENG1 ANTI-ICE
        await SubscribeLedAsync("AntiIceEng1_U", "K_U4");
        await SubscribeLedAsync("AntiIceEng1_L", "K_L4");
        // K5: ENG2 ANTI-ICE
        await SubscribeLedAsync("AntiIceEng2_U", "K_U5");
        await SubscribeLedAsync("AntiIceEng2_L", "K_L5");

        // K6: CREW OXYGEN (special: CrewOxy_S is switch state, not LED)
        await SubscribeEnqueuedAsync(GetDataRefPath("CrewOxy_S"), (value) => _crewOxySwitch = value);
        await SubscribeEnqueuedAsync(GetDataRefPath("CrewOxy_L"), (float value) =>
            {
                UpdateLed("K_L6", value);
                if (value > 0.01f && _crewOxySwitch == 1 ) _crewOxySwitch = 0;
                else if (value <= 0.01f && _crewOxySwitch == 0) _crewOxySwitch = 1;
            });
        
        // K7: PACK 1
        await SubscribeLedAsync("Pack1_U", "K_U7");
        await SubscribeLedAsync("Pack1_L", "K_L7");
        // K8: APU BLEED
        await SubscribeLedAsync("ApuBleed_U", "K_U8");
        await SubscribeLedAsync("ApuBleed_L", "K_L8");
        // K9: PACK 2
        await SubscribeLedAsync("Pack2_U", "K_U9");
        await SubscribeLedAsync("Pack2_L", "K_L9");
        // K10: ADIRS 1
        await SubscribeLedAsync("Adirs1_U", "K_U10");
        await SubscribeLedAsync("Adirs1_L", "K_L10");
        await SubscribeEnqueuedAsync(GetDataRefPath("IR1_Switch"), (int value) => _adirs1Ir = value == 1 ? 1 : 0);
        // K11: ADIRS 3
        await SubscribeLedAsync("Adirs3_U", "K_U11");
        await SubscribeLedAsync("Adirs3_L", "K_L11");
        await SubscribeEnqueuedAsync(GetDataRefPath("IR3_Switch"), (int value) => _adirs3Ir = value == 1 ? 1 : 0);
        // K12: ADIRS 2
        await SubscribeLedAsync("Adirs2_U", "K_U12");
        await SubscribeLedAsync("Adirs2_L", "K_L12");
        await SubscribeEnqueuedAsync(GetDataRefPath("IR2_Switch"), (int value) => _adirs2Ir = value == 1 ? 1 : 0);
        // K13: FLAP3 MODE (needs state tracking for toggle)
        await SubscribeLedAsync("Flap3_U", "K_U13");
        await SubscribeEnqueuedAsync(GetDataRefPath("Flap3_L"), (float value) =>
            {
                _logger.LogDebug("Flap3_L: {flap3_l}", value);
                _flap3Mode = value > 0.01f ? 1 : 0;
                UpdateLed("K_L13", value);
            });

        //for test purposes enqueue this Led through the command handler to verify that enqueued updates work correctly
        // K14: BAT 1
        await SubscribeLedEnqueuedAsync("Bat1_U", "K_U14");
        await SubscribeLedEnqueuedAsync("Bat1_L", "K_L14");
        // K15: BAT 2
        await SubscribeLedAsync("Bat2_U", "K_U15");
        await SubscribeLedAsync("Bat2_L", "K_L15");
        // K16: EXT PWR
        await SubscribeLedAsync("ExtPwr_U", "K_U16");
        await SubscribeLedAsync("ExtPwr_L", "K_L16");
        // K17: L TK PUMP 1
        await SubscribeLedAsync("FuelPumpLTk1_U", "K_U17");
        await SubscribeLedAsync("FuelPumpLTk1_L", "K_L17");
        // K18: L TK PUMP 2
        await SubscribeLedAsync("FuelPumpLTk2_U", "K_U18");
        await SubscribeLedAsync("FuelPumpLTk2_L", "K_L18");
        // K19: CTR L
        await SubscribeLedAsync("FuelPumpCtrL_U", "K_U19");
        await SubscribeLedAsync("FuelPumpCtrL_L", "K_L19");
        // K20: X FEED (needs state tracking for toggle)
        await SubscribeLedAsync("XFeed_U", "K_U20");
        await SubscribeEnqueuedAsync(GetDataRefPath("XFeed_L"), (float value) =>
            {
                _xFeedValve = value > 0.01f ? 1 : 0;
                UpdateLed("K_L20", value);
            });
        // K21: CTR R
        await SubscribeLedAsync("FuelPumpCtrR_U", "K_U21");
        await SubscribeLedAsync("FuelPumpCtrR_L", "K_L21");
        // K22: R TK PUMP 1
        await SubscribeLedAsync("FuelPumpRTk1_U", "K_U22");
        await SubscribeLedAsync("FuelPumpRTk1_L", "K_L22");
        // K23: R TK PUMP 2
        await SubscribeLedAsync("FuelPumpRTk2_U", "K_U23");
        await SubscribeLedAsync("FuelPumpRTk2_L", "K_L23");

        // ========================================================================
        // PANEL INDICATORS
        // ========================================================================

        // onBAT: ADIRS running on battery power
        await _connector.SubscribeAsync(GetDataRefPath("onBAT"), (int value) =>
        {
            string val = value == 1  ? "1" : "0";
            if (HasValueChanged("BTT", val))
                SendToHardware("BTT", val);
        });
        // OVD: Panel brightness level (with change detection)
        await SubscribeEnqueuedAsync(GetDataRefPath("OvhdBrightness"), (float value) =>
            {
                if (value == 0)
                {
                    SendToHardware("PNBL", (value * 10f).ToString("."));
                }

                if (_panelBrightness == 0 && value > 0)
                {
                    SendToHardware("PNBL", (0.3 * 10f).ToString("."));
                }

                _panelBrightness = value;
            });

        _logger.LogInformation("OVH: Subscribed to datarefs");


        // ========================================================================
        // CACHE WARM-UP: pre-resolve write-only datarefs and all commands so the
        // first button press / toggle switch does not incur a REST round-trip.
        // Subscribed datarefs are already cached by SubscribeAsync above.
        // ========================================================================

        var warmUpTasks = new List<Task>();

        // Write-only datarefs (used in SetDataRefValueAsync but never subscribed)
        string[] writeOnlyDataRefKeys =
        [
            "Flap3Mode", "XFeedValve",
            "LightStrobe", "LightBeacon", "LightWing", "LightNav",
            "LightRwy", "LightLandL", "LightLandR", "LightNose",
            "LightSeat", "LightNoSmoke", "LightExit", "LightDome",
            "AdirsNav1", "AdirsNav3", "AdirsNav2",
            "Wiper", "OhpBrightness"
        ];

        foreach (var key in writeOnlyDataRefKeys)
        {
            if (TryGetDataRefPath(key, out var drPath))
                warmUpTasks.Add(_connector.PreResolveDataRefAsync(drPath));
        }

        // All commands (none are pre-resolved by subscriptions)
        string[] commandKeys =
        [
            "ApuStart", "ApuMaster",
            "AntiIceWing", "AntiIceEng1", "AntiIceEng2",
            "CrewOxy", "Pack1", "ApuBleed", "Pack2",
            "Bat1", "Bat2", "ExtPwr",
            "FuelPumpLTk1", "FuelPumpLTk2", "FuelPumpCtrL",
            "FuelPumpCtrR", "FuelPumpRTk1", "FuelPumpRTk2",
            "TestEng1_Begin", "TestEng1_End",
            "TestApu_Begin", "TestApu_End",
            "TestEng2_Begin", "TestEng2_End",
            "CallAll"
        ];

        foreach (var key in commandKeys)
        {
            if (TryGetCommand(key, out var cmdPath))
                warmUpTasks.Add(_connector.PreResolveCommandAsync(cmdPath));
        }

        await Task.WhenAll(warmUpTasks);
        _logger.LogInformation("OVH: Pre-resolved {Count} dataref/command IDs", warmUpTasks.Count);
    }

    protected override async Task ProcessCommandAsync(string command, string value)
    {
        _logger.LogDebug("Received command from hardware: {Command} with value {Value}", command, value);

        // Track toggle/rotary commands received during initialization for zero-state sync
        if (!_initSyncComplete && command.Length >= 2 && command[0] is 'T' or 'R')
            _initReceivedCommands.Add(command);

        if (_commandHandlers.TryGetValue(command, out Func<int, Task>? handler))
        {
            if (int.TryParse(value, out int numValue))
            {
                await handler(numValue);
            }
        }
        else
        {
            _logger.LogDebug("Unknown OVH command: {Command}", command);
        }
    }

    /// <summary>
    /// Sends value 0 for any toggle/rotary commands not reported by the hardware during init.
    /// Runs on the panel work queue — no concurrency concerns.
    /// </summary>
    private async Task SyncUnreportedSwitchStatesAsync()
    {
        _initSyncComplete = true;

        string[] switchAndRotaryCommands =
        [
            "T01", "T02", "T03", "T04", "T05", "T06", "T07", "T08", "T09", "T10", "T11", "T12", "R01", "R02", "R03", "R04"
        ];

        int synced = 0;
        foreach (var cmd in switchAndRotaryCommands)
        {
            if (!_initReceivedCommands.Contains(cmd) && _commandHandlers.TryGetValue(cmd, out var handler))
            {
                _logger.LogDebug("OVH: Syncing unreported {Cmd} with value 0", cmd);
                await handler(0);
                synced++;
            }
        }

        _logger.LogInformation("OVH: Initial switch state sync complete ({Synced} synced to zero, {Reported} reported by hardware)",
            synced, _initReceivedCommands.Count);
    }

    private Task SubscribeLedAsync(string dataRefKey, string led)
    {
        return _connector.SubscribeAsync(GetDataRefPath(dataRefKey), (float value) => UpdateLed(led, value));
    }

    private Task SubscribeLedEnqueuedAsync(string dataRefKey, string led)
    {
        return SubscribeEnqueuedAsync(GetDataRefPath(dataRefKey), (float value) => UpdateLed(led, value));
    }

    private void UpdateLed(string led, float value)
    {
        bool isOn = value > 0.0;
        bool changed = false;

        _ledStateCache.AddOrUpdate(led, _ => { changed = true; return isOn; }, (_, previous) =>
            {
                if (previous != isOn) changed = true;
                return isOn;
            });

        if (changed)
            SendToHardware(led, isOn ? "1" : "0");
    }

    private bool HasValueChanged(string key, string value)
    {
        bool changed = false;

        _hardwareValueCache.AddOrUpdate(key, _ => { changed = true; return value; }, (_, previous) =>
            {
                if (previous != value) changed = true;
                return value;
            });
        return changed;
    }


    // ========================================================================
    // KORRY BUTTON HANDLERS (K01-K23)
    // ========================================================================

    #region K01-K09: APU, Anti-Ice, Oxygen, Packs

    private async Task HandleK01_ApuStart(int i)
    {
        if (i == 1)
        {
            _logger.LogDebug("K01: APU START pressed");
            await _connector.SendCommandAsync(GetCommand("ApuStart"));
        }
    }

    private async Task HandleK02_ApuMaster(int i)
    {
        if (i == 1)
        {
            _logger.LogDebug("K02: APU MASTER pressed");
            await _connector.SendCommandAsync(GetCommand("ApuMaster"));
        }
    }

    private async Task HandleK03_AntiIceWing(int i)
    {
        if (i == 1)
        {
            _logger.LogDebug("K03: WING ANTI-ICE pressed");
            await _connector.SendCommandAsync(GetCommand("AntiIceWing"));
        }
    }

    private async Task HandleK04_AntiIceEng1(int i)
    {
        if (i == 1)
        {
            _logger.LogDebug("K04: ENG1 ANTI-ICE pressed");
            await _connector.SendCommandAsync(GetCommand("AntiIceEng1"));
        }
    }

    private async Task HandleK05_AntiIceEng2(int i)
    {
        if (i == 1)
        {
            _logger.LogDebug("K05: ENG2 ANTI-ICE pressed");
            await _connector.SendCommandAsync(GetCommand("AntiIceEng2"));
        }
    }

    private async Task HandleK06_CrewOxy(int i)
    {
        _logger.LogDebug("K06: CREW OXYGEN pressed");
        if (i == 1)
        {
            await _connector.SendCommandAsync(GetCommand("CrewOxy"));
            // Toggle local state and sync to X-Plane
            if (_crewOxySwitch == 0.0f)
            {
                await _connector.SetDataRefValueAsync(GetDataRefPath("CrewOxy_S"), 1);
                _crewOxySwitch = 1;
            }
            else
            {
                await _connector.SetDataRefValueAsync(GetDataRefPath("CrewOxy_S"), 0);
                _crewOxySwitch = 0;
            }
        }
    }

    private async Task HandleK07_Pack1(int i)
    {
        if (i == 1)
        {
            _logger.LogDebug("K07: PACK 1 pressed");
            await _connector.SendCommandAsync(GetCommand("Pack1"));
        }
    }

    private async Task HandleK08_ApuBleed(int i)
    {
        if (i == 1)
        {
            _logger.LogDebug("K08: APU BLEED pressed");
            await _connector.SendCommandAsync(GetCommand("ApuBleed"));
        }
    }

    private async Task HandleK09_Pack2(int i)
    {
        if (i == 1)
        {
            _logger.LogDebug("K09: PACK 2 pressed");
            await _connector.SendCommandAsync(GetCommand("Pack2"));
        }
    }

    #endregion

    #region K10-K12: ADIRS 

    private async Task HandleK10_Adirs1(int i)
    {
        if (i == 1)
        {
            _logger.LogDebug("K10: ADIRS 1 pressed");
            await _connector.SetDataRefValueAsync(GetDataRefPath("IR1_Switch"), _adirs1Ir == 1 ? 0 : 1);
        }
    }

    private async Task HandleK11_Adirs3(int i)
    {
        if (i == 1)
        {
            _logger.LogDebug("K11: ADIRS 3 pressed");
            await _connector.SetDataRefValueAsync(GetDataRefPath("IR3_Switch"), _adirs3Ir == 1 ? 0 : 1);
        }
    }

    private async Task HandleK12_Adirs2(int i)
    {
        if (i == 1)
        {
            _logger.LogDebug("K12: ADIRS 2 pressed");
            await _connector.SetDataRefValueAsync(GetDataRefPath("IR2_Switch"), _adirs2Ir == 1 ? 0 : 1);
        }
    }

    #endregion

    #region K13: FLAP3 MODE (toggle via dataref)

    private async Task HandleK13_Flap3(int i)
    {
        if (i == 1)
        {
            _logger.LogDebug("K13: FLAP3 pressed");
            await _connector.SetDataRefValueAsync(GetDataRefPath("Flap3Mode"), _flap3Mode == 1 ? 0 : 1);
        }
    }

    #endregion

    #region K14-K16: Electrical

    private async Task HandleK14_Bat1(int i)
    {
        if (i == 1)
        {
            _logger.LogDebug("K14: BAT1 pressed");
            await _connector.SendCommandAsync(GetCommand("Bat1"));
        }
    }

    private async Task HandleK15_Bat2(int i)
    {
        if (i == 1)
        {
            _logger.LogDebug("K15: BAT2 pressed");
            await _connector.SendCommandAsync(GetCommand("Bat2"));
        }
    }

    private async Task HandleK16_ExtPwr(int i)
    {
        if (i == 1)
        {
            _logger.LogDebug("K16: EXT PWR pressed");
            await _connector.SendCommandAsync(GetCommand("ExtPwr"));
        }
    }

    #endregion

    #region K17-K23: Fuel Pumps

    private async Task HandleK17_FuelPumpLTk1(int i)
    {
        if (i == 1)
        {
            _logger.LogDebug("K17: L TK PUMP 1 pressed");
            await _connector.SendCommandAsync(GetCommand("FuelPumpLTk1"));
        }
    }

    private async Task HandleK18_FuelPumpLTk2(int i)
    {
        if (i == 1)
        {
            _logger.LogDebug("K18: L TK PUMP 2 pressed");
            await _connector.SendCommandAsync(GetCommand("FuelPumpLTk2"));
        }
    }

    private async Task HandleK19_FuelPumpCtrL(int i)
    {
        if (i == 1)
        {
            _logger.LogDebug("K19: CTR L pressed");
            await _connector.SendCommandAsync(GetCommand("FuelPumpCtrL"));
        }
    }

    private async Task HandleK20_XFeed(int i)
    {
        if (i == 1)
        {
            _logger.LogDebug("K20: X FEED pressed");
            await _connector.SetDataRefValueAsync(GetDataRefPath("XFeedValve"), 
                _xFeedValve == 1 ? 0 : 1);
        }
    }

    private async Task HandleK21_FuelPumpCtrR(int i)
    {
        if (i == 1)
        {
            _logger.LogDebug("K21: CTR R pressed");
            await _connector.SendCommandAsync(GetCommand("FuelPumpCtrR"));
        }
    }

    private async Task HandleK22_FuelPumpRTk1(int i)
    {
        if (i == 1)
        {
            _logger.LogDebug("K22: R TK PUMP 1 pressed");
            await _connector.SendCommandAsync(GetCommand("FuelPumpRTk1"));
        }
    }

    private async Task HandleK23_FuelPumpRTk2(int i)
    {
        if (i == 1)
        {
            _logger.LogDebug("K23: R TK PUMP 2 pressed");
            await _connector.SendCommandAsync(GetCommand("FuelPumpRTk2"));
        }
    }

    #endregion

    // ========================================================================
    // TOGGLE SWITCH HANDLERS (T01-T12)
    // ========================================================================

    #region T01-T04: Strobe, Beacon, Wing, Nav

    private async Task HandleT01_Strobe(int i)
    {
        int value = i switch
        {
            0 => 0,  // OFF
            1 => 2,  // ON
            2 => 1,  // AUTO
            _ => 0
        };
        _logger.LogDebug("T01: STROBE set to {Position}", i);
        await _connector.SetDataRefValueAsync(GetDataRefPath("LightStrobe"), value);
    }

    private async Task HandleT02_Beacon(int i)
    {
        _logger.LogDebug("T02: BEACON set to {Position}", i);
        await _connector.SetDataRefValueAsync(GetDataRefPath("LightBeacon"), i == 1  ? 1 : 0);
    }

    private async Task HandleT03_Wing(int i)
    {
        _logger.LogDebug("T03: WING LIGHT set to {Position}", i);
        await _connector.SetDataRefValueAsync(GetDataRefPath("LightWing"), i == 1 ? 1 : 0);
    }

    private async Task HandleT04_Nav(int i)
    {
        int value = i switch
        {
            0 => 0,  // OFF
            1 => 2,  // 2
            2 => 1,  // 1
            _ => 0
        };
        _logger.LogDebug("T04: NAV set to {Position}", i);
        await _connector.SetDataRefValueAsync(GetDataRefPath("LightNav"), value);
    }

    #endregion

    #region T05-T08: Rwy, Land L, Land R, Nose

    private async Task HandleT05_Rwy(int i)
    {
        _logger.LogDebug("T05: RWY TURNOFF set to {Position}", i);
        await _connector.SetDataRefValueAsync(GetDataRefPath("LightRwy"), i == 1 ? 1 : 0);
    }

    private async Task HandleT06_LandL(int i)
    {
        int value = i switch
        {
            0 => 0,  // RETRACT
            1 => 2,  // ON
            2 => 1,  // OFF
            _ => 0
        };
        _logger.LogDebug("T06: LAND L set to {Position}", i);
        await _connector.SetDataRefValueAsync(GetDataRefPath("LightLandL"), value);
    }

    private async Task HandleT07_LandR(int i)
    {
        int value = i switch
        {
            0 => 0,  // RETRACT
            1 => 2,  // ON
            2 => 1,  // OFF
            _ => 0
        };
        _logger.LogDebug("T07: LAND R set to {Position}", i);
        await _connector.SetDataRefValueAsync(GetDataRefPath("LightLandR"), value);
    }

    private async Task HandleT08_Nose(int i)
    {
        int value = i switch
        {
            0 => 0,  // OFF
            1 => 2,  // T.O.
            2 => 1,  // TAXI
            _ => 0
        };
        _logger.LogDebug("T08: NOSE set to {Position}", i);
        await _connector.SetDataRefValueAsync(GetDataRefPath("LightNose"), value);
    }

    #endregion

    #region T09-T12: Seat, No Smoke, Exit, Dome

    private async Task HandleT09_Seat(int i)
    {
        _logger.LogDebug("T09: SEATBELT set to {Position}", i);
        await _connector.SetDataRefValueAsync(GetDataRefPath("LightSeat"), i == 1 ? 1 : 0);
    }

    private async Task HandleT10_NoSmoke(int i)
    {
        int value = i switch
        {
            0 => 0,  // OFF
            1 => 2,  // ON
            2 => 1,  // AUTO
            _ => 0
        };
        _logger.LogDebug("T10: NO SMOKE set to {Position}", i);
        await _connector.SetDataRefValueAsync(GetDataRefPath("LightNoSmoke"), value);
    }

    private async Task HandleT11_Exit(int i)
    {
        int value = i switch
        {
            0 => 0,  // OFF
            1 => 2,  // ON
            2 => 1,  // ARM
            _ => 0
        };
        _logger.LogDebug("T11: EXIT LT set to {Position}", i);
        await _connector.SetDataRefValueAsync(GetDataRefPath("LightExit"), value);
    }

    private async Task HandleT12_Dome(int i)
    {
        int value = i switch
        {
            0 => 0,  // OFF
            1 => 2,  // BRT
            2 => 1,  // DIM
            _ => 0
        };
        _logger.LogDebug("T12: DOME set to {Position}", i);
        await _connector.SetDataRefValueAsync(GetDataRefPath("LightDome"), value);
    }

    #endregion

    // ========================================================================
    // ROTARY KNOB HANDLERS (R01-R04)
    // ========================================================================

    #region R01-R04: ADIRS Rotaries, Wiper

    private async Task HandleR01_Nav1(int i)
    {
        int value = i switch
        {
            0 => 0,  // OFF
            1 => 2,  // NAV
            2 => 1,  // ATT
            _ => 0
        };
        _logger.LogDebug("R01: NAV1 set to {Position}", i);
        await _connector.SetDataRefValueAsync(GetDataRefPath("AdirsNav1"), value);
    }

    private async Task HandleR02_Nav3(int i)
    {
        int value = i switch
        {
            0 => 0,  // OFF
            1 => 2,  // NAV
            2 => 1,  // ATT
            _ => 0
        };
        _logger.LogDebug("R02: NAV3 set to {Position}", i);
        await _connector.SetDataRefValueAsync(GetDataRefPath("AdirsNav3"), value);
    }

    private async Task HandleR03_Nav2(int i)
    {
        int value = i switch
        {
            0 => 0,  // OFF
            1 => 2,  // NAV
            2 => 1,  // ATT
            _ => 0
        };
        _logger.LogDebug("R03: NAV2 set to {Position}", i);
        await _connector.SetDataRefValueAsync(GetDataRefPath("AdirsNav2"), value);
    }

    private async Task HandleR04_Wiper(int i)
    {
        int value = i switch
        {
            0 => 2,  // FAST
            1 => 0,  // OFF
            2 => 1,  // SLOW
            _ => 0
        };
        _logger.LogDebug("R04: WIPER set to {Position}", i);
        await _connector.SetDataRefValueAsync(GetDataRefPath("Wiper"), value);
    }

    #endregion

    // ========================================================================
    // TEST BUTTON HANDLERS (B01-B04)
    // ========================================================================

    #region B01-B04: Fire Test, Call All

    private async Task HandleB01_TestEng1(int i)
    {
        _logger.LogDebug("B01: TEST ENG1 {State}", i == 1 ? "pressed" : "released");

        string commandPath = i == 1
            ? GetCommand("TestEng1_Begin") 
            : GetCommand("TestEng1_End");
        await _connector.SendCommandAsync(commandPath);
    }

    private async Task HandleB02_TestApu(int i)
    {
        _logger.LogDebug("B02: TEST APU {State}", i == 1 ? "pressed" : "released");

        string commandPath = i == 1
            ? GetCommand("TestApu_Begin") 
            : GetCommand("TestApu_End");
        await _connector.SendCommandAsync(commandPath);
    }

    private async Task HandleB03_TestEng2(int i)
    {
        _logger.LogDebug("B03: TEST ENG2 {State}", i == 1 ? "pressed" : "released");

        string commandPath = i == 1
            ? GetCommand("TestEng2_Begin") 
            : GetCommand("TestEng2_End");
        await _connector.SendCommandAsync(commandPath);
    }

    private async Task HandleB04_CallAll(int i)
    {
        _logger.LogDebug("B04: CALL ALL {State}", i == 1 ? "pressed" : "released");
        if (i == 1)
            await _connector.SendCommandAsync(GetCommand("CallAll"));
    }

    #endregion

    // ========================================================================
    // PANEL BRIGHTNESS HANDLER
    // ========================================================================

    private async Task HandleOVHD_Brightness(int i)
    {
        float value = (float)(i / 10.0);
        _logger.LogDebug("OVHD: Brightness set to {Value}", value);
        await _connector.SetDataRefValueAsync(GetDataRefPath("OhpBrightness"), value);
    }

    //return a string with a '.' between each digit of the version number, e.g. 1234 -> "1.2.3.4"
    private string VersionString(int version)
    {
        string versionStr = version.ToString("F0"); // Convert to string without decimal places
        return string.Join('.', versionStr.Select(c => c.ToString()));
    }
}
