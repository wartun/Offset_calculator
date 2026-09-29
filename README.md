# Battery Current Offset Calibration (Mission Planner plugin)

Calibrates the ArduPilot analog current sensor offset `BATT_AMP_OFFSET` against an external
ammeter. Written for Mission Planner 1.3.83 and ArduCopter 4.6.x.

ArduPilot's analog monitor computes `current = (pin_voltage - BATT_AMP_OFFSET) * BATT_AMP_PERVLT`,
so the offset that makes the reported current match the meter is

    new_offset_V = old_offset_V + (reported_current_A - actual_current_A) / BATT_AMP_PERVLT

Example: `0.054 + (-1.6 - 0.3) / 180 = 0.04344 V` moves the reading from -1.6 A to 0.3 A.

What the plugin does:

1. Checks that Mission Planner is connected and `BATT_MONITOR = 4` (analog voltage and current).
2. Reads `BATT_AMP_OFFSET` and `BATT_AMP_PERVLT` fresh from the vehicle, and averages about 2 s of
   fresh battery 1 current telemetry (`SYS_STATUS` / `BATTERY_STATUS` id 0).
3. Asks for the current measured with your meter (`0.3` or `0,3`).
4. Shows the proposed offset and waits for your confirmation.
5. Writes only the existing `BATT_AMP_OFFSET` (`MAVLinkInterface.setParam`), reads it back
   (`GetParam`) to verify it, then shows the newly reported current.

If the vehicle is not connected, telemetry is missing, a parameter is missing, `BATT_MONITOR` is not 4,
or `BATT_AMP_PERVLT` is not a positive number, the plugin shows an error and writes nothing.
`BATT_AMP_PERVLT` is never changed.

## Install

Use **one** of the two options, not both (you would get two menu entries).

### Option A: source file (no build needed)

1. Close Mission Planner.
2. Copy `BattAmpOffsetCal.cs` into `C:\Program Files (x86)\Mission Planner\plugins\`
   (Windows asks for administrator rights).
3. Start Mission Planner. It compiles `.cs` plugins at start-up.

### Option B: DLL

Build with the .NET SDK (6 or newer) or Visual Studio 2022 on the PC where Mission Planner is installed:

```
cd Offset_calculator
dotnet build -c Release
```

If Mission Planner is not in `C:\Program Files (x86)\Mission Planner`, pass its folder:

```
dotnet build -c Release -p:MissionPlannerDir="D:\Mission Planner"
```

Close Mission Planner, copy `bin\Release\net472\BattAmpOffsetCal.dll` into the Mission Planner
`plugins` folder, then start Mission Planner. Rebuild after updating Mission Planner.

### Check that it loaded

Press **Ctrl+P** in Mission Planner. "Battery Current Offset Calibration" should be in the plugin
list. For option A, compile errors are listed in the same window.

## Use

**DATA** (Flight Data) screen → right-click the HUD → **Battery Current Offset Calibration...**

## Test procedure (bench, propellers removed)

1. Power the vehicle through its power module with the external ammeter measuring the same battery
   current. Connect Mission Planner and wait for the parameter download to finish.
2. In **CONFIG → Full Parameter List**, note `BATT_AMP_OFFSET` and `BATT_AMP_PERVLT`.
3. Open the plugin. Check that the dialog shows the same two values, and a reported current close
   to the HUD value.
4. Keep the load steady (disarmed). Type the meter reading, e.g. `0,3`, and press **Calculate...**.
   Check the proposed value against the formula. With your screenshot values (0.054 V, 180 A/V,
   -1.6 A reported, 0.3 A measured) it proposes about 0.0434 V.
5. Answer **No** and confirm the status says "Cancelled. Nothing was changed". Then press
   **Calculate...** again and answer **Yes**.
6. The result box must say "written and verified". The reported current should now be close to the
   meter reading.
7. In **Full Parameter List**, press **Refresh Params**. `BATT_AMP_OFFSET` shows the new value,
   `BATT_AMP_PERVLT` is unchanged, and no new parameter appeared. The HUD current matches the meter.
8. Error checks: type `abc` and press Calculate (input error, nothing written). Disconnect, open the
   plugin and check that it reports "not connected".

Optional dry run in SITL (**SIMULATION** screen, if `BATT_MONITOR = 4` there):

1. Note the reported current I0.
2. Increase `BATT_AMP_OFFSET` by 0.05 V. The reading drops by 0.05 × `BATT_AMP_PERVLT`.
3. Enter I0 as the actual current. The plugin should propose the original offset, and the reading
   returns to I0.
